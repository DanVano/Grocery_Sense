from __future__ import annotations

from dataclasses import dataclass
from typing import List, Optional, Tuple

from Grocery_Sense.data.connection import connection_scope, get_connection


@dataclass
class ShoppingListRow:
    id: int
    display_name: str
    quantity: float
    unit: str
    category: str
    is_checked_off: bool
    notes: str
    added_by: Optional[str]
    added_by_member_id: Optional[int]
    is_active: bool
    planned_store_id: Optional[int]
    item_id: Optional[int] = None


def _row_to_obj(row) -> ShoppingListRow:
    return ShoppingListRow(
        id=int(row["id"]),
        display_name=str(row["display_name"] or ""),
        quantity=float(row["quantity"] or 1.0),
        unit=str(row["unit"] or ""),
        category=str(row["category"] or ""),
        is_checked_off=bool(row["is_checked_off"] or 0),
        notes=str(row["notes"] or ""),
        added_by=str(row["added_by"]) if row["added_by"] else None,
        added_by_member_id=int(row["added_by_member_id"]) if row["added_by_member_id"] is not None else None,
        is_active=bool(row["is_active"] or 0),
        planned_store_id=int(row["planned_store_id"]) if row["planned_store_id"] is not None else None,
        item_id=int(row["item_id"]) if row["item_id"] is not None else None,
    )


def list_active_items(
    *, store_id: Optional[int] = None, include_checked_off: bool = False
) -> List[ShoppingListRow]:
    """
    Active + not deleted items.

    If store_id is provided, filters by planned_store_id == store_id.
    If include_checked_off is False (default), only returns unchecked items.
    """
    cols = (
        "SELECT id, display_name, quantity, unit, category, is_checked_off, notes, "
        "added_by, added_by_member_id, is_active, planned_store_id, item_id FROM shopping_list "
        "WHERE is_active = 1 AND is_deleted = 0"
    )
    with connection_scope() as conn:
        if store_id is None:
            if include_checked_off:
                sql = cols + " ORDER BY id DESC"
            else:
                sql = cols + " AND is_checked_off = 0 ORDER BY id DESC"
            rows = conn.execute(sql).fetchall()
        else:
            if include_checked_off:
                sql = cols + " AND planned_store_id = ? ORDER BY id DESC"
            else:
                sql = cols + " AND is_checked_off = 0 AND planned_store_id = ? ORDER BY id DESC"
            rows = conn.execute(sql, (int(store_id),)).fetchall()

    return [_row_to_obj(r) for r in rows]


def list_all_items() -> List[ShoppingListRow]:
    with connection_scope() as conn:
        rows = conn.execute(
            """
            SELECT
                id, display_name, quantity, unit, category, is_checked_off, notes,
                added_by, added_by_member_id, is_active, planned_store_id, item_id
            FROM shopping_list
            WHERE is_deleted = 0
            ORDER BY id DESC
            """
        ).fetchall()
    return [_row_to_obj(r) for r in rows]


def get_item(row_id: int) -> Optional[ShoppingListRow]:
    with connection_scope() as conn:
        row = conn.execute(
            """
            SELECT id, display_name, quantity, unit, category,
                   is_checked_off, notes, added_by, added_by_member_id, is_active,
                   planned_store_id, item_id
            FROM shopping_list WHERE id = ?
            """,
            (int(row_id),),
        ).fetchone()
    return _row_to_obj(row) if row else None


def bulk_add_items(rows: List[Tuple]) -> int:
    """Insert many shopping_list rows in one transaction.

    Each tuple matches: (display_name, quantity, unit, category, notes,
    added_by, added_by_member_id, planned_store_id, item_id).
    """
    if not rows:
        return 0
    with connection_scope() as conn:
        cur = conn.executemany(
            """
            INSERT INTO shopping_list
                (display_name, quantity, unit, category, notes, added_by,
                 added_by_member_id, planned_store_id, item_id)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            rows,
        )
        conn.commit()
        n = int(cur.rowcount or 0)
        return n if n >= 0 else len(rows)


def add_item(
    *,
    display_name: str,
    quantity: float = 1.0,
    unit: str = "",
    category: str = "",
    notes: str = "",
    added_by: Optional[str] = None,
    added_by_member_id: Optional[int] = None,
    planned_store_id: Optional[int] = None,
    item_id: Optional[int] = None,
) -> int:
    with connection_scope() as conn:
        cur = conn.execute(
            """
            INSERT INTO shopping_list
                (display_name, quantity, unit, category, notes, added_by, added_by_member_id, planned_store_id, item_id)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                (display_name or "").strip(),
                float(quantity or 1.0),
                (unit or "").strip(),
                (category or "").strip(),
                (notes or "").strip(),
                (added_by or "").strip() or None,
                int(added_by_member_id) if added_by_member_id is not None else None,
                int(planned_store_id) if planned_store_id is not None else None,
                int(item_id) if item_id is not None else None,
            ),
        )
        conn.commit()
        return int(cur.lastrowid)


