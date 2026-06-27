from __future__ import annotations

from datetime import date, timedelta

from Grocery_Sense.data.repositories import stores_repo, shopping_list_repo
from Grocery_Sense.data.repositories.items_repo import create_item
from Grocery_Sense.data.repositories.prices_repo import add_price_point
from Grocery_Sense.services import list_audit_service


def _d(days_ago: int) -> str:
    return (date.today() - timedelta(days=days_ago)).isoformat()


def _seed_item_with_prices(name: str, store_id: int, *, baseline: float, current: float) -> int:
    """Item with 3 old baseline prices (sets 'usual') and one current price."""
    item = create_item(canonical_name=name, default_unit="each")
    for n in (60, 50, 40):
        add_price_point(
            item_id=item.id, store_id=store_id, unit_price=baseline, unit="each",
            source="receipt", date=_d(n),
        )
    add_price_point(
        item_id=item.id, store_id=store_id, unit_price=current, unit="each",
        source="receipt", date=_d(1),
    )
    return item.id


def test_audit_flags_overpay_and_savings():
    store = stores_repo.create_store(name="Test Mart")

    overpay_id = _seed_item_with_prices("Widget A", store.id, baseline=10.0, current=13.0)
    deal_id = _seed_item_with_prices("Widget B", store.id, baseline=10.0, current=8.0)

    shopping_list_repo.add_item(display_name="Widget A", quantity=1.0, unit="each", item_id=overpay_id)
    shopping_list_repo.add_item(display_name="Widget B", quantity=1.0, unit="each", item_id=deal_id)
    shopping_list_repo.add_item(display_name="Mystery Thing", quantity=1.0, unit="each")  # no item_id

    audit = list_audit_service.audit_active_list()

    assert audit["priced_count"] == 2
    assert "Mystery Thing" in audit["unmatched"]

    by_name = {li["name"]: li for li in audit["line_items"]}
    assert by_name["Widget A"]["classification"] == "expensive"
    assert by_name["Widget B"]["classification"] in ("good", "great")

    overpay_names = {li["name"] for li in audit["overpay_items"]}
    assert overpay_names == {"Widget A"}

    # Widget A is $3 over usual, Widget B is $2 under -> net savings = -1.
    assert audit["overpay_excess"] == 13.0 - 10.0
    assert audit["savings_vs_usual"] == (10.0 - 13.0) + (10.0 - 8.0)
    assert audit["estimated_total"] == 13.0 + 8.0


def test_audit_empty_list_is_safe():
    audit = list_audit_service.audit_active_list()
    assert audit["priced_count"] == 0
    assert audit["line_items"] == []
    assert audit["overpay_items"] == []
