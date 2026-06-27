from __future__ import annotations

import json
import sqlite3
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional, Tuple

from Grocery_Sense.data.connection import connection_scope, get_connection


def _now_utc_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


# -----------------------------------------------------------------------------
# Schema helpers (safe, additive)
# -----------------------------------------------------------------------------

def ensure_receipt_support_tables() -> None:
    """No-op: canonical DDL now lives in data/schema.py:create_tables.
    Retained for backwards-compatibility with existing call sites."""
    return None


# -----------------------------------------------------------------------------
# Queries (recent receipts, receipt details, line items, raw json)
# -----------------------------------------------------------------------------

def list_recent_receipts(
    limit: int = 50,
    offset: int = 0,
    store_id: Optional[int] = None,
    since: Optional[str] = None,
    until: Optional[str] = None,
) -> List[Dict[str, Any]]:
    """
    Returns recent receipts with store name + line item count.

    Optional filters: store_id, and a purchase_date range (since/until, ISO
    YYYY-MM-DD strings, inclusive).
    """
    ensure_receipt_support_tables()

    where: List[str] = []
    params: List[Any] = []
    if store_id is not None:
        where.append("r.store_id = ?")
        params.append(int(store_id))
    if since:
        where.append("r.purchase_date >= ?")
        params.append(str(since))
    if until:
        where.append("r.purchase_date <= ?")
        params.append(str(until))
    where_sql = ("WHERE " + " AND ".join(where)) if where else ""
    params.extend([int(limit), int(offset)])

    with connection_scope() as conn:
        rows = conn.execute(
            f"""
            SELECT
                r.id,
                r.purchase_date,
                r.total_amount,
                r.subtotal_amount,
                r.tax_amount,
                r.store_id,
                COALESCE(s.name, '') AS store_name,
                r.file_path,
                r.created_at,
                COALESCE(li.cnt, 0) AS item_count
            FROM receipts r
            LEFT JOIN stores s ON s.id = r.store_id
            LEFT JOIN (
                SELECT receipt_id, COUNT(1) AS cnt
                FROM receipt_line_items
                GROUP BY receipt_id
            ) li ON li.receipt_id = r.id
            {where_sql}
            ORDER BY r.id DESC
            LIMIT ? OFFSET ?;
            """,
            tuple(params),
        ).fetchall()

    out: List[Dict[str, Any]] = []
    for r in rows:
        out.append(
            {
                "id": int(r[0]),
                "purchase_date": r[1],
                "total_amount": r[2],
                "subtotal_amount": r[3],
                "tax_amount": r[4],
                "store_id": r[5],
                "store_name": r[6],
                "file_path": r[7],
                "created_at": r[8],
                "item_count": int(r[9] or 0),
            }
        )
    return out


def get_receipt(receipt_id: int) -> Optional[Dict[str, Any]]:
    ensure_receipt_support_tables()

    with connection_scope() as conn:
        r = conn.execute(
            """
            SELECT
                r.id,
                r.purchase_date,
                r.total_amount,
                r.subtotal_amount,
                r.tax_amount,
                r.store_id,
                COALESCE(s.name, '') AS store_name,
                r.file_path,
                r.source,
                r.azure_request_id,
                r.created_at
            FROM receipts r
            LEFT JOIN stores s ON s.id = r.store_id
            WHERE r.id = ?;
            """,
            (int(receipt_id),),
        ).fetchone()

    if not r:
        return None

    return {
        "id": int(r[0]),
        "purchase_date": r[1],
        "total_amount": r[2],
        "subtotal_amount": r[3],
        "tax_amount": r[4],
        "store_id": r[5],
        "store_name": r[6],
        "file_path": r[7],
        "source": r[8],
        "azure_request_id": r[9],
        "created_at": r[10],
    }


