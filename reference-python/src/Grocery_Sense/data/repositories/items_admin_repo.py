from __future__ import annotations

import sqlite3
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional, Tuple

from Grocery_Sense.data.connection import connection_scope, get_connection


def _now_utc_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


VALID_UNITS = ("each", "lb", "kg", "g")

# Tables this admin repo is allowed to inspect via PRAGMA. PRAGMA cannot accept
# `?` placeholders, so all dynamic table names go through this whitelist.
_ALLOWED_PRAGMA_TABLES = frozenset({
    "items", "item_aliases", "prices", "receipt_line_items",
    "shopping_list", "flyer_deals",
})

# Tables that carry an item_id column referencing items.id. Used by merge_items
# instead of walking sqlite_master (per CLAUDE.md: no generic helpers for
# one-off operations; new tables must be added here explicitly).
_ITEM_ID_TABLES = ("prices", "receipt_line_items", "shopping_list", "flyer_deals", "item_aliases")


@dataclass(frozen=True)
class ItemRow:
    id: int
    canonical_name: str
    is_tracked: int
    default_unit: Optional[str]
    price_points: int
    last_price_date: Optional[str]


class ItemsAdminRepo:
    """
    Admin helpers for Item Manager UI.

    - Ensures items.default_unit + items.is_tracked exist
    - Searches items
    - Toggles tracked
    - Sets default unit
    - Renames canonical name
    - Merges items safely by updating all tables containing item_id
    """

    # ---------------------------
    # Schema ensure
    # ---------------------------

    def ensure_schema(self) -> None:
        self._ensure_items_columns()

    def _col_exists(self, table: str, col: str) -> bool:
        if table not in _ALLOWED_PRAGMA_TABLES:
            raise ValueError(f"Disallowed table for PRAGMA: {table}")
        with connection_scope() as conn:
            rows = conn.execute(f"PRAGMA table_info({table});").fetchall()
        return any(r[1] == col for r in rows)

    def _ensure_items_columns(self) -> None:
        with connection_scope() as conn:
            # items.is_tracked
            if not self._col_exists("items", "is_tracked"):
                conn.execute("ALTER TABLE items ADD COLUMN is_tracked INTEGER NOT NULL DEFAULT 0;")

            # items.default_unit
            if not self._col_exists("items", "default_unit"):
                conn.execute("ALTER TABLE items ADD COLUMN default_unit TEXT;")

            conn.commit()

    # ---------------------------
    # Queries
    # ---------------------------

    def search_items(self, query: str = "", *, limit: int = 250) -> List[ItemRow]:
        """
        Returns items with light stats (price_points + last_price_date).
        """
        self.ensure_schema()
        q = (query or "").strip()

        where = ""
        params: List[Any] = []
        if q:
            where = "WHERE i.canonical_name LIKE ?"
            params.append(f"%{q}%")

        sql = f"""
            SELECT
                i.id,
                COALESCE(i.canonical_name, '') AS canonical_name,
                COALESCE(i.is_tracked, 0) AS is_tracked,
                i.default_unit,
                COALESCE(ps.price_points, 0) AS price_points,
                ps.last_price_date
            FROM items i
            LEFT JOIN (
                SELECT item_id, COUNT(1) AS price_points, MAX(date) AS last_price_date
                FROM prices
                GROUP BY item_id
            ) ps ON ps.item_id = i.id
            {where}
            ORDER BY COALESCE(i.is_tracked, 0) DESC, i.canonical_name ASC
            LIMIT ?;
        """
        params.append(int(limit))

        with connection_scope() as conn:
            rows = conn.execute(sql, tuple(params)).fetchall()

        out: List[ItemRow] = []
        for r in rows:
            out.append(
                ItemRow(
                    id=int(r[0]),
                    canonical_name=str(r[1] or ""),
                    is_tracked=int(r[2] or 0),
                    default_unit=(str(r[3]).strip().lower() if r[3] else None),
                    price_points=int(r[4] or 0),
                    last_price_date=r[5],
                )
            )
        return out

    def get_item(self, item_id: int) -> Optional[Dict[str, Any]]:
        self.ensure_schema()
        with connection_scope() as conn:
            r = conn.execute(
                """
                SELECT id, canonical_name, COALESCE(is_tracked,0), default_unit
                FROM items
                WHERE id = ?;
                """,
                (int(item_id),),
            ).fetchone()
        if not r:
            return None
        return {
            "id": int(r[0]),
            "canonical_name": r[1],
            "is_tracked": int(r[2] or 0),
            "default_unit": (str(r[3]).strip().lower() if r[3] else None),
        }

    # ---------------------------
    # Mutations
    # ---------------------------

    def toggle_tracked(self, item_id: int) -> int:
        self.ensure_schema()
        with connection_scope() as conn:
            cur = conn.execute("SELECT COALESCE(is_tracked,0) FROM items WHERE id=?;", (int(item_id),)).fetchone()
            if not cur:
                raise ValueError(f"Item not found: {item_id}")
            new_val = 0 if int(cur[0] or 0) == 1 else 1
            conn.execute("UPDATE items SET is_tracked=? WHERE id=?;", (new_val, int(item_id)))
            conn.commit()
        return new_val

    def set_default_unit(self, item_id: int, default_unit: Optional[str]) -> None:
        self.ensure_schema()
        unit = (default_unit or "").strip().lower() or None
        if unit is not None and unit not in VALID_UNITS:
            raise ValueError(f"Invalid unit: {unit} (allowed: {', '.join(VALID_UNITS)})")

        with connection_scope() as conn:
            conn.execute("UPDATE items SET default_unit=? WHERE id=?;", (unit, int(item_id)))
            conn.commit()

    def rename_item(self, item_id: int, new_name: str) -> None:
        self.ensure_schema()
        name = (new_name or "").strip()
        if not name:
            raise ValueError("New name cannot be empty.")

        with connection_scope() as conn:
            conn.execute("UPDATE items SET canonical_name=? WHERE id=?;", (name, int(item_id)))
            conn.commit()

    def merge_items(
        self,
        *,
        target_item_id: int,
        source_item_id: int,
        keep_source_as_alias: bool = True,
    ) -> None:
        """
        Merge source -> target:
          - move all references (tables with item_id column) from source to target
          - copy default_unit to target if target missing
          - OR tracked: if either tracked, target becomes tracked
          - delete source row

        If keep_source_as_alias, attempts to insert source canonical name into item_aliases
        (only if the table exists).
        """
        self.ensure_schema()

        if int(target_item_id) == int(source_item_id):
            raise ValueError("Target and source item are the same.")

        with connection_scope() as conn:
            conn.execute("BEGIN;")
            try:
                t = conn.execute(
                    "SELECT id, canonical_name, COALESCE(is_tracked,0), default_unit FROM items WHERE id=?;",
                    (int(target_item_id),),
                ).fetchone()
                s = conn.execute(
                    "SELECT id, canonical_name, COALESCE(is_tracked,0), default_unit FROM items WHERE id=?;",
                    (int(source_item_id),),
                ).fetchone()

                if not t:
                    raise ValueError(f"Target item not found: {target_item_id}")
                if not s:
                    raise ValueError(f"Source item not found: {source_item_id}")

                target_tracked = int(t[2] or 0)
                source_tracked = int(s[2] or 0)
                target_unit = (str(t[3]).strip().lower() if t[3] else None)
                source_unit = (str(s[3]).strip().lower() if s[3] else None)

                # Promote tracked / default_unit
                if target_tracked == 0 and source_tracked == 1:
                    conn.execute("UPDATE items SET is_tracked=1 WHERE id=?;", (int(target_item_id),))

                if (not target_unit) and source_unit in VALID_UNITS:
                    conn.execute("UPDATE items SET default_unit=? WHERE id=?;", (source_unit, int(target_item_id)))

                # Move references across the known item_id-bearing tables. New
                # such tables must be added to _ITEM_ID_TABLES explicitly.
                existing_tables = {
                    r[0] for r in conn.execute(
                        "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';"
                    ).fetchall()
                }

                for table in _ITEM_ID_TABLES:
                    if table not in existing_tables:
                        continue

                    # Wrap per-table UPDATE in a SAVEPOINT so a unique-constraint
                    # collision only rolls back this table's change, leaving the
                    # outer transaction intact.
                    conn.execute("SAVEPOINT merge_table;")
                    try:
                        conn.execute(
                            f"UPDATE {table} SET item_id=? WHERE item_id=?;",
                            (int(target_item_id), int(source_item_id)),
                        )
                        conn.execute("RELEASE merge_table;")
                    except sqlite3.IntegrityError:
                        conn.execute("ROLLBACK TO merge_table;")
                        conn.execute("RELEASE merge_table;")
                        conn.execute(
                            f"DELETE FROM {table} WHERE item_id=?;",
                            (int(source_item_id),),
                        )

                # Keep source name as alias (optional, if item_aliases exists)
                if keep_source_as_alias:
                    source_name = (s[1] or "").strip().lower()
                    if source_name:
                        if "item_aliases" in existing_tables:
                            # best-effort schema: alias_text, item_id, confidence, source, created_at
                            cols = conn.execute("PRAGMA table_info(item_aliases);").fetchall()
                            alias_cols = {c[1] for c in cols}
                            if {"alias_text", "item_id"}.issubset(alias_cols):
                                # avoid duplicates
                                existing = conn.execute(
                                    "SELECT 1 FROM item_aliases WHERE alias_text=? LIMIT 1;",
                                    (source_name,),
                                ).fetchone()
                                if not existing:
                                    # flexible insert based on available cols
                                    fields = ["alias_text", "item_id"]
                                    values = [source_name, int(target_item_id)]

                                    if "confidence" in alias_cols:
                                        fields.append("confidence")
                                        values.append(1.0)
                                    if "source" in alias_cols:
                                        fields.append("source")
                                        values.append("merge")
                                    if "created_at" in alias_cols:
                                        fields.append("created_at")
                                        values.append(_now_utc_iso())

                                    placeholders = ", ".join(["?"] * len(values))
                                    conn.execute(
                                        f"INSERT INTO item_aliases ({', '.join(fields)}) VALUES ({placeholders});",
                                        tuple(values),
                                    )

                # Delete the source item
                conn.execute("DELETE FROM items WHERE id=?;", (int(source_item_id),))

                conn.commit()

            except Exception:
                conn.execute("ROLLBACK;")
                raise
