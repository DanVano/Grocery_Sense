from __future__ import annotations

import sqlite3
from contextlib import closing
from datetime import datetime, timedelta, timezone
from typing import Iterable, List, Optional, Tuple, Dict, Any

from Grocery_Sense.data.connection import get_connection
from Grocery_Sense.domain.models import PricePoint, PriceStats


# SQLite default SQLITE_MAX_VARIABLE_NUMBER is 999 on older builds, 32766 on
# 3.32+. Chunk to 900 so we stay well under the floor regardless of build.
_SQL_PARAM_CHUNK = 900


def _coerce_id_list(ids: Iterable[int]) -> List[int]:
    return [int(x) for x in ids if int(x) > 0]


def _chunks(seq: List[int], size: int = _SQL_PARAM_CHUNK):
    for i in range(0, len(seq), size):
        yield seq[i:i + size]


def add_price_point(
    item_id: int,
    store_id: int,
    unit_price: float,
    unit: str,
    quantity: Optional[float] = None,
    total_price: Optional[float] = None,
    raw_name: Optional[str] = None,
    confidence: Optional[int] = None,
    source: str = "manual",
    date: Optional[str] = None,
    receipt_id: Optional[int] = None,
    flyer_source_id: Optional[int] = None,
) -> int:
    """
    Inserts a new price point into the prices table.
    Returns inserted row id.
    """
    if date is None:
        date = datetime.now().strftime("%Y-%m-%d")

    with closing(get_connection()) as conn:
        cur = conn.cursor()
        cur.execute(
            """
            INSERT INTO prices (
                item_id, store_id, receipt_id, flyer_source_id, source, date,
                unit_price, unit, quantity, total_price, raw_name, confidence
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                item_id,
                store_id,
                receipt_id,
                flyer_source_id,
                source,
                date,
                unit_price,
                unit,
                quantity,
                total_price,
                raw_name,
                confidence,
            ),
        )
        conn.commit()
        return int(cur.lastrowid)


def get_prices_for_item(
    item_id: int,
    store_id: Optional[int] = None,
    since_days: int = 365,
    *,
    limit: Optional[int] = None,
) -> List[PricePoint]:
    """
    Returns price points for an item, optionally filtered by store.

    When `limit` is provided the query fetches the most-recent N rows
    (DESC + LIMIT) and reverses them so the caller still sees ASC order
    — preserving the no-limit contract.
    """
    # Compare the stored ISO date directly (no date() wrapper) so the
    # idx_prices_item_date / idx_prices_item_store_date indexes can serve the
    # range. Dates are zero-padded YYYY-MM-DD, so lexical >= is chronological >=.
    cutoff = (datetime.now(timezone.utc).date() - timedelta(days=int(since_days))).isoformat()
    sql = """
        SELECT id, item_id, store_id, receipt_id, flyer_source_id, source, date,
               unit_price, unit, quantity, total_price, raw_name, confidence,
               norm_unit_price, norm_unit
        FROM prices
        WHERE item_id = ? AND date >= ?
    """
    params: List[Any] = [item_id, cutoff]

    if store_id is not None:
        sql += " AND store_id = ?"
        params.append(store_id)

    if limit is None:
        sql += " ORDER BY date ASC"
    else:
        sql += " ORDER BY date DESC LIMIT ?"
        params.append(int(limit))

    out: List[PricePoint] = []
    with closing(get_connection()) as conn:
        cur = conn.execute(sql, params)
        rows = cur.fetchall()
        for r in rows:
            out.append(
                PricePoint(
                    id=r[0],
                    item_id=r[1],
                    store_id=r[2],
                    receipt_id=r[3],
                    flyer_source_id=r[4],
                    source=r[5],
                    date=r[6],
                    unit_price=r[7],
                    unit=r[8],
                    quantity=r[9],
                    total_price=r[10],
                    raw_name=r[11],
                    confidence=r[12],
                    norm_unit_price=r[13],
                    norm_unit=r[14],
                )
            )
    if limit is not None:
        # We fetched DESC to honour LIMIT; flip back to ASC for caller contract.
        out.reverse()
    return out


def get_most_recent_price(item_id: int, store_id: Optional[int] = None) -> Optional[PricePoint]:
    """
    Returns the most recent price point for item (optionally by store).
    """
    sql = """
        SELECT id, item_id, store_id, receipt_id, flyer_source_id, source, date,
               unit_price, unit, quantity, total_price, raw_name, confidence,
               norm_unit_price, norm_unit
        FROM prices
        WHERE item_id = ?
    """
    params: List[Any] = [item_id]

    if store_id is not None:
        sql += " AND store_id = ?"
        params.append(store_id)

    sql += " ORDER BY date DESC, id DESC LIMIT 1"

    with closing(get_connection()) as conn:
        row = conn.execute(sql, params).fetchone()
        if not row:
            return None
        return PricePoint(
            id=row[0],
            item_id=row[1],
            store_id=row[2],
            receipt_id=row[3],
            flyer_source_id=row[4],
            source=row[5],
            date=row[6],
            unit_price=row[7],
            unit=row[8],
            quantity=row[9],
            total_price=row[10],
            raw_name=row[11],
            confidence=row[12],
            norm_unit_price=row[13],
            norm_unit=row[14],
        )


def get_price_stats_for_item(item_id: int, store_id: Optional[int] = None, since_days: int = 365) -> PriceStats:
    """
    Returns basic stats for an item's price history. Computed via SQL aggregate.
    """
    cutoff = (datetime.now(timezone.utc).date() - timedelta(days=int(since_days))).isoformat()
    sql = (
        "SELECT MIN(unit_price), MAX(unit_price), AVG(unit_price), COUNT(*) "
        "FROM prices "
        "WHERE item_id = ? AND date >= ? AND unit_price IS NOT NULL"
    )
    params: List[Any] = [int(item_id), cutoff]
    if store_id is not None:
        sql += " AND store_id = ?"
        params.append(int(store_id))

    with closing(get_connection()) as conn:
        row = conn.execute(sql, params).fetchone()

    if not row or not row[3]:
        return PriceStats(item_id=item_id, store_id=store_id, min_price=None, max_price=None, avg_price=None, count=0)

    return PriceStats(
        item_id=item_id,
        store_id=store_id,
        min_price=float(row[0]),
        max_price=float(row[1]),
        avg_price=float(row[2]),
        count=int(row[3]),
    )


def add_price_points(rows: List[Tuple]) -> None:
    """Bulk insert price points via executemany.

    Each tuple matches: (item_id, store_id, receipt_id, flyer_source_id, source, date,
    unit_price, unit, quantity, total_price, raw_name, confidence).
    """
    if not rows:
        return
    with closing(get_connection()) as conn:
        conn.executemany(
            """
            INSERT INTO prices (
                item_id, store_id, receipt_id, flyer_source_id, source, date,
                unit_price, unit, quantity, total_price, raw_name, confidence
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            rows,
        )
        conn.commit()


# ---------- Advanced query helpers (Milestone 2: usual price + 6-mo low + staples) ----------

def _median(values: List[float]) -> Optional[float]:
    """Return the median of a list of floats (None if empty)."""
    if not values:
        return None
    vals = sorted(v for v in values if v is not None)
    if not vals:
        return None
    n = len(vals)
    mid = n // 2
    if n % 2 == 1:
        return float(vals[mid])
    return float((vals[mid - 1] + vals[mid]) / 2.0)


def _since_clause(days: int) -> str:
    # SQLite date modifier like '-180 day'
    days = int(max(1, days))
    return f"-{days} day"


def list_unit_prices(
    item_id: int,
    *,
    store_id: Optional[int] = None,
    since_days: int = 180,
    sources: Optional[List[str]] = None,
    receipt_only: bool = False,
    limit: Optional[int] = None,
) -> List[float]:
    """Return unit_price history for an item.

    Notes:
      - Uses COALESCE(date, created_at) for time filtering.
      - If receipt_only=True, filters to rows that look like receipt line-items.
    """
    sql = [
        "SELECT unit_price",
        "FROM prices",
        "WHERE item_id = ?",
        "  AND unit_price IS NOT NULL",
        "  AND unit_price > 0",
        "  AND date(COALESCE(date, created_at)) >= date('now', ?)",
    ]
    params: List[Any] = [int(item_id), _since_clause(since_days)]

    if store_id is not None:
        sql.append("  AND store_id = ?")
        params.append(int(store_id))

    if receipt_only:
        sql.append("  AND (source = 'receipt' OR receipt_id IS NOT NULL)")
    elif sources:
        placeholders = ",".join(["?"] * len(sources))
        sql.append(f"  AND source IN ({placeholders})")
        params.extend([str(s) for s in sources])

    sql.append("ORDER BY COALESCE(date, created_at) DESC")
    if limit:
        sql.append("LIMIT ?")
        params.append(int(limit))

    with closing(get_connection()) as conn:
        cur = conn.execute("\n".join(sql), params)
        return [float(r[0]) for r in cur.fetchall() if r and r[0] is not None]


def get_usual_unit_price(
    item_id: int,
    *,
    store_id: Optional[int] = None,
    receipt_only: bool = True,
    min_samples: int = 4,
    since_days: int = 180,
) -> Tuple[Optional[float], int, str]:
    """Compute a 'usual' unit price.

    Returns: (usual_price, sample_count, basis)
      basis: 'receipt_median' | 'estimated_median' | 'unknown'
    """
    prices = list_unit_prices(
        item_id,
        store_id=store_id,
        since_days=since_days,
        receipt_only=receipt_only,
    )
    if len(prices) >= int(min_samples):
        med = _median(prices)
        return (med, len(prices), "receipt_median" if receipt_only else "estimated_median")

    if receipt_only:
        fallback = list_unit_prices(
            item_id,
            store_id=store_id,
            since_days=since_days,
            receipt_only=False,
        )
        if fallback:
            return (_median(fallback), len(fallback), "estimated_median")

    return (None, len(prices), "unknown")


def get_six_month_low_unit_price(
    item_id: int,
    *,
    store_id: Optional[int] = None,
    since_days: int = 183,
) -> Tuple[Optional[float], Optional[str]]:
    """Return (lowest_unit_price, when_iso) within the lookback window."""
    sql = [
        "SELECT unit_price, COALESCE(date, created_at) AS when_iso",
        "FROM prices",
        "WHERE item_id = ?",
        "  AND unit_price IS NOT NULL",
        "  AND unit_price > 0",
        "  AND date(COALESCE(date, created_at)) >= date('now', ?)",
    ]
    params: List[Any] = [int(item_id), _since_clause(since_days)]

    if store_id is not None:
        sql.append("  AND store_id = ?")
        params.append(int(store_id))

    sql.append("ORDER BY unit_price ASC, when_iso ASC")
    sql.append("LIMIT 1")

    with closing(get_connection()) as conn:
        cur = conn.execute("\n".join(sql), params)
        row = cur.fetchone()
        if not row:
            return (None, None)
        return (float(row[0]), str(row[1]) if row[1] else None)


def get_last_seen_at_or_below(
    item_id: int,
    *,
    store_id: Optional[int] = None,
    price_ceiling: float,
    since_days: int = 183,
) -> Optional[str]:
    """Most recent date we saw unit_price <= price_ceiling (within lookback)."""
    sql = [
        "SELECT COALESCE(date, created_at) AS when_iso",
        "FROM prices",
        "WHERE item_id = ?",
        "  AND unit_price IS NOT NULL",
        "  AND unit_price > 0",
        "  AND unit_price <= ?",
        "  AND date(COALESCE(date, created_at)) >= date('now', ?)",
    ]
    params: List[Any] = [int(item_id), float(price_ceiling), _since_clause(since_days)]
    if store_id is not None:
        sql.append("  AND store_id = ?")
        params.append(int(store_id))
    sql.append("ORDER BY when_iso DESC")
    sql.append("LIMIT 1")

    with closing(get_connection()) as conn:
        cur = conn.execute("\n".join(sql), params)
        row = cur.fetchone()
        return str(row[0]) if row and row[0] else None


def get_usual_unit_price_batch(
    item_ids: List[int],
    *,
    receipt_only: bool = True,
    min_samples: int = 4,
    since_days: int = 180,
) -> Dict[int, Tuple[Optional[float], int, str]]:
    """
    Batch version of get_usual_unit_price.

    Returns {item_id: (usual_price, sample_count, basis)} for all item_ids.

    Single query fetches all prices (both receipt and non-receipt) for the
    full item set. Python then applies the same median + fallback logic as the
    single-item version, with zero extra DB round-trips.
    """
    ids = _coerce_id_list(item_ids)
    if not ids:
        return {}

    since = _since_clause(since_days)

    receipt_rows: Dict[int, List[float]] = {iid: [] for iid in ids}
    all_rows: Dict[int, List[float]] = {iid: [] for iid in ids}

    with closing(get_connection()) as conn:
        for chunk in _chunks(ids):
            placeholders = ",".join("?" * len(chunk))
            sql = (
                "SELECT item_id, unit_price, "
                "CASE WHEN (source = 'receipt' OR receipt_id IS NOT NULL) THEN 1 ELSE 0 END AS is_receipt "
                "FROM prices "
                f"WHERE item_id IN ({placeholders}) "
                "  AND unit_price IS NOT NULL AND unit_price > 0 "
                "  AND date(COALESCE(date, created_at)) >= date('now', ?) "
                "ORDER BY item_id"
            )
            for row in conn.execute(sql, (*chunk, since)).fetchall():
                try:
                    iid = int(row[0])
                    price = float(row[1])
                    is_receipt = int(row[2])
                except Exception:
                    continue
                if iid in all_rows:
                    all_rows[iid].append(price)
                    if is_receipt:
                        receipt_rows[iid].append(price)

    out: Dict[int, Tuple[Optional[float], int, str]] = {}
    for iid in ids:
        r_prices = receipt_rows[iid]
        a_prices = all_rows[iid]

        if len(r_prices) >= min_samples:
            out[iid] = (_median(r_prices), len(r_prices), "receipt_median")
        elif receipt_only and a_prices:
            out[iid] = (_median(a_prices), len(a_prices), "estimated_median")
        else:
            out[iid] = (None, len(r_prices), "unknown")

    return out


def get_six_month_low_batch(
    item_ids: List[int],
    *,
    since_days: int = 183,
) -> Dict[int, Tuple[Optional[float], Optional[str]]]:
    """
    Batch version of get_six_month_low_unit_price.

    Returns {item_id: (lowest_unit_price, when_iso)}.
    Uses a window function to find the minimum price row per item in one query.
    """
    ids = _coerce_id_list(item_ids)
    if not ids:
        return {}

    since = _since_clause(since_days)
    out: Dict[int, Tuple[Optional[float], Optional[str]]] = {iid: (None, None) for iid in ids}

    with closing(get_connection()) as conn:
        for chunk in _chunks(ids):
            placeholders = ",".join("?" * len(chunk))
            sql = (
                "SELECT item_id, unit_price, when_iso FROM ( "
                "  SELECT item_id, unit_price, COALESCE(date, created_at) AS when_iso, "
                "         ROW_NUMBER() OVER ("
                "             PARTITION BY item_id "
                "             ORDER BY unit_price ASC, COALESCE(date, created_at) ASC"
                "         ) AS rn "
                "  FROM prices "
                f"  WHERE item_id IN ({placeholders}) "
                "    AND unit_price IS NOT NULL AND unit_price > 0 "
                "    AND date(COALESCE(date, created_at)) >= date('now', ?) "
                ") WHERE rn = 1"
            )
            for row in conn.execute(sql, (*chunk, since)).fetchall():
                try:
                    iid = int(row[0])
                    price = float(row[1])
                    when = str(row[2]) if row[2] else None
                except Exception:
                    continue
                if iid in out:
                    out[iid] = (price, when)

    return out


def get_last_seen_at_or_below_batch(
    item_id_to_ceiling: Dict[int, float],
    *,
    since_days: int = 183,
) -> Dict[int, Optional[str]]:
    """
    Batch version of get_last_seen_at_or_below.

    item_id_to_ceiling: {item_id: price_ceiling} — each item may have a
    different ceiling (typically its current best price).

    Returns {item_id: most_recent_date_iso_or_None}.

    Fetches all candidate rows in one query, then filters by per-item ceiling
    in Python to avoid a variable-per-row WHERE clause.
    """
    ids = _coerce_id_list(item_id_to_ceiling.keys())
    if not ids:
        return {}

    since = _since_clause(since_days)
    out: Dict[int, Optional[str]] = {iid: None for iid in ids}
    seen: set = set()

    with closing(get_connection()) as conn:
        for chunk in _chunks(ids):
            placeholders = ",".join("?" * len(chunk))
            sql = (
                "SELECT item_id, unit_price, COALESCE(date, created_at) AS when_iso "
                "FROM prices "
                f"WHERE item_id IN ({placeholders}) "
                "  AND unit_price IS NOT NULL AND unit_price > 0 "
                "  AND date(COALESCE(date, created_at)) >= date('now', ?) "
                "ORDER BY item_id, when_iso DESC"
            )
            for row in conn.execute(sql, (*chunk, since)).fetchall():
                try:
                    iid = int(row[0])
                    price = float(row[1])
                    when = str(row[2]) if row[2] else None
                except Exception:
                    continue
                if iid in seen:
                    continue
                ceiling = item_id_to_ceiling.get(iid)
                if ceiling is not None and price <= float(ceiling):
                    out[iid] = when
                    seen.add(iid)

    return out


def get_active_flyer_unit_price(
    item_id: int,
    store_id: int,
) -> Optional[float]:
    """Return the active flyer unit price if we can resolve it.

    Priority:
      1) Join with flyer_sources when present.
      2) Fallback: any 'flyer' price recorded in the last ~3 weeks.
    """
    with closing(get_connection()) as conn:
        cur = conn.cursor()

        # 1) Try flyer_sources join (table/column may not exist in early prototypes)
        try:
            cur.execute(
                """
                SELECT p.unit_price
                FROM prices p
                JOIN flyer_sources fs ON fs.id = p.flyer_source_id
                WHERE p.item_id = ?
                  AND p.store_id = ?
                  AND p.unit_price IS NOT NULL
                  AND p.source = 'flyer'
                  AND date(fs.valid_from) <= date('now')
                  AND date(fs.valid_to) >= date('now')
                ORDER BY p.unit_price ASC
                LIMIT 1
                """,
                (int(item_id), int(store_id)),
            )
            row = cur.fetchone()
            if row and row[0] is not None:
                return float(row[0])
        except Exception:
            pass

        # 2) Fallback: recent flyer rows
        try:
            cur.execute(
                """
                SELECT unit_price
                FROM prices
                WHERE item_id = ?
                  AND store_id = ?
                  AND unit_price IS NOT NULL
                  AND source = 'flyer'
                  AND date(COALESCE(date, created_at)) >= date('now', '-21 day')
                ORDER BY unit_price ASC
                LIMIT 1
                """,
                (int(item_id), int(store_id)),
            )
            row = cur.fetchone()
            if row and row[0] is not None:
                return float(row[0])
        except Exception:
            pass

    return None


def list_staple_item_ids(
    *,
    since_days: int = 90,
    min_distinct_receipts: int = 3,
    min_line_items: int = 4,
) -> List[Tuple[int, int, int]]:
    """Return likely staple items based on receipt history.

    Returns list of tuples:
      (item_id, line_count, distinct_receipt_count)
    """
    sql = """
    SELECT
        item_id,
        COUNT(*) AS line_count,
        COUNT(DISTINCT receipt_id) AS receipt_count
    FROM prices
    WHERE item_id IS NOT NULL
      AND unit_price IS NOT NULL
      AND (source = 'receipt' OR receipt_id IS NOT NULL)
      AND date(COALESCE(date, created_at)) >= date('now', ?)
    GROUP BY item_id
    HAVING line_count >= ? OR receipt_count >= ?
    ORDER BY receipt_count DESC, line_count DESC
    """

    with closing(get_connection()) as conn:
        cur = conn.execute(sql, (_since_clause(since_days), int(min_line_items), int(min_distinct_receipts)))
        return [(int(r[0]), int(r[1]), int(r[2])) for r in cur.fetchall()]


def get_best_current_quote_for_item_store(
    item_id: int,
    store_id: int,
) -> Optional[Dict[str, Any]]:
    """Best-effort current quote for an item/store.

    Preference order:
      flyer (active) -> most recent store price (any source)
    """
    flyer = get_active_flyer_unit_price(item_id, store_id)
    if flyer is not None:
        return {"unit_price": float(flyer), "source": "flyer"}

    latest = get_most_recent_price(item_id, store_id=store_id)
    if latest and latest.unit_price is not None:
        return {"unit_price": float(latest.unit_price), "source": latest.source or "latest"}

    return None


# ---------------------------------------------------------------------------
# Batch query helpers — replace N+1 loops with single SQL round-trips
# ---------------------------------------------------------------------------

def _price_cols() -> str:
    """Column list used by all batch SELECT statements (matches PricePoint field order)."""
    return (
        "id, item_id, store_id, receipt_id, flyer_source_id, source, date, "
        "unit_price, unit, quantity, total_price, raw_name, confidence, "
        "norm_unit_price, norm_unit"
    )


def _row_to_price_point(r) -> PricePoint:
    return PricePoint(
        id=r[0], item_id=r[1], store_id=r[2], receipt_id=r[3],
        flyer_source_id=r[4], source=r[5], date=r[6],
        unit_price=r[7], unit=r[8], quantity=r[9],
        total_price=r[10], raw_name=r[11], confidence=r[12],
        norm_unit_price=r[13], norm_unit=r[14],
    )


def get_most_recent_prices_by_store_batch(
    item_ids: List[int],
    store_ids: List[int],
) -> Dict[Tuple[int, int], PricePoint]:
    """Return the most recent PricePoint per (item_id, store_id) in a single query.

    Replaces N×M calls to get_most_recent_price(item_id, store_id=store_id).
    Returns {(item_id, store_id): PricePoint}.
    """
    items = _coerce_id_list(item_ids)
    stores = _coerce_id_list(store_ids)
    if not items or not stores:
        return {}

    store_ph = ",".join("?" * len(stores))
    out: Dict[Tuple[int, int], PricePoint] = {}
    with closing(get_connection()) as conn:
        for chunk in _chunks(items):
            item_ph = ",".join("?" * len(chunk))
            sql = (
                f"SELECT {_price_cols()} FROM ( "
                f"  SELECT {_price_cols()}, "
                "         ROW_NUMBER() OVER ("
                "             PARTITION BY item_id, store_id "
                "             ORDER BY date DESC, id DESC"
                "         ) AS rn "
                "  FROM prices "
                f"  WHERE item_id  IN ({item_ph}) "
                f"    AND store_id IN ({store_ph}) "
                "    AND unit_price IS NOT NULL "
                ") WHERE rn = 1"
            )
            for r in conn.execute(sql, (*chunk, *stores)).fetchall():
                pp = _row_to_price_point(r)
                out[(pp.item_id, pp.store_id)] = pp
    return out


def get_most_recent_prices_global_batch(
    item_ids: List[int],
) -> Dict[int, PricePoint]:
    """Return the most recent PricePoint per item_id (across all stores) in a single query.

    Replaces N calls to get_most_recent_price(item_id, store_id=None).
    Returns {item_id: PricePoint}.
    """
    items = _coerce_id_list(item_ids)
    if not items:
        return {}

    out: Dict[int, PricePoint] = {}
    with closing(get_connection()) as conn:
        for chunk in _chunks(items):
            placeholders = ",".join("?" * len(chunk))
            sql = (
                f"SELECT {_price_cols()} FROM ( "
                f"  SELECT {_price_cols()}, "
                "         ROW_NUMBER() OVER ("
                "             PARTITION BY item_id "
                "             ORDER BY date DESC, id DESC"
                "         ) AS rn "
                "  FROM prices "
                f"  WHERE item_id IN ({placeholders}) "
                "    AND unit_price IS NOT NULL "
                ") WHERE rn = 1"
            )
            for r in conn.execute(sql, chunk).fetchall():
                pp = _row_to_price_point(r)
                out[pp.item_id] = pp
    return out


def get_active_flyer_prices_batch(
    item_ids: List[int],
    store_ids: List[int],
) -> Dict[Tuple[int, int], Dict[str, Any]]:
    """Return the lowest active flyer unit_price per (item_id, store_id) in a single query.

    Replaces N×M calls to get_active_flyer_unit_price(item_id, store_id).
    Returns {(item_id, store_id): {"unit_price": float, "source": "flyer"}}.
    """
    items = _coerce_id_list(item_ids)
    stores = _coerce_id_list(store_ids)
    if not items or not stores:
        return {}

    store_ph = ",".join("?" * len(stores))
    out: Dict[Tuple[int, int], Dict[str, Any]] = {}
    try:
        with closing(get_connection()) as conn:
            for chunk in _chunks(items):
                item_ph = ",".join("?" * len(chunk))
                sql = (
                    "SELECT p.item_id, p.store_id, "
                    "       MIN(COALESCE(p.norm_unit_price, p.unit_price)) AS unit_price, "
                    "       COALESCE(p.norm_unit, p.unit, 'each') AS unit "
                    "FROM prices p "
                    "JOIN flyer_sources fs ON fs.id = p.flyer_source_id "
                    "WHERE p.source = 'flyer' "
                    f"  AND p.item_id  IN ({item_ph}) "
                    f"  AND p.store_id IN ({store_ph}) "
                    "  AND p.unit_price IS NOT NULL "
                    "  AND date(fs.valid_from) <= date('now') "
                    "  AND date(fs.valid_to)   >= date('now') "
                    "GROUP BY p.item_id, p.store_id"
                )
                for r in conn.execute(sql, (*chunk, *stores)).fetchall():
                    item_id = int(r[0])
                    store_id = int(r[1])
                    unit_price = float(r[2])
                    unit = str(r[3] or "each").strip().lower()
                    out[(item_id, store_id)] = {"unit_price": unit_price, "unit": unit, "source": "flyer"}
    except sqlite3.OperationalError as e:
        if "flyer_sources" not in str(e).lower():
            raise
    return out


def get_price_stats_batch(
    item_ids: List[int],
    since_days: int = 180,
) -> Dict[int, PriceStats]:
    """Return PriceStats per item_id in a single query.

    Replaces N calls to get_price_stats_for_item(item_id, since_days=...).
    Returns {item_id: PriceStats}. Items with no history are omitted.
    """
    items = _coerce_id_list(item_ids)
    if not items:
        return {}

    since = _since_clause(since_days)
    out: Dict[int, PriceStats] = {}
    with closing(get_connection()) as conn:
        for chunk in _chunks(items):
            placeholders = ",".join("?" * len(chunk))
            sql = (
                "SELECT item_id, MIN(COALESCE(norm_unit_price, unit_price)), MAX(COALESCE(norm_unit_price, unit_price)), AVG(COALESCE(norm_unit_price, unit_price)), COUNT(*) "
                "FROM prices "
                f"WHERE item_id IN ({placeholders}) "
                "  AND unit_price IS NOT NULL "
                "  AND date(COALESCE(date, created_at)) >= date('now', ?) "
                "GROUP BY item_id"
            )
            for r in conn.execute(sql, (*chunk, since)).fetchall():
                item_id = int(r[0])
                out[item_id] = PriceStats(
                    item_id=item_id,
                    store_id=None,
                    min_price=float(r[1]),
                    max_price=float(r[2]),
                    avg_price=float(r[3]),
                    count=int(r[4]),
                )
    return out


def get_recent_avg_unit_price_by_store_batch(
    item_ids: List[int],
    store_ids: List[int],
    *,
    since_days: int = 180,
    limit: int = 12,
) -> Dict[Tuple[int, int], float]:
    """Average of the most-recent `limit` unit prices per (item_id, store_id),
    within the trailing `since_days` window, in a single (chunked) query.

    Replaces the O(items x stores) per-pair calls PlanningService made via
    mean(get_prices_for_item(item_id, store_id, since_days, limit)). Parity: rows
    are ranked by date DESC and the top `limit` are kept (NULL unit_prices count
    toward the limit, exactly like get_prices_for_item's DESC+LIMIT), then AVG
    skips NULLs (matching the Python `p.unit_price is not None` filter).
    """
    items = _coerce_id_list(item_ids)
    stores = _coerce_id_list(store_ids)
    if not items or not stores:
        return {}

    cutoff = (datetime.now(timezone.utc).date() - timedelta(days=int(since_days))).isoformat()
    lim = int(limit) if limit and int(limit) > 0 else None
    store_ph = ",".join("?" * len(stores))
    out: Dict[Tuple[int, int], float] = {}
    with closing(get_connection()) as conn:
        for chunk in _chunks(items):
            item_ph = ",".join("?" * len(chunk))
            if lim is None:
                sql = (
                    "SELECT item_id, store_id, AVG(COALESCE(norm_unit_price, unit_price)) "
                    "FROM prices "
                    f"WHERE item_id IN ({item_ph}) AND store_id IN ({store_ph}) "
                    "  AND date >= ? AND unit_price IS NOT NULL "
                    "GROUP BY item_id, store_id"
                )
                params: Tuple[Any, ...] = (*chunk, *stores, cutoff)
            else:
                sql = (
                    "SELECT item_id, store_id, AVG(COALESCE(norm_unit_price, unit_price)) FROM ( "
                    "  SELECT item_id, store_id, norm_unit_price, unit_price, "
                    "         ROW_NUMBER() OVER ( "
                    "             PARTITION BY item_id, store_id ORDER BY date DESC, id DESC "
                    "         ) AS rn "
                    "  FROM prices "
                    f"  WHERE item_id IN ({item_ph}) AND store_id IN ({store_ph}) AND date >= ? "
                    ") WHERE rn <= ? AND unit_price IS NOT NULL "
                    "GROUP BY item_id, store_id"
                )
                params = (*chunk, *stores, cutoff, lim)
            for r in conn.execute(sql, params).fetchall():
                if r[2] is not None:
                    out[(int(r[0]), int(r[1]))] = float(r[2])
    return out


def get_recent_avg_unit_price_global_batch(
    item_ids: List[int],
    *,
    since_days: int = 180,
    limit: int = 20,
) -> Dict[int, float]:
    """Like get_recent_avg_unit_price_by_store_batch but across ALL stores —
    PlanningService's overall fallback when an item has no store-specific
    history. Mirrors mean(get_prices_for_item(item_id, store_id=None, ...)).
    """
    items = _coerce_id_list(item_ids)
    if not items:
        return {}

    cutoff = (datetime.now(timezone.utc).date() - timedelta(days=int(since_days))).isoformat()
    lim = int(limit) if limit and int(limit) > 0 else None
    out: Dict[int, float] = {}
    with closing(get_connection()) as conn:
        for chunk in _chunks(items):
            item_ph = ",".join("?" * len(chunk))
            if lim is None:
                sql = (
                    "SELECT item_id, AVG(COALESCE(norm_unit_price, unit_price)) FROM prices "
                    f"WHERE item_id IN ({item_ph}) AND date >= ? AND unit_price IS NOT NULL "
                    "GROUP BY item_id"
                )
                params: Tuple[Any, ...] = (*chunk, cutoff)
            else:
                sql = (
                    "SELECT item_id, AVG(COALESCE(norm_unit_price, unit_price)) FROM ( "
                    "  SELECT item_id, norm_unit_price, unit_price, "
                    "         ROW_NUMBER() OVER ( "
                    "             PARTITION BY item_id ORDER BY date DESC, id DESC "
                    "         ) AS rn "
                    "  FROM prices "
                    f"  WHERE item_id IN ({item_ph}) AND date >= ? "
                    ") WHERE rn <= ? AND unit_price IS NOT NULL "
                    "GROUP BY item_id"
                )
                params = (*chunk, cutoff, lim)
            for r in conn.execute(sql, params).fetchall():
                if r[1] is not None:
                    out[int(r[0])] = float(r[1])
    return out


def get_purchase_cadence_batch(
    item_ids: List[int],
    *,
    since_days: int = 180,
) -> Dict[int, Tuple[Optional[float], Optional[float]]]:
    """Return avg purchase interval and typical quantity for staple items.

    Returns {item_id: (avg_interval_days, typical_qty)}.
    avg_interval_days is None if fewer than 2 distinct receipts.
    typical_qty is None if no quantity data.
    Uses only receipt-sourced rows (source='receipt' or receipt_id IS NOT NULL).
    """
    items = _coerce_id_list(item_ids)
    if not items:
        return {}

    out: Dict[int, Tuple[Optional[float], Optional[float]]] = {}
    with closing(get_connection()) as conn:
        for chunk in _chunks(items):
            item_ph = ",".join("?" * len(chunk))
            sql = f"""
            SELECT
                item_id,
                COUNT(DISTINCT receipt_id) AS receipt_count,
                MIN(date(COALESCE(date, created_at))) AS first_date,
                MAX(date(COALESCE(date, created_at))) AS last_date,
                AVG(CASE WHEN quantity IS NOT NULL AND quantity > 0 THEN quantity END) AS avg_qty
            FROM prices
            WHERE item_id IN ({item_ph})
              AND (source = 'receipt' OR receipt_id IS NOT NULL)
              AND date(COALESCE(date, created_at)) >= date('now', ?)
            GROUP BY item_id
            """
            rows = conn.execute(sql, (*chunk, _since_clause(since_days))).fetchall()
            for r in rows:
                item_id = int(r[0])
                receipt_count = int(r[1]) if r[1] is not None else 0
                first_date = r[2]
                last_date = r[3]
                avg_qty = float(r[4]) if r[4] is not None else None

                avg_interval: Optional[float] = None
                if receipt_count >= 2 and first_date and last_date and first_date != last_date:
                    from datetime import date as _date
                    try:
                        d0 = _date.fromisoformat(first_date)
                        d1 = _date.fromisoformat(last_date)
                        span = (d1 - d0).days
                        if span > 0:
                            avg_interval = span / (receipt_count - 1)
                    except ValueError:
                        pass

                out[item_id] = (avg_interval, avg_qty)
    return out