def list_receipt_line_items(receipt_id: int) -> List[Dict[str, Any]]:
    ensure_receipt_support_tables()

    with connection_scope() as conn:
        rows = conn.execute(
            """
            SELECT
                li.id,
                li.line_index,
                li.item_id,
                COALESCE(i.canonical_name, '') AS canonical_name,
                COALESCE(li.description, '') AS description,
                li.quantity,
                li.unit_price,
                li.line_total,
                li.discount,
                li.confidence
            FROM receipt_line_items li
            LEFT JOIN items i ON i.id = li.item_id
            WHERE li.receipt_id = ?
            ORDER BY li.line_index ASC;
            """,
            (int(receipt_id),),
        ).fetchall()

    out: List[Dict[str, Any]] = []
    for r in rows:
        out.append(
            {
                "id": int(r[0]),
                "line_index": int(r[1]),
                "item_id": r[2],
                "canonical_name": r[3],
                "description": r[4],
                "quantity": r[5],
                "unit_price": r[6],
                "line_total": r[7],
                "discount": r[8],
                "confidence": r[9],
            }
        )
    return out


def get_receipt_raw_json(receipt_id: int) -> Tuple[Optional[str], Optional[str]]:
    """
    Returns (raw_json, json_path)
    """
    ensure_receipt_support_tables()

    with connection_scope() as conn:
        row = conn.execute(
            """
            SELECT raw_json, json_path
            FROM receipt_raw_json
            WHERE receipt_id = ?;
            """,
            (int(receipt_id),),
        ).fetchone()

    if not row:
        return None, None
    return row[0], row[1]


# -----------------------------------------------------------------------------
# Spend aggregation
# -----------------------------------------------------------------------------

def get_month_spend(year_month: str) -> Dict[str, Any]:
    """
    Return total spend and receipt count for a given month ('YYYY-MM').
    """
    with connection_scope() as conn:
        row = conn.execute(
            """
            SELECT
                COALESCE(SUM(total_amount), 0.0) AS total,
                COUNT(*) AS receipt_count
            FROM receipts
            WHERE STRFTIME('%Y-%m', purchase_date) = ?
              AND total_amount IS NOT NULL;
            """,
            (year_month,),
        ).fetchone()
    total = float(row[0]) if row and row[0] is not None else 0.0
    count = int(row[1]) if row and row[1] is not None else 0
    return {"month": year_month, "total": total, "receipt_count": count}


def get_spend_trend(months: int = 12) -> List[Dict[str, Any]]:
    """
    Return monthly spend totals for the last N months, oldest first.
    Each entry: {month: 'YYYY-MM', total: float, receipt_count: int}.
    """
    months = max(1, int(months))
    with connection_scope() as conn:
        rows = conn.execute(
            """
            SELECT
                STRFTIME('%Y-%m', purchase_date) AS month,
                COALESCE(SUM(total_amount), 0.0) AS total,
                COUNT(*) AS receipt_count
            FROM receipts
            WHERE total_amount IS NOT NULL
              AND purchase_date >= DATE('now', ? || ' months')
            GROUP BY month
            ORDER BY month ASC;
            """,
            (f"-{months}",),
        ).fetchall()
    return [
        {"month": r[0], "total": float(r[1]), "receipt_count": int(r[2])}
        for r in rows
    ]


# -----------------------------------------------------------------------------
# Safe cascade delete + Undo backup/restore
# -----------------------------------------------------------------------------

def delete_receipt_cascade(receipt_id: int) -> None:
    """
    Hard delete a receipt and all derived rows that reference it.
    This is SAFE (won’t error if optional tables exist; assumes they do).
    """
    ensure_receipt_support_tables()

    with connection_scope() as conn:
        # Child -> parent order
        conn.execute("DELETE FROM prices WHERE receipt_id = ?;", (int(receipt_id),))
        conn.execute("DELETE FROM receipt_line_items WHERE receipt_id = ?;", (int(receipt_id),))
        conn.execute("DELETE FROM receipt_raw_json WHERE receipt_id = ?;", (int(receipt_id),))
        conn.execute("DELETE FROM receipt_file_hashes WHERE receipt_id = ?;", (int(receipt_id),))
        conn.execute("DELETE FROM receipt_signatures WHERE receipt_id = ?;", (int(receipt_id),))
        conn.execute("DELETE FROM receipts WHERE id = ?;", (int(receipt_id),))
        conn.commit()


