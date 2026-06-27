"""
Phase 1 — recipe catalog + per-serving cost tests.

#1: recipes.json exists, parses, every entry is valid.
#2: _compute_cost_estimate math, ratio, None-when-unknown.
"""
from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Dict, Optional

import pytest

CATALOG_PATH = (
    Path(__file__).resolve().parents[2]
    / "src" / "Grocery_Sense" / "recipes" / "recipes.json"
)


# ---------------------------------------------------------------------------
# #1 — Catalog integrity
# ---------------------------------------------------------------------------

def test_catalog_exists():
    assert CATALOG_PATH.exists(), f"recipes.json not found at {CATALOG_PATH}"


def test_catalog_parses():
    data = json.loads(CATALOG_PATH.read_text(encoding="utf-8"))
    assert isinstance(data, list), "recipes.json must be a list"
    assert len(data) >= 50, f"Expected at least 50 recipes, got {len(data)}"


def test_catalog_ids_unique():
    data = json.loads(CATALOG_PATH.read_text(encoding="utf-8"))
    ids = [r.get("id") for r in data if r.get("id") is not None]
    assert len(ids) == len(set(ids)), "Duplicate recipe IDs found"


def test_every_recipe_has_required_fields():
    data = json.loads(CATALOG_PATH.read_text(encoding="utf-8"))
    for r in data:
        rid = r.get("id", "?")
        assert r.get("name"), f"Recipe id={rid} missing name"
        ings = r.get("ingredients")
        assert isinstance(ings, list) and len(ings) >= 1, (
            f"Recipe id={rid} has empty or non-list ingredients"
        )
        servings = r.get("servings")
        assert isinstance(servings, int) and servings > 0, (
            f"Recipe id={rid} missing or invalid servings"
        )


def test_no_blank_ingredients():
    data = json.loads(CATALOG_PATH.read_text(encoding="utf-8"))
    for r in data:
        rid = r.get("id", "?")
        for ing in r.get("ingredients", []):
            assert str(ing).strip(), f"Recipe id={rid} has blank ingredient"


# ---------------------------------------------------------------------------
# #2 — Per-serving cost estimate
# ---------------------------------------------------------------------------

from Grocery_Sense.services.meal_suggestion_service import _compute_cost_estimate  # noqa: E402


def _recipe(servings: int, ingredients: list) -> Dict[str, Any]:
    return {"id": 99, "name": "Test", "servings": servings, "ingredients": ingredients}


def test_cost_all_known():
    baseline = {"chicken thighs": 5.00, "rice": 2.00, "garlic": 1.00}
    recipe = _recipe(4, ["chicken thighs", "rice", "garlic"])
    total, per_serving, ratio = _compute_cost_estimate(recipe, baseline)
    assert total == pytest.approx(8.00)
    assert per_serving == pytest.approx(2.00)
    assert ratio == pytest.approx(1.0)


def test_cost_partial_known():
    baseline = {"chicken thighs": 5.00}
    recipe = _recipe(4, ["chicken thighs", "rice", "garlic"])
    total, per_serving, ratio = _compute_cost_estimate(recipe, baseline)
    assert total == pytest.approx(5.00)
    assert per_serving == pytest.approx(1.25)
    assert ratio == pytest.approx(1 / 3)


def test_cost_none_known():
    recipe = _recipe(4, ["exotic ingredient", "mystery spice"])
    total, per_serving, ratio = _compute_cost_estimate(recipe, {})
    assert total is None
    assert per_serving is None
    assert ratio == pytest.approx(0.0)


def test_cost_no_ingredients():
    recipe = _recipe(4, [])
    total, per_serving, ratio = _compute_cost_estimate(recipe, {"rice": 2.00})
    assert total is None
    assert per_serving is None
    assert ratio == pytest.approx(0.0)


def test_cost_no_servings():
    baseline = {"rice": 2.00}
    recipe = {"id": 1, "name": "Test", "ingredients": ["rice"]}  # no servings key
    total, per_serving, ratio = _compute_cost_estimate(recipe, baseline)
    assert total == pytest.approx(2.00)
    assert per_serving is None   # can't divide without servings
    assert ratio == pytest.approx(1.0)
