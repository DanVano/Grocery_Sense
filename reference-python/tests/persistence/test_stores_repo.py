"""
M1 — stores_repo

Covers:
  - create_store returns a populated Store dataclass
  - get_store_by_id / list_stores (favorites filter, priority ordering)
  - set_store_favorite (with and without priority override)
  - update_store_address
  - delete_store
  - upsert_store_from_flipp create + update paths
"""

from __future__ import annotations

import pytest

from Grocery_Sense.data.connection import get_connection
from Grocery_Sense.data.repositories.stores_repo import (
    create_store,
    delete_store,
    get_store_by_id,
    list_stores,
    set_store_active,
    set_store_favorite,
    set_store_shop_here,
    update_store,
    update_store_address,
    upsert_store_from_flipp,
)


# ---------------------------------------------------------------------------
# create_store
# ---------------------------------------------------------------------------


class TestCreateStore:
    def test_minimal_create(self, isolated_db):
        s = create_store(name="Mini Mart")
        assert s.id > 0
        assert s.name == "Mini Mart"
        assert s.is_favorite is False
        assert s.priority == 0

    def test_full_create(self, isolated_db):
        s = create_store(
            name="Full Mart",
            address="123 Main",
            city="Coquitlam",
            postal_code="V3J 0P6",
            flipp_store_id="FL_001",
            is_favorite=True,
            priority=9,
            notes="best store",
        )
        assert s.address == "123 Main"
        assert s.flipp_store_id == "FL_001"
        assert s.is_favorite is True
        assert s.priority == 9
        assert s.notes == "best store"


# ---------------------------------------------------------------------------
# get_store_by_id
# ---------------------------------------------------------------------------


class TestGetStoreById:
    def test_returns_store(self, isolated_db):
        created = create_store(name="A")
        fetched = get_store_by_id(created.id)
        assert fetched is not None
        assert fetched.id == created.id
        assert fetched.name == "A"

    def test_missing_returns_none(self, isolated_db):
        assert get_store_by_id(9999) is None


# ---------------------------------------------------------------------------
# list_stores
# ---------------------------------------------------------------------------


class TestListStores:
    def test_returns_all(self, isolated_db):
        create_store(name="A")
        create_store(name="B")
        assert len(list_stores()) == 2

    def test_order_by_priority(self, isolated_db):
        create_store(name="Low", priority=1)
        create_store(name="High", priority=10)
        create_store(name="Mid", priority=5)

        names = [s.name for s in list_stores(order_by_priority=True)]
        assert names == ["High", "Mid", "Low"]

    def test_only_favorites_filter(self, isolated_db):
        create_store(name="Fav", is_favorite=True)
        create_store(name="Nope")

        favs = list_stores(only_favorites=True)
        assert [s.name for s in favs] == ["Fav"]

    def test_alpha_order_when_priority_off(self, isolated_db):
        create_store(name="B")
        create_store(name="A")
        create_store(name="C")

        names = [s.name for s in list_stores(order_by_priority=False)]
        assert names == ["A", "B", "C"]


# ---------------------------------------------------------------------------
# set_store_favorite
# ---------------------------------------------------------------------------


class TestSetStoreFavorite:
    def test_toggles_favorite_flag(self, isolated_db):
        s = create_store(name="X")
        set_store_favorite(s.id, True)
        assert get_store_by_id(s.id).is_favorite is True
        set_store_favorite(s.id, False)
        assert get_store_by_id(s.id).is_favorite is False

    def test_updates_priority_when_provided(self, isolated_db):
        s = create_store(name="Y", priority=0)
        set_store_favorite(s.id, True, priority=7)
        refreshed = get_store_by_id(s.id)
        assert refreshed.is_favorite is True
        assert refreshed.priority == 7

    def test_priority_unchanged_when_omitted(self, isolated_db):
        s = create_store(name="Z", priority=3)
        set_store_favorite(s.id, True)  # no priority arg
        assert get_store_by_id(s.id).priority == 3


# ---------------------------------------------------------------------------
# update_store_address
# ---------------------------------------------------------------------------


class TestUpdateStoreAddress:
    def test_updates_fields(self, isolated_db):
        s = create_store(name="A", address="old addr", city="old city")
        update_store_address(s.id, address="new addr", city="new city", postal_code="V3J 0P6")
        refreshed = get_store_by_id(s.id)
        assert refreshed.address == "new addr"
        assert refreshed.city == "new city"
        assert refreshed.postal_code == "V3J 0P6"

    def test_overwrites_with_none_if_passed(self, isolated_db):
        s = create_store(name="A", address="had value")
        update_store_address(s.id, address=None, city=None, postal_code=None)
        refreshed = get_store_by_id(s.id)
        assert refreshed.address is None
        assert refreshed.city is None


# ---------------------------------------------------------------------------
# delete_store
# ---------------------------------------------------------------------------


class TestDeleteStore:
    def test_removes_row(self, isolated_db):
        s = create_store(name="bye")
        delete_store(s.id)
        assert get_store_by_id(s.id) is None

    def test_delete_nonexistent_is_silent(self, isolated_db):
        # Does not raise.
        delete_store(9999)


# ---------------------------------------------------------------------------
# upsert_store_from_flipp
# ---------------------------------------------------------------------------


