"""
Grocery_Sense.data.repositories.stores_repo

SQLite-backed persistence for Store objects.
"""

from __future__ import annotations

from typing import List, Optional
from contextlib import closing
from datetime import datetime, timezone

from Grocery_Sense.data.connection import connection_scope, get_connection
from Grocery_Sense.domain.models import Store


# ---------- Row mapping helpers ----------

_SELECT_COLS = (
    "id, name, address, city, postal_code, "
    "flipp_store_id, is_favorite, priority, shop_here, notes, created_at, is_active, distance_km"
)


def _row_to_store(row) -> Store:
    (
        store_id,
        name,
        address,
        city,
        postal_code,
        flipp_store_id,
        is_favorite,
        priority,
        shop_here,
        notes,
        created_at,
        is_active,
        distance_km,
    ) = row

    return Store(
        id=store_id,
        name=name,
        address=address,
        city=city,
        postal_code=postal_code,
        flipp_store_id=flipp_store_id,
        is_favorite=bool(is_favorite),
        priority=priority or 0,
        shop_here=bool(shop_here) if shop_here is not None else True,
        is_active=bool(is_active) if is_active is not None else True,
        notes=notes,
        distance_km=float(distance_km) if distance_km is not None else None,
    )


# ---------- CRUD operations ----------