def delete_receipt_with_backup(receipt_id: int) -> int:
    """
    Deletes receipt with a backup snapshot stored for Undo.
    Returns backup_id.
    """
    ensure_receipt_support_tables()

    snapshot = _snapshot_receipt(receipt_id)
    if snapshot is None:
        raise ValueError(f"Receipt not found: {receipt_id}")

    backup_json = json.dumps(snapshot, ensure_ascii=False)
    # Write-time cap: if the snapshot (dominated by the OCR raw_json) exceeds the
    # restore limit, drop the heavy blob so the backup stays restorable. Structured
    # rows (line items / prices) are still preserved.
    if len(backup_json) > _MAX_BACKUP_BYTES:
        snapshot["raw_json"] = None
        backup_json = json.dumps(snapshot, ensure_ascii=False)

    with connection_scope() as conn:
        cur = conn.execute(
            """
            INSERT INTO deleted_receipt_backups (original_receipt_id, deleted_at, backup_json)
            VALUES (?, ?, ?);
            """,
            (int(receipt_id), _now_utc_iso(), backup_json),
        )
        backup_id = int(cur.lastrowid)

        # Now delete
        conn.execute("DELETE FROM prices WHERE receipt_id = ?;", (int(receipt_id),))
        conn.execute("DELETE FROM receipt_line_items WHERE receipt_id = ?;", (int(receipt_id),))
        conn.execute("DELETE FROM receipt_raw_json WHERE receipt_id = ?;", (int(receipt_id),))
        conn.execute("DELETE FROM receipt_file_hashes WHERE receipt_id = ?;", (int(receipt_id),))
        conn.execute("DELETE FROM receipt_signatures WHERE receipt_id = ?;", (int(receipt_id),))
        conn.execute("DELETE FROM receipts WHERE id = ?;", (int(receipt_id),))

        # Retention: keep only the newest _MAX_BACKUPS_KEPT undo snapshots so the
        # backup table (which embeds full OCR JSON) doesn't grow unbounded. Runs in
        # the same transaction; the row just inserted has the highest id and is kept.
        conn.execute(
            "DELETE FROM deleted_receipt_backups WHERE id NOT IN ("
            "SELECT id FROM deleted_receipt_backups ORDER BY id DESC LIMIT ?)",
            (_MAX_BACKUPS_KEPT,),
        )
        conn.commit()

    return backup_id


_MAX_BACKUP_BYTES = 50 * 1024 * 1024
# Keep only the most recent N undo snapshots (each may embed full OCR raw_json).
_MAX_BACKUPS_KEPT = 50


