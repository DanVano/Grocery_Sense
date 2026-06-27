"""
list_recent_receipts optional filters: store_id + purchase_date range.

Guards the Receipt Browser filter — wrong WHERE/param wiring would either
ignore the filter or crash on the dynamic SQL.
"""
from __future__ import annotations

from Grocery_Sense.data.connection import get_connection
from Grocery_Sense.data.repositories.receipts_repo import list_recent_receipts
from Grocery_Sense.data.repositories.stores_repo import create_store


def _insert_receipt(store_id: int, purchase_date: str) -> int:
    with get_connection() as c:
        cur = c.execute(
            """
            INSERT INTO receipts
                (store_id, purchase_date, subtotal_amount, tax_amount, total_amount,
                 source, file_path)
            VALUES (?, ?, 9.0, 1.0, 10.0, 'receipt', '/tmp/r.pdf')
            """,
            (store_id, purchase_date),
        )
        rid = int(cur.lastrowid)
        c.commit()
    return rid


def test_filter_by_store_and_date_range(isolated_db):
    a = create_store("Alpha")
    b = create_store("Beta")
    _insert_receipt(a.id, "2026-01-10")
    _insert_receipt(a.id, "2026-03-15")
    _insert_receipt(b.id, "2026-03-20")

    # No filter → all three
    assert len(list_recent_receipts()) == 3

    # Store filter
    alpha = list_recent_receipts(store_id=a.id)
    assert {r["store_id"] for r in alpha} == {a.id}
    assert len(alpha) == 2

    # Date range (inclusive) — only March rows
    march = list_recent_receipts(since="2026-03-01", until="2026-03-31")
    assert len(march) == 2
    assert all(r["purchase_date"].startswith("2026-03") for r in march)

    # Combined store + date
    combo = list_recent_receipts(store_id=a.id, since="2026-03-01")
    assert len(combo) == 1
    assert combo[0]["store_id"] == a.id
    assert combo[0]["purchase_date"] == "2026-03-15"