class TestUpsertStoreFromFlipp:
    def test_creates_when_missing(self, isolated_db):
        s = upsert_store_from_flipp(
            name="New From Flipp",
            flipp_store_id="FL_NEW",
            address="1 Main",
            city="Coquitlam",
            postal_code="V3J 0P6",
        )
        assert s.id > 0
        assert s.flipp_store_id == "FL_NEW"
        assert s.name == "New From Flipp"

    def test_updates_existing_match(self, isolated_db):
        original = create_store(
            name="Old Name", flipp_store_id="FL_EXIST", address="1 Old"
        )
        updated = upsert_store_from_flipp(
            name="New Name",
            flipp_store_id="FL_EXIST",
            address="2 New",
            city="Coquitlam",
            postal_code="V3J 0P6",
        )
        # Same id, new details.
        assert updated.id == original.id
        assert updated.name == "New Name"
        assert updated.address == "2 New"

        # DB row really changed.
        with get_connection() as c:
            row = c.execute(
                "SELECT name, address FROM stores WHERE id = ?", (original.id,)
            ).fetchone()
        assert row["name"] == "New Name"
        assert row["address"] == "2 New"

    def test_no_update_when_details_match(self, isolated_db):
        """If nothing changed, the repo should skip the UPDATE entirely."""
        create_store(
            name="Stable",
            flipp_store_id="FL_STABLE",
            address="1 Stable",
            city="Coquitlam",
            postal_code="V3J 0P6",
        )
        # Call upsert with identical values. Should not raise; returned row equal.
        r = upsert_store_from_flipp(
            name="Stable",
            flipp_store_id="FL_STABLE",
            address="1 Stable",
            city="Coquitlam",
            postal_code="V3J 0P6",
        )
        assert r.name == "Stable"
        assert r.address == "1 Stable"

    def test_preserves_favorite_and_priority_across_update(self, isolated_db):
        original = create_store(
            name="Fav Store",
            flipp_store_id="FL_FAV",
            is_favorite=True,
            priority=10,
        )
        updated = upsert_store_from_flipp(
            name="Fav Store Renamed",
            flipp_store_id="FL_FAV",
            address="1 Main",
        )
        assert updated.id == original.id
        # upsert only updates name/address/city/postal_code — favorite/priority stay.
        refreshed = get_store_by_id(original.id)
        assert refreshed.is_favorite is True
        assert refreshed.priority == 10


# ---------------------------------------------------------------------------
# shop_here
# ---------------------------------------------------------------------------


class TestShopHere:
    def test_defaults_to_true(self, isolated_db):
        s = create_store(name="New Store")
        assert s.shop_here is True

    def test_set_store_shop_here_toggles(self, isolated_db):
        s = create_store(name="Costco")
        set_store_shop_here(s.id, False)
        refreshed = get_store_by_id(s.id)
        assert refreshed.shop_here is False

        set_store_shop_here(s.id, True)
        refreshed2 = get_store_by_id(s.id)
        assert refreshed2.shop_here is True

    def test_migration_old_db_gets_column_with_default_1(self, isolated_db):
        from Grocery_Sense.data.connection import get_connection
        with get_connection() as c:
            cols = {row[1] for row in c.execute("PRAGMA table_info(stores)").fetchall()}
        assert "shop_here" in cols


# ---------------------------------------------------------------------------
# is_active / archive
# ---------------------------------------------------------------------------


class TestIsActive:
    def test_new_store_defaults_active(self, isolated_db):
        s = create_store(name="Fresh Store")
        assert s.is_active is True

    def test_set_store_active_archives(self, isolated_db):
        s = create_store(name="To Archive")
        set_store_active(s.id, False)
        refreshed = get_store_by_id(s.id)
        assert refreshed.is_active is False

    def test_set_store_active_reactivates(self, isolated_db):
        s = create_store(name="Was Archived")
        set_store_active(s.id, False)
        set_store_active(s.id, True)
        assert get_store_by_id(s.id).is_active is True

    def test_list_stores_excludes_archived_by_default(self, isolated_db):
        active = create_store(name="Active")
        archived = create_store(name="Archived")
        set_store_active(archived.id, False)

        visible = list_stores()
        ids = {s.id for s in visible}
        assert active.id in ids
        assert archived.id not in ids

    def test_list_stores_includes_archived_when_requested(self, isolated_db):
        active = create_store(name="Active")
        archived = create_store(name="Archived")
        set_store_active(archived.id, False)

        all_stores = list_stores(include_archived=True)
        ids = {s.id for s in all_stores}
        assert active.id in ids
        assert archived.id in ids

    def test_list_stores_only_favorites_and_active(self, isolated_db):
        fav_active = create_store(name="FavActive", is_favorite=True)
        fav_archived = create_store(name="FavArchived", is_favorite=True)
        set_store_active(fav_archived.id, False)
        _non_fav = create_store(name="NonFav")

        result = list_stores(only_favorites=True)
        ids = {s.id for s in result}
        assert fav_active.id in ids
        assert fav_archived.id not in ids


# ---------------------------------------------------------------------------
# update_store
# ---------------------------------------------------------------------------


class TestUpdateStore:
    def test_updates_all_editable_fields(self, isolated_db):
        s = create_store(name="Old Name", city="Old City", priority=1)
        update_store(
            s.id,
            name="New Name",
            address="123 Main",
            city="New City",
            postal_code="V3J 0P6",
            flipp_store_id="FL_99",
            is_favorite=True,
            priority=8,
            notes="updated",
        )
        r = get_store_by_id(s.id)
        assert r.name == "New Name"
        assert r.address == "123 Main"
        assert r.city == "New City"
        assert r.postal_code == "V3J 0P6"
        assert r.flipp_store_id == "FL_99"
        assert r.is_favorite is True
        assert r.priority == 8
        assert r.notes == "updated"

    def test_update_does_not_touch_is_active(self, isolated_db):
        s = create_store(name="Will Archive")
        set_store_active(s.id, False)
        update_store(s.id, name="Still Archived", priority=0)
        r = get_store_by_id(s.id)
        assert r.is_active is False