def restore_receipt_from_backup(backup_id: int) -> Tuple[int, List[Tuple[str, str]]]:
    """
    Undo delete: restores snapshot into DB as a NEW receipt row (new receipt_id),
    then restores raw_json, line items, prices, and dedupe keys linked to the NEW id.

    Returns (new_receipt_id, conflicts) where conflicts is a list of
    (kind, key) for dedupe rows that already mapped to a different receipt and
    were therefore skipped (kind in {"signature", "file_hash"}).
    """
    ensure_receipt_support_tables()

    with connection_scope() as conn:
        row = conn.execute(
            "SELECT backup_json FROM deleted_receipt_backups WHERE id = ?;",
            (int(backup_id),),
        ).fetchone()

    if not row:
        raise ValueError(f"Backup not found: {backup_id}")

    raw_text = row[0] or ""
    if len(raw_text) > _MAX_BACKUP_BYTES:
        raise ValueError(f"Backup {backup_id} is too large to restore safely")

    snapshot = json.loads(raw_text)
    if not isinstance(snapshot, dict) or not isinstance(snapshot.get("receipt"), dict):
        raise ValueError(f"Backup {backup_id} is corrupt: missing 'receipt' key")
    for key in ("line_items", "prices", "file_hashes", "signatures"):
        val = snapshot.get(key)
        if val is not None and not isinstance(val, list):
            raise ValueError(f"Backup {backup_id} is corrupt: '{key}' must be a list")

    rec = snapshot["receipt"]
    conflicts: List[Tuple[str, str]] = []

    with connection_scope() as conn:
        conn.execute("BEGIN;")
        try:
            cur = conn.execute(
                """
                INSERT INTO receipts (
                    store_id, purchase_date, subtotal_amount, tax_amount, total_amount,
                    source, file_path, image_overall_confidence, keep_image_until,
                    azure_request_id, created_at
                )
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                """,
                (
                    rec.get("store_id"),
                    rec.get("purchase_date"),
                    rec.get("subtotal_amount"),
                    rec.get("tax_amount"),
                    rec.get("total_amount"),
                    rec.get("source"),
                    rec.get("file_path"),
                    rec.get("image_overall_confidence"),
                    rec.get("keep_image_until"),
                    rec.get("azure_request_id"),
                    rec.get("created_at") or _now_utc_iso(),
                ),
            )
            new_receipt_id = int(cur.lastrowid)

            raw = snapshot.get("raw_json")
            if raw:
                conn.execute(
                    """
                    INSERT OR REPLACE INTO receipt_raw_json (receipt_id, operation_id, json_path, raw_json, created_at)
                    VALUES (?, ?, ?, ?, ?);
                    """,
                    (
                        new_receipt_id,
                        raw.get("operation_id"),
                        raw.get("json_path"),
                        raw.get("raw_json"),
                        raw.get("created_at") or _now_utc_iso(),
                    ),
                )

            line_rows = [
                (
                    new_receipt_id,
                    li.get("line_index"),
                    li.get("item_id"),
                    li.get("description"),
                    li.get("quantity"),
                    li.get("unit_price"),
                    li.get("line_total"),
                    li.get("discount"),
                    li.get("confidence"),
                    li.get("created_at") or _now_utc_iso(),
                )
                for li in snapshot.get("line_items", []) or []
            ]
            if line_rows:
                conn.executemany(
                    """
                    INSERT INTO receipt_line_items (
                        receipt_id, line_index, item_id, description, quantity,
                        unit_price, line_total, discount, confidence, created_at
                    )
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                    """,
                    line_rows,
                )

            price_rows: List[Tuple[Any, ...]] = []
            for p in snapshot.get("prices", []) or []:
                fsid = p.get("flyer_source_id")
                if fsid is not None:
                    ok = conn.execute(
                        "SELECT 1 FROM flyer_sources WHERE id = ?;",
                        (int(fsid),),
                    ).fetchone()
                    if not ok:
                        fsid = None
                price_rows.append((
                    p.get("item_id"),
                    p.get("store_id"),
                    new_receipt_id,
                    fsid,
                    p.get("source"),
                    p.get("date"),
                    p.get("unit_price"),
                    p.get("unit"),
                    p.get("quantity"),
                    p.get("total_price"),
                    p.get("raw_name"),
                    p.get("confidence"),
                    p.get("created_at") or _now_utc_iso(),
                ))
            if price_rows:
                conn.executemany(
                    """
                    INSERT INTO prices (
                        item_id, store_id, receipt_id, flyer_source_id, source, date,
                        unit_price, unit, quantity, total_price, raw_name, confidence, created_at
                    )
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                    """,
                    price_rows,
                )

            # Dedupe keys: use INSERT (not REPLACE) so we don't silently
            # overwrite another receipt's existing dedupe row.
            for fh in snapshot.get("file_hashes", []) or []:
                fh_val = fh.get("file_hash")
                if not fh_val:
                    continue
                try:
                    conn.execute(
                        "INSERT INTO receipt_file_hashes (file_hash, receipt_id, file_path, created_at) VALUES (?, ?, ?, ?);",
                        (fh_val, new_receipt_id, fh.get("file_path"), fh.get("created_at") or _now_utc_iso()),
                    )
                except sqlite3.IntegrityError:
                    conflicts.append(("file_hash", str(fh_val)))

            for sig in snapshot.get("signatures", []) or []:
                sig_val = sig.get("signature")
                if not sig_val:
                    continue
                try:
                    conn.execute(
                        "INSERT INTO receipt_signatures (signature, receipt_id, created_at) VALUES (?, ?, ?);",
                        (sig_val, new_receipt_id, sig.get("created_at") or _now_utc_iso()),
                    )
                except sqlite3.IntegrityError:
                    conflicts.append(("signature", str(sig_val)))

            conn.commit()
        except Exception:
            conn.rollback()
            raise

    return new_receipt_id, conflicts


def list_deleted_backups(limit: int = 25) -> List[Dict[str, Any]]:
    ensure_receipt_support_tables()
    with connection_scope() as conn:
        rows = conn.execute(
            """
            SELECT id, original_receipt_id, deleted_at
            FROM deleted_receipt_backups
            ORDER BY id DESC
            LIMIT ?;
            """,
            (int(limit),),
        ).fetchall()

    return [
        {"backup_id": int(r[0]), "original_receipt_id": int(r[1]) if r[1] is not None else None, "deleted_at": r[2]}
        for r in rows
    ]


