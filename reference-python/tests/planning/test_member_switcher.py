"""
Phase 2A — member switcher plumbing.

Verifies that add_single_item correctly stores added_by_member_id in the DB.
Does NOT test the Tkinter widget (no display); tests the service + repo layer.
"""
from __future__ import annotations

import pytest

from Grocery_Sense.services.shopping_list_service import ShoppingListService
from Grocery_Sense.data.repositories import shopping_list_repo


def test_add_item_stores_member_id(isolated_db):
    svc = ShoppingListService()
    row_id = svc.add_single_item(
        name="Milk",
        quantity=2.0,
        unit="L",
        added_by="Mom",
        added_by_member_id=2,
    )
    items = svc.get_active_items()
    match = next((i for i in items if i.id == row_id), None)
    assert match is not None
    assert match.added_by_member_id == 2
    assert match.added_by == "Mom"


def test_add_item_no_member_id_is_fine(isolated_db):
    svc = ShoppingListService()
    row_id = svc.add_single_item(name="Eggs", quantity=1.0, unit="dozen")
    items = svc.get_active_items()
    match = next((i for i in items if i.id == row_id), None)
    assert match is not None
    assert match.added_by_member_id is None


def test_member_id_persists_across_refresh(isolated_db):
    svc = ShoppingListService()
    svc.add_single_item(name="Butter", quantity=1.0, unit="each", added_by_member_id=3)
    svc.add_single_item(name="Cheese", quantity=1.0, unit="each", added_by_member_id=1)

    items = svc.get_active_items()
    by_name = {i.display_name: i for i in items}
    assert by_name["Butter"].added_by_member_id == 3
    assert by_name["Cheese"].added_by_member_id == 1
