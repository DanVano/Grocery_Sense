"""family_requests_service — member picks meals/items, parent reviews.

Uses the autouse isolated_db (temp SQLite) plus a local tmp_config_file fixture
(temp household JSON) and a temp recipes.json wired into the recipe engine, so
nothing touches real user data or the shipped recipe catalog.
"""

from __future__ import annotations

import json

import pytest

from Grocery_Sense.config import config_store
from Grocery_Sense.config.config_store import ROLE_SECONDARY, add_member, get_master_member, save_member_profile
from Grocery_Sense.data.repositories import member_requests_repo, shopping_list_repo
from Grocery_Sense.recipes import recipe_engine
from Grocery_Sense.recipes.recipe_engine import RecipeEngine
from Grocery_Sense.services import family_requests_service


@pytest.fixture(autouse=True)
def tmp_config_file(tmp_path, monkeypatch):
    """Redirect config_store at a temp household file and clear its caches."""
    monkeypatch.setattr(config_store, "_CONFIG_FILE", tmp_path / "user_config.json")
    monkeypatch.setattr(config_store, "_CACHE_FILE", tmp_path / "deals_cache.json")
    monkeypatch.setattr(config_store, "_config_cache", None)
    monkeypatch.setattr(config_store, "_config_mtime_key", None)
    from Grocery_Sense.services import preferences_service
    preferences_service._invalidate_effective_cache()
    yield


@pytest.fixture(autouse=True)
def tmp_recipes(tmp_path, monkeypatch):
    """Point the recipe engine at a tiny deterministic catalog."""
    recipes = [
        {"name": "Chicken Rice", "ingredients": ["chicken", "rice"], "tags": ["chicken"]},
        {"name": "Peanut Stir Fry", "ingredients": ["peanuts", "noodles"], "tags": ["vegetarian"]},
    ]
    path = tmp_path / "recipes.json"
    path.write_text(json.dumps(recipes), encoding="utf-8")
    monkeypatch.setattr(recipe_engine, "_default_engine", RecipeEngine(path))
    yield


def _secondary(name="Emma"):
    get_master_member()  # ensure a default household + master exist
    return add_member(name, role=ROLE_SECONDARY)


def test_pick_meal_adds_ingredients_and_creates_request():
    emma = _secondary()

    req = family_requests_service.pick_meal(emma.id, "Chicken Rice")

    rows = shopping_list_repo.list_all_items()
    assert {r.display_name for r in rows} == {"chicken", "rice"}
    assert all(r.added_by_member_id == emma.id for r in rows)
    assert all(r.notes == "Family pick: Chicken Rice" for r in rows)

    assert req is not None
    assert req.kind == "meal"
    assert req.label == "Chicken Rice"
    assert sorted(req.item_row_ids) == sorted(r.id for r in rows)
    assert family_requests_service.unreviewed_count() == 1


def test_pick_item_adds_one_row_and_one_request():
    emma = _secondary()

    req = family_requests_service.pick_item(emma.id, "cookies")

    rows = shopping_list_repo.list_all_items()
    assert [r.display_name for r in rows] == ["cookies"]
    assert req is not None and req.kind == "item" and req.label == "cookies"
    assert req.item_row_ids == [rows[0].id]
    assert family_requests_service.unreviewed_count() == 1


def test_master_pick_adds_but_creates_no_request():
    master = get_master_member()

    req = family_requests_service.pick_item(master.id, "beer")

    assert req is None  # master adds never self-notify
    assert [r.display_name for r in shopping_list_repo.list_all_items()] == ["beer"]
    assert family_requests_service.unreviewed_count() == 0


def test_pickable_recipes_hides_household_allergen():
    get_master_member()
    master = get_master_member()
    save_member_profile(master.id, {"allergies": ["peanuts"]})

    names = family_requests_service.pickable_recipes()

    assert "Chicken Rice" in names
    assert "Peanut Stir Fry" not in names  # peanuts allergy → hard household exclude


def test_remove_request_soft_deletes_rows_and_marks_reviewed():
    emma = _secondary()
    req = family_requests_service.pick_meal(emma.id, "Chicken Rice")
    assert len(shopping_list_repo.list_active_items()) == 2

    family_requests_service.remove_request(req.id)

    assert shopping_list_repo.list_active_items() == []
    assert family_requests_service.unreviewed_count() == 0
    assert member_requests_repo.get_request(req.id).reviewed is True
