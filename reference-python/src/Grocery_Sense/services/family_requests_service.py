"""
Grocery_Sense.services.family_requests_service

"Family picks": a household member (typically a kid / secondary) picks a meal
or an item; it lands on the shared shopping list attributed to them, and the
parent (master) gets a notification + review queue.

Design notes:
- No approval gate. Picks add to the list immediately; the parent reviews
  *after* (can remove). The unreviewed request count drives the parent badge.
- A request row is created only when the picker is a SECONDARY member, so the
  master adding their own things never self-notifies. Master picks still add to
  the list (so the feature is uniform), they just don't create a review item.
  The role decision lives here (single site) so the UI stays dumb.
- Hard excludes / allergies are household-wide and already enforced by
  preferences_service.get_meal_profile(); pickable_recipes() reuses that so a
  kid can never pick a recipe containing a household allergen.
"""

from __future__ import annotations

from typing import List, Optional

from Grocery_Sense.config import config_store
from Grocery_Sense.data.repositories import member_requests_repo
from Grocery_Sense.data.repositories.member_requests_repo import MemberRequestRow
from Grocery_Sense.recipes import recipe_engine
from Grocery_Sense.services import preferences_service
from Grocery_Sense.services.shopping_list_service import ShoppingListService

# add_single_item is a thin wrapper today; reuse it so a future auto_map upgrade
# flows through here too.
_shopping = ShoppingListService()


def _member_name(member_id: int) -> str:
    m = config_store.get_member(member_id)
    return m.name if m else f"Member {member_id}"


def _is_secondary(member_id: int) -> bool:
    m = config_store.get_member(member_id)
    return bool(m and m.role != config_store.ROLE_MASTER)


def pick_meal(member_id: int, recipe_name: str) -> Optional[MemberRequestRow]:
    """Add a recipe's ingredients to the shared list, attributed to the member.

    Returns the created request (secondary picker) or None (master picker).
    Raises ValueError if the recipe name is unknown — fail loud rather than add
    an empty pick.
    """
    recipe = recipe_engine.get_recipe_by_name(recipe_name)
    if recipe is None:
        raise ValueError(f"Unknown recipe: {recipe_name!r}")

    name = _member_name(member_id)
    ingredients = [str(i).strip() for i in (recipe.get("ingredients") or []) if str(i).strip()]

    row_ids: List[int] = []
    for ing in ingredients:
        row_ids.append(
            _shopping.add_single_item(
                name=ing,
                quantity=1.0,
                unit="each",
                notes=f"Family pick: {recipe_name}",
                added_by=name,
                added_by_member_id=member_id,
                auto_map=True,
            )
        )

    if not _is_secondary(member_id):
        return None

    req_id = member_requests_repo.add_request(
        member_id=member_id,
        member_name=name,
        kind="meal",
        label=recipe_name,
        item_row_ids=row_ids,
    )
    return member_requests_repo.get_request(req_id)


def pick_item(
    member_id: int,
    text: str,
    *,
    quantity: float = 1.0,
    unit: str = "each",
) -> Optional[MemberRequestRow]:
    """Add a single item to the shared list, attributed to the member.

    Returns the created request (secondary picker) or None (master picker).
    """
    label = (text or "").strip()
    if not label:
        raise ValueError("Item text is required.")

    name = _member_name(member_id)
    row_id = _shopping.add_single_item(
        name=label,
        quantity=quantity,
        unit=unit or "each",
        notes="Family pick",
        added_by=name,
        added_by_member_id=member_id,
        auto_map=True,
    )

    if not _is_secondary(member_id):
        return None

    req_id = member_requests_repo.add_request(
        member_id=member_id,
        member_name=name,
        kind="item",
        label=label,
        item_row_ids=[row_id],
    )
    return member_requests_repo.get_request(req_id)


def pickable_recipes() -> List[str]:
    """Recipe names a member may pick: household hard excludes / allergies hidden.

    Household-wide (allergies block for everyone), so no per-member arg. Soft
    excludes do NOT filter — a kid can still pick those.
    """
    recipes = recipe_engine.load_all_recipes()
    if not recipes:
        return []

    all_ingredients = {
        str(i).strip()
        for r in recipes
        for i in (r.get("ingredients") or [])
        if str(i).strip()
    }
    profile = preferences_service.get_meal_profile()
    # include_ingredients = every ingredient → every recipe overlaps, so this
    # returns all recipes that survive the hard-profile filter.
    passing = recipe_engine.filter_recipes_by_ingredients_and_profile(
        include_ingredients=all_ingredients,
        profile=profile,
        max_results=len(recipes),
    )
    names = [str(r.get("name", "")).strip() for r in passing if str(r.get("name", "")).strip()]
    return sorted(names, key=str.lower)


# --- thin pass-throughs for the UI ----------------------------------------

def unreviewed_count() -> int:
    return member_requests_repo.count_unreviewed()


def list_unreviewed() -> List[MemberRequestRow]:
    return member_requests_repo.list_unreviewed()


def mark_reviewed(request_id: int) -> None:
    member_requests_repo.mark_reviewed(request_id)


def remove_request(request_id: int) -> None:
    """Soft-delete the shopping_list rows this pick created, then mark reviewed."""
    req = member_requests_repo.get_request(request_id)
    if req is None:
        return
    for row_id in req.item_row_ids:
        _shopping.soft_delete_item(row_id)
    member_requests_repo.mark_reviewed(request_id)