def create_store(
    name: str,
    address: Optional[str] = None,
    city: Optional[str] = None,
    postal_code: Optional[str] = None,
    flipp_store_id: Optional[str] = None,
    is_favorite: bool = False,
    priority: int = 0,
    notes: Optional[str] = None,
) -> Store:
    """
    Insert a new store and return the Store object.
    """
    with connection_scope() as conn, closing(conn.cursor()) as cur:
        cur.execute(
            """
            INSERT INTO stores (
                name,
                address,
                city,
                postal_code,
                flipp_store_id,
                is_favorite,
                priority,
                notes,
                created_at
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                name,
                address,
                city,
                postal_code,
                flipp_store_id,
                1 if is_favorite else 0,
                priority,
                notes,
                datetime.now(timezone.utc).isoformat(timespec="seconds"),
            ),
        )
        store_id = cur.lastrowid

        cur.execute(
            f"SELECT {_SELECT_COLS} FROM stores WHERE id = ?",
            (store_id,),
        )
        row = cur.fetchone()

    return _row_to_store(row)


def get_store_by_id(store_id: int) -> Optional[Store]:
    """
    Fetch a single store by ID (active or archived).
    """
    with connection_scope() as conn, closing(conn.cursor()) as cur:
        cur.execute(
            f"SELECT {_SELECT_COLS} FROM stores WHERE id = ?",
            (store_id,),
        )
        row = cur.fetchone()

    return _row_to_store(row) if row else None


def list_stores(
    only_favorites: bool = False,
    order_by_priority: bool = True,
    limit: Optional[int] = None,
    include_archived: bool = False,
) -> List[Store]:
    """
    Return stores ordered by priority then name.
    Excludes archived stores by default; pass include_archived=True to include them.
    """
    conditions = []
    if only_favorites:
        conditions.append("is_favorite = 1")
    if not include_archived:
        conditions.append("is_active = 1")

    where_clause = ("WHERE " + " AND ".join(conditions)) if conditions else ""
    order_clause = "ORDER BY priority DESC, name ASC" if order_by_priority else "ORDER BY name ASC"
    limit_clause = " LIMIT ?" if limit is not None else ""

    query = (
        f"SELECT {_SELECT_COLS} FROM stores {where_clause} {order_clause}{limit_clause}"
    )

    with connection_scope() as conn, closing(conn.cursor()) as cur:
        if limit is None:
            cur.execute(query)
        else:
            cur.execute(query, (int(limit),))
        rows = cur.fetchall()

    return [_row_to_store(r) for r in rows]


def set_store_favorite(store_id: int, is_favorite: bool, priority: Optional[int] = None) -> None:
    """
    Mark a store as favorite / not favorite and optionally update its priority.
    """
    with connection_scope() as conn, closing(conn.cursor()) as cur:
        if priority is not None:
            cur.execute(
                """
                UPDATE stores
                SET is_favorite = ?, priority = ?
                WHERE id = ?
                """,
                (1 if is_favorite else 0, priority, store_id),
            )
        else:
            cur.execute(
                """
                UPDATE stores
                SET is_favorite = ?
                WHERE id = ?
                """,
                (1 if is_favorite else 0, store_id),
            )
        conn.commit()


def set_store_shop_here(store_id: int, shop_here: bool) -> None:
    """Mark whether the user actually shops at this store."""
    with connection_scope() as conn, closing(conn.cursor()) as cur:
        cur.execute(
            "UPDATE stores SET shop_here = ? WHERE id = ?",
            (1 if shop_here else 0, store_id),
        )
        conn.commit()


def set_store_distance_km(store_id: int, distance_km: Optional[float]) -> None:
    """Set the one-way driving distance in km for a store. None clears it."""
    val = float(distance_km) if distance_km is not None else None
    with connection_scope() as conn, closing(conn.cursor()) as cur:
        cur.execute(
            "UPDATE stores SET distance_km = ? WHERE id = ?",
            (val, store_id),
        )
        conn.commit()


def update_store_address(
    store_id: int,
    address: Optional[str] = None,
    city: Optional[str] = None,
    postal_code: Optional[str] = None,
) -> None:
    """
    Update address-related fields for a store.
    """
    with connection_scope() as conn, closing(conn.cursor()) as cur:
        cur.execute(
            """
            UPDATE stores
            SET address = ?, city = ?, postal_code = ?
            WHERE id = ?
            """,
            (address, city, postal_code, store_id),
        )
        conn.commit()


def update_store(
    store_id: int,
    *,
    name: str,
    address: Optional[str] = None,
    city: Optional[str] = None,
    postal_code: Optional[str] = None,
    flipp_store_id: Optional[str] = None,
    is_favorite: bool = False,
    priority: int = 0,
    notes: Optional[str] = None,
) -> None:
    """Full-row update for the Edit dialog (all editable fields except is_active)."""
    with connection_scope() as conn, closing(conn.cursor()) as cur:
        cur.execute(
            """
            UPDATE stores
            SET name = ?, address = ?, city = ?, postal_code = ?,
                flipp_store_id = ?, is_favorite = ?, priority = ?, notes = ?
            WHERE id = ?
            """,
            (
                name,
                address,
                city,
                postal_code,
                flipp_store_id,
                1 if is_favorite else 0,
                priority,
                notes,
                store_id,
            ),
        )
        conn.commit()


def set_store_active(store_id: int, is_active: bool) -> None:
    """Archive (is_active=False) or reactivate (is_active=True) a store."""
    with connection_scope() as conn, closing(conn.cursor()) as cur:
        cur.execute(
            "UPDATE stores SET is_active = ? WHERE id = ?",
            (1 if is_active else 0, store_id),
        )
        conn.commit()


def delete_store(store_id: int) -> None:
    """
    Hard delete a store. Never wired to the UI — used by tests only.
    """
    with connection_scope() as conn, closing(conn.cursor()) as cur:
        cur.execute("DELETE FROM stores WHERE id = ?", (store_id,))
        conn.commit()


# ---------- Flipp / external helpers ----------

def upsert_store_from_flipp(
    name: str,
    flipp_store_id: str,
    address: Optional[str] = None,
    city: Optional[str] = None,
    postal_code: Optional[str] = None,
) -> Store:
    """
    Ensure there is a Store row for a given Flipp store ID.
    If it exists, update basic info; otherwise, create it.
    """
    with connection_scope() as conn, closing(conn.cursor()) as cur:
        cur.execute(
            f"SELECT {_SELECT_COLS} FROM stores WHERE flipp_store_id = ?",
            (flipp_store_id,),
        )
        row = cur.fetchone()

        if row:
            # Update name/address if changed
            store = _row_to_store(row)
            if (
                store.name != name
                or store.address != address
                or store.city != city
                or store.postal_code != postal_code
            ):
                cur.execute(
                    """
                    UPDATE stores
                    SET name = ?, address = ?, city = ?, postal_code = ?
                    WHERE id = ?
                    """,
                    (name, address, city, postal_code, store.id),
                )
                conn.commit()
            return Store(
                id=store.id,
                name=name,
                address=address,
                city=city,
                postal_code=postal_code,
                flipp_store_id=flipp_store_id,
                is_favorite=store.is_favorite,
                priority=store.priority,
                is_active=store.is_active,
                notes=store.notes,
            )

        # Not found → create
        cur.execute(
            """
            INSERT INTO stores (
                name, address, city, postal_code,
                flipp_store_id, is_favorite, priority, notes, created_at
            )
            VALUES (?, ?, ?, ?, ?, 0, 0, NULL, ?)
            """,
            (
                name,
                address,
                city,
                postal_code,
                flipp_store_id,
                datetime.now(timezone.utc).isoformat(timespec="seconds"),
            ),
        )
        new_id = cur.lastrowid

        cur.execute(
            f"SELECT {_SELECT_COLS} FROM stores WHERE id = ?",
            (new_id,),
        )
        new_row = cur.fetchone()

    return _row_to_store(new_row)