# -----------------------------------------------------------------------------
# Snapshot internals
# -----------------------------------------------------------------------------

def _snapshot_receipt(receipt_id: int) -> Optional[Dict[str, Any]]:
    """
    Snapshot receipt + derived tables into a JSON-able structure.
    """
    with connection_scope() as conn:
        rec = conn.execute(
            """
            SELECT
                id, store_id, purchase_date, subtotal_amount, tax_amount, total_amount,
                source, file_path, image_overall_confidence, keep_image_until, azure_request_id, created_at
            FROM receipts
            WHERE id = ?;
            """,
            (int(receipt_id),),
        ).fetchone()

        if not rec:
            return None

        raw = conn.execute(
            """
            SELECT receipt_id, operation_id, json_path, raw_json, created_at
            FROM receipt_raw_json
            WHERE receipt_id = ?;
            """,
            (int(receipt_id),),
        ).fetchone()

        line_items = conn.execute(
            """
            SELECT receipt_id, line_index, item_id, description, quantity, unit_price, line_total, discount, confidence, created_at
            FROM receipt_line_items
            WHERE receipt_id = ?
            ORDER BY line_index ASC;
            """,
            (int(receipt_id),),
        ).fetchall()

        prices = conn.execute(
            """
            SELECT
                item_id, store_id, receipt_id, flyer_source_id, source, date,
                unit_price, unit, quantity, total_price, raw_name, confidence, created_at
            FROM prices
            WHERE receipt_id = ?;
            """,
            (int(receipt_id),),
        ).fetchall()

        file_hashes = conn.execute(
            """
            SELECT file_hash, receipt_id, file_path, created_at
            FROM receipt_file_hashes
            WHERE receipt_id = ?;
            """,
            (int(receipt_id),),
        ).fetchall()

        signatures = conn.execute(
            """
            SELECT signature, receipt_id, created_at
            FROM receipt_signatures
            WHERE receipt_id = ?;
            """,
            (int(receipt_id),),
        ).fetchall()

    snapshot: Dict[str, Any] = {
        "receipt": {
            "id": int(rec[0]),
            "store_id": rec[1],
            "purchase_date": rec[2],
            "subtotal_amount": rec[3],
            "tax_amount": rec[4],
            "total_amount": rec[5],
            "source": rec[6],
            "file_path": rec[7],
            "image_overall_confidence": rec[8],
            "keep_image_until": rec[9],
            "azure_request_id": rec[10],
            "created_at": rec[11],
        },
        "raw_json": None,
        "line_items": [],
        "prices": [],
        "file_hashes": [],
        "signatures": [],
    }

    if raw:
        snapshot["raw_json"] = {
            "receipt_id": raw[0],
            "operation_id": raw[1],
            "json_path": raw[2],
            "raw_json": raw[3],
            "created_at": raw[4],
        }

    for li in line_items:
        snapshot["line_items"].append(
            {
                "receipt_id": li[0],
                "line_index": li[1],
                "item_id": li[2],
                "description": li[3],
                "quantity": li[4],
                "unit_price": li[5],
                "line_total": li[6],
                "discount": li[7],
                "confidence": li[8],
                "created_at": li[9],
            }
        )

    for p in prices:
        snapshot["prices"].append(
            {
                "item_id": p[0],
                "store_id": p[1],
                "receipt_id": p[2],
                "flyer_source_id": p[3],
                "source": p[4],
                "date": p[5],
                "unit_price": p[6],
                "unit": p[7],
                "quantity": p[8],
                "total_price": p[9],
                "raw_name": p[10],
                "confidence": p[11],
                "created_at": p[12],
            }
        )

    for fh in file_hashes:
        snapshot["file_hashes"].append(
            {
                "file_hash": fh[0],
                "receipt_id": fh[1],
                "file_path": fh[2],
                "created_at": fh[3],
            }
        )

    for sig in signatures:
        snapshot["signatures"].append(
            {
                "signature": sig[0],
                "receipt_id": sig[1],
                "created_at": sig[2],
            }
        )

    return snapshot