def set_checked_off(item_id: int, checked: bool) -> None:
    with connection_scope() as conn:
        conn.execute(
            "UPDATE shopping_list SET is_checked_off = ? WHERE id = ?",
            (1 if checked else 0, int(item_id)),
        )
        conn.commit()


def delete_item(item_id: int) -> None:
    with connection_scope() as conn:
        conn.execute("UPDATE shopping_list SET is_deleted = 1 WHERE id = ?", (int(item_id),))
        conn.commit()


def clear_all_items() -> None:
    with connection_scope() as conn:
        conn.execute("UPDATE shopping_list SET is_deleted = 1")
        conn.commit()


def clear_checked_off_items() -> None:
    """Mark only checked-off active items as deleted."""
    with connection_scope() as conn:
        conn.execute(
            "UPDATE shopping_list SET is_deleted = 1 WHERE is_checked_off = 1 AND is_active = 1"
        )
        conn.commit()


# ---------------------------------------------------------------------------
# NEW: Planned store assignment (Milestone: “Use this plan”)
# ---------------------------------------------------------------------------

def clear_planned_store_ids_for_active_items(*, include_checked_off: bool = False) -> int:
    """
    Clears planned_store_id for active items (optionally including checked-off ones).
    Returns the number of rows affected (best-effort; sqlite may return -1 in some cases).
    """
    if include_checked_off:
        sql = "UPDATE shopping_list SET planned_store_id = NULL WHERE is_active = 1 AND is_deleted = 0"
    else:
        sql = "UPDATE shopping_list SET planned_store_id = NULL WHERE is_active = 1 AND is_deleted = 0 AND is_checked_off = 0"
    with connection_scope() as conn:
        cur = conn.execute(sql)
        conn.commit()
        return int(cur.rowcount or 0)


def set_planned_store_id(item_id: int, planned_store_id: Optional[int]) -> None:
    with connection_scope() as conn:
        conn.execute(
            "UPDATE shopping_list SET planned_store_id = ? WHERE id = ?",
            (int(planned_store_id) if planned_store_id is not None else None, int(item_id)),
        )
        conn.commit()


def bulk_set_planned_store_ids(assignments: List[Tuple[int, Optional[int]]]) -> int:
    """
    Update planned_store_id on shopping_list rows keyed by row id.

    assignments = [(row_id, planned_store_id_or_None), ...]
    Returns number of attempted updates.
    """
    if not assignments:
        return 0

    rows = [(int(row_id), int(store_id) if store_id is not None else None) for (row_id, store_id) in assignments]

    with connection_scope() as conn:
        conn.executemany(
            "UPDATE shopping_list SET planned_store_id = ? WHERE id = ?",
            [(store_id, row_id) for (row_id, store_id) in rows],
        )
        conn.commit()

    return len(rows)


def bulk_set_planned_store_ids_by_item_id(
    assignments: List[Tuple[int, Optional[int]]],
    *,
    active_only: bool = True,
) -> int:
    """
    Update planned_store_id on shopping_list rows keyed by CANONICAL item_id
    (items.id), not row id. This is what callers who have an optimizer result
    — which holds canonical item ids — actually want.

    assignments = [(item_id, planned_store_id_or_None), ...]
    Returns the number of rows actually updated (sums cur.rowcount per UPDATE).

    When active_only=True (default), only active, non-deleted rows are touched,
    so clearing the list doesn't inadvertently modify archived rows.
    """
    if not assignments:
        return 0

    rows = [
        (int(item_id), int(store_id) if store_id is not None else None)
        for (item_id, store_id) in assignments
    ]

    if active_only:
        sql = "UPDATE shopping_list SET planned_store_id = ? WHERE item_id = ? AND is_active = 1 AND is_deleted = 0"
    else:
        sql = "UPDATE shopping_list SET planned_store_id = ? WHERE item_id = ?"

    with connection_scope() as conn:
        cur = conn.executemany(sql, [(store_id, item_id) for item_id, store_id in rows])
        conn.commit()
        updated = int(cur.rowcount or 0)
        if updated < 0:
            updated = 0

    return updated
