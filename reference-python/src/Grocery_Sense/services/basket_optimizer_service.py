from __future__ import annotations

import datetime as _dt
import sqlite3
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional, Tuple

# Repos (DB)
from Grocery_Sense.data.repositories import shopping_list_repo, stores_repo, prices_repo
from Grocery_Sense.data.repositories.prices_repo import (
    get_most_recent_prices_by_store_batch,
    get_most_recent_prices_global_batch,
    get_price_stats_batch,
)
from Grocery_Sense.services.budget_service import get_gas_cost_per_km

# Preferences (optional; code fails-safe if not present)
try:
    from Grocery_Sense.services import preferences_service
    from Grocery_Sense.config import config_store
except Exception:  # pragma: no cover
    preferences_service = None  # type: ignore
    config_store = None  # type: ignore


# ---------------------------------------------------------------------------
# Small "phrase-safe" matcher (reduces false positives)
# ---------------------------------------------------------------------------

# Things we never want preference excludes to accidentally "hit"
# (Ex: "olives" should NOT flag "olive oil")
DEFAULT_EXCLUDE_SAFE_PHRASES: List[str] = [
    "olive oil",
]

_FLAT_TRIP_PENALTY = 6.0  # fallback when distance_km is unknown


def _compute_trip_penalty(store_a, store_b) -> float:
    """
    Return the cost penalty for adding a second stop.

    If either store has distance_km set, we charge: 2 * max_distance * gas_cost_per_km
    (round-trip to the farther store). Falls back to _FLAT_TRIP_PENALTY when
    distance is unknown — the warning in BasketOptimizationResult discloses this.
    """
    try:
        gas_rate = get_gas_cost_per_km()
    except Exception:
        gas_rate = 0.18

    d_a = getattr(store_a, "distance_km", None)
    d_b = getattr(store_b, "distance_km", None)

    distances = [d for d in (d_a, d_b) if d is not None and d > 0]
    if not distances:
        return _FLAT_TRIP_PENALTY  # ponytail: flat until distances are entered

    # Charge for a round-trip to the farther store (detour cost).
    return 2.0 * max(distances) * gas_rate


def _positive(v: object) -> bool:
    """Return True only for a strictly positive numeric price."""
    return isinstance(v, (int, float)) and v > 0

def _norm(s: str) -> str:
    return (s or "").strip().lower()

def _today_date() -> _dt.date:
    return _dt.date.today()

def _parse_date(s: Any) -> Optional[_dt.date]:
    try:
        if isinstance(s, _dt.date):
            return s
        t = str(s).strip()
        if not t:
            return None
        # Accept YYYY-MM-DD (most common)
        return _dt.date.fromisoformat(t[:10])
    except Exception:
        return None

def phrase_safe_hit(
    text: str,
    term: str,
    *,
    safe_phrases: Optional[List[str]] = None,
) -> bool:
    """
    Returns True if `term` is a meaningful phrase hit in `text`,
    while trying to avoid obvious false positives.
    """
    txt = _norm(text)
    trm = _norm(term)
    if not txt or not trm:
        return False

    safe_phrases = safe_phrases or DEFAULT_EXCLUDE_SAFE_PHRASES
    for sp in safe_phrases:
        if _norm(sp) and _norm(sp) in txt and trm in {"olive", "olives"}:
            # If the text explicitly contains "olive oil", don't treat "olive/olives" as a hit.
            return False

    # Whole-word-ish match for single tokens
    if " " not in trm:
        # quick boundary check without importing regex
        # pad with spaces and replace punctuation-ish with spaces
        cleaned = "".join(ch if ch.isalnum() else " " for ch in txt)
        tokens = [t for t in cleaned.split() if t]
        return trm in tokens

    # Phrase match for multi-word terms
    return trm in txt


# ---------------------------------------------------------------------------
# Data models
# ---------------------------------------------------------------------------

@dataclass
class PricePick:
    store_id: int
    store_name: str
    unit_price: Optional[float]   # None => unknown
    unit: str
    source: str                  # "flyer" | "history_store" | "history_any" | "unknown"


@dataclass
class BasketItemPlan:
    item_id: int
    name: str
    quantity: float
    unit: str

    # preference annotations (for UI tooltips)
    starred: bool = False
    hard_excluded: bool = False
    soft_hits: List[Tuple[str, List[str]]] = field(default_factory=list)  # [(ingredient_hit, [members])]

    # pricing
    chosen: Optional[PricePick] = None
    usual_avg_unit_price_180d: Optional[float] = None
    lowest_unit_price_180d: Optional[float] = None


@dataclass
class StorePlan:
    store_id: int
    store_name: str
    items: List[BasketItemPlan] = field(default_factory=list)
    total_estimated: float = 0.0
    unknown_count: int = 0


@dataclass
class BasketOptimizationResult:
    mode: str  # "one_store" | "two_store"
    stores: List[StorePlan] = field(default_factory=list)

    basket_total_estimated: float = 0.0
    basket_usual_avg_estimated: Optional[float] = None
    basket_lowest_estimated: Optional[float] = None

    save_vs_usual_avg: Optional[float] = None
    save_vs_lowest: Optional[float] = None

    warnings: List[str] = field(default_factory=list)

    # Items that match a household HARD exclude / allergy. They are kept OUT of
    # the optimized buy plan (not priced, not assigned, not in totals) and
    # surfaced here so the UI can flag them rather than silently route the user
    # to buy an allergen.
    excluded_items: List[BasketItemPlan] = field(default_factory=list)


# ---------------------------------------------------------------------------
# Basket optimizer service
# ---------------------------------------------------------------------------

class BasketOptimizerService:
    """
    Milestone 3:
    - Uses your ACTIVE shopping list as the basket
    - Considers ONLY stores in your DB (stores table)
    - If flyer data exists (prices.source='flyer' joined to flyer_sources with active valid_from/to),
      it uses that for estimates; otherwise it falls back to most recent historical prices.
    - Computes:
        * Estimated total for 1-store (fast trip) or max-2-store (savings mode)
        * You save $X vs usual basket (avg over last 6 months / 180 days)
        * You save $Y vs lowest price seen (last 6 months / 180 days)
    - Stars soft-excluded items and provides "why" details (ingredient hit + member list)
    """

    def __init__(self) -> None:
        pass

    def optimize(self, *, mode: str = "two_store") -> BasketOptimizationResult:
        """
        mode:
          - "one_store" => pick the single best store
          - "two_store" => pick up to two stores (savings mode)
        """
        mode = (mode or "").strip().lower()
        if mode not in {"one_store", "two_store"}:
            mode = "two_store"

        basket_items = shopping_list_repo.list_active_items()
        all_stores = stores_repo.list_stores()
        stores = [s for s in all_stores if s.shop_here]

        result = BasketOptimizationResult(mode=mode)

        if not stores:
            stores = all_stores
            if stores:
                result.warnings.append(
                    "No stores marked 'Shop here' — using all stores. "
                    "Use Store Settings to select where you shop."
                )

        if not basket_items:
            result.warnings.append("Your active shopping list is empty.")
            return result

        if not stores:
            result.warnings.append("No stores found in your database. Add stores first.")
            return result

        # Build preference context (fail-safe)
        eff = None
        safe_phrases = list(DEFAULT_EXCLUDE_SAFE_PHRASES)
        if preferences_service is not None:
            try:
                eff = preferences_service.compute_effective_preferences()
            except Exception:
                eff = None

        # Normalize basket items and precompute stats
        normalized: List[BasketItemPlan] = []
        excluded: List[BasketItemPlan] = []
        for it in basket_items:
            # shopping_list_repo returns ShoppingListItem dataclass (id, item_id, name, quantity, unit, etc.)
            try:
                item_id = int(getattr(it, "item_id", 0) or 0)
            except Exception:
                item_id = 0
            if item_id <= 0:
                # Can't optimize without item_id (in this milestone)
                continue

            name = str(getattr(it, "display_name", "") or "").strip() or f"Item {item_id}"
            unit = str(getattr(it, "unit", "") or "").strip().lower() or "each"
            raw_qty = getattr(it, "quantity", None)
            try:
                qty = float(raw_qty) if raw_qty is not None else 1.0
            except (TypeError, ValueError):
                qty = 1.0
            if qty <= 0:
                qty = 1.0

            plan = BasketItemPlan(item_id=item_id, name=name, quantity=qty, unit=unit)

            # preference annotations (optional)
            if eff is not None:
                self._apply_preference_annotations(plan, eff, safe_phrases)

            # A household HARD exclude / allergy match is kept OUT of the buy
            # plan entirely (not priced, not summed) — see M2 / CLAUDE.md
            # "allergies are ALWAYS hard exclusions, household-wide".
            if plan.hard_excluded:
                excluded.append(plan)
                continue

            normalized.append(plan)

        result.excluded_items = excluded

        if not normalized:
            if excluded:
                result.warnings.append(
                    f"{len(excluded)} basket item(s) match a household allergy/hard-exclude "
                    f"and were EXCLUDED from the buy plan: "
                    f"{', '.join(p.name for p in excluded)}. "
                    f"Remove them from your list or review the household allergy settings."
                )
            else:
                result.warnings.append("No optimizable items found (missing item_id on shopping list entries).")
            return result

        all_item_ids = [p.item_id for p in normalized]

        # Batch-load stats for savings lines (avg + min over 180 days) — 1 query
        stats_map = get_price_stats_batch(all_item_ids, since_days=180)
        for plan in normalized:
            s = stats_map.get(plan.item_id)
            if s and s.count > 0:
                plan.usual_avg_unit_price_180d = s.avg_price
                plan.lowest_unit_price_180d = s.min_price

        # Estimate per-store unit prices for each item
        store_map: Dict[int, str] = {s.id: s.name for s in stores}
        all_store_ids = list(store_map.keys())

        # 1 query: active flyer prices
        flyer_map = self._load_active_flyer_unit_prices(
            item_ids=all_item_ids,
            store_ids=all_store_ids,
        )

        # 2 queries (batch): most-recent per (item, store) + global any-store fallback
        store_history = get_most_recent_prices_by_store_batch(all_item_ids, all_store_ids)
        global_history = get_most_recent_prices_global_batch(all_item_ids)

        # Build price matrix from in-memory dicts — 0 extra DB calls
        price_matrix: Dict[Tuple[int, int], PricePick] = {}
        for store_id, store_name in store_map.items():
            for p in normalized:
                price_matrix[(store_id, p.item_id)] = self._pick_price_from_maps(
                    item_id=p.item_id,
                    store_id=store_id,
                    store_name=store_name,
                    flyer_map=flyer_map,
                    store_history=store_history,
                    global_history=global_history,
                )

        # Choose best store(s)
        if mode == "one_store":
            chosen_store_ids = [self._choose_best_single_store(normalized, stores, price_matrix)]
        else:
            chosen_store_ids = self._choose_best_two_stores(normalized, stores, price_matrix)

        # Build store plans and assign items to stores
        store_plans: Dict[int, StorePlan] = {sid: StorePlan(store_id=sid, store_name=store_map[sid]) for sid in chosen_store_ids}

        # Assign each item to the store where it's cheapest (or known)
        for item in normalized:
            best_sid = self._best_store_for_item(item.item_id, chosen_store_ids, price_matrix)
            chosen = price_matrix.get((best_sid, item.item_id))
            item.chosen = chosen
            store_plans[best_sid].items.append(item)

        # Totals + unknown counts
        basket_total = 0.0
        unknown_total = 0
        for sp in store_plans.values():
            total = 0.0
            unknown = 0
            for item in sp.items:
                unit_price = item.chosen.unit_price if item.chosen else None
                if unit_price is None:
                    unknown += 1
                    continue
                total += unit_price * item.quantity
            sp.total_estimated = float(total)
            sp.unknown_count = int(unknown)
            basket_total += sp.total_estimated
            unknown_total += sp.unknown_count

        result.stores = list(store_plans.values())
        result.basket_total_estimated = float(basket_total)

        # Savings lines
        avg_total = 0.0
        lowest_total = 0.0
        avg_unknown = 0
        low_unknown = 0
        for item in normalized:
            if item.usual_avg_unit_price_180d is None:
                avg_unknown += 1
            else:
                avg_total += float(item.usual_avg_unit_price_180d) * item.quantity

            if item.lowest_unit_price_180d is None:
                low_unknown += 1
            else:
                lowest_total += float(item.lowest_unit_price_180d) * item.quantity

        result.basket_usual_avg_estimated = None if avg_unknown == len(normalized) else float(avg_total)
        result.basket_lowest_estimated = None if low_unknown == len(normalized) else float(lowest_total)

        if result.basket_usual_avg_estimated is not None:
            result.save_vs_usual_avg = float(result.basket_usual_avg_estimated - result.basket_total_estimated)
        if result.basket_lowest_estimated is not None:
            result.save_vs_lowest = float(result.basket_lowest_estimated - result.basket_total_estimated)

        # Warnings
        if unknown_total > 0:
            result.warnings.append(
                f"{unknown_total} basket item(s) have unknown prices in the DB. Totals are partial estimates."
            )
        if mode == "two_store" and len(chosen_store_ids) == 2:
            store_objs = {int(s.id): s for s in stores}
            s_a = store_objs.get(chosen_store_ids[0])
            s_b = store_objs.get(chosen_store_ids[1])
            d_a = getattr(s_a, "distance_km", None)
            d_b = getattr(s_b, "distance_km", None)
            distances = [d for d in (d_a, d_b) if d is not None and d > 0]
            if distances:
                try:
                    gas_rate = get_gas_cost_per_km()
                except Exception:
                    gas_rate = 0.18
                extra_km = 2.0 * max(distances)
                extra_cost = extra_km * gas_rate
                result.warnings.append(
                    f"Two-store mode: extra ~{extra_km:.0f} km driving "
                    f"(≈ ${extra_cost:.2f} gas at ${gas_rate:.2f}/km)."
                )
            else:
                result.warnings.append(
                    "Two-store mode may save more, but requires an extra trip (time + gas). "
                    "Set store distance in Store Settings for a precise cost estimate."
                )

        # Preference warnings: hard-excluded / allergen items were pulled OUT of
        # the optimized plan above; tell the user which and why.
        if excluded:
            result.warnings.append(
                f"{len(excluded)} basket item(s) match a household allergy/hard-exclude "
                f"and were EXCLUDED from the buy plan: "
                f"{', '.join(p.name for p in excluded)}. "
                f"Remove them from your list or review the household allergy settings."
            )

        # sort store plans by total
        result.stores.sort(key=lambda x: x.total_estimated)

        return result

    # ---------------------------------------------------------------------
    # Preferences helpers
    # ---------------------------------------------------------------------

    def _apply_preference_annotations(self, item: BasketItemPlan, eff: Any, safe_phrases: List[str]) -> None:
        name = _norm(item.name)

        # Hard excludes: household-level (allergies + master hard excludes)
        hard_terms = list(getattr(eff, "hard_excludes", set()) or [])
        for term in hard_terms:
            if phrase_safe_hit(name, term, safe_phrases=safe_phrases):
                item.hard_excluded = True
                break

        # Soft excludes: map term -> members
        soft_map = getattr(eff, "soft_excludes", {}) or {}
        hits: List[Tuple[str, List[str]]] = []
        starred = False

        # Resolve household membership ONCE (invariant for this call) rather than
        # per matched term. Compare by id (not name) — two members can share a name.
        resolved = False
        name_to_id: Dict[str, int] = {}
        master_id = 0
        if preferences_service is not None and config_store is not None:
            try:
                name_to_id = {
                    getattr(mm, "name", ""): int(getattr(mm, "id", 0) or 0)
                    for mm in config_store.list_members()  # type: ignore[attr-defined]
                }
                master_id = int(getattr(config_store.get_master_member(), "id", 0) or 0)  # type: ignore[attr-defined]
                resolved = True
            except Exception:
                resolved = False

        for term, members in soft_map.items():
            if not term:
                continue
            if phrase_safe_hit(name, term, safe_phrases=safe_phrases):
                mems = list(members or [])
                hits.append((str(term), mems))
                # star only if any SECONDARY member is involved
                if resolved:
                    mem_ids = {name_to_id.get(m, -1) for m in mems}
                    if any(mid != master_id for mid in mem_ids if mid >= 0):
                        starred = True
                else:
                    starred = True

        item.soft_hits = hits
        item.starred = starred

    # ---------------------------------------------------------------------
    # Price selection helpers
    # ---------------------------------------------------------------------

    def _pick_price_from_maps(
        self,
        *,
        item_id: int,
        store_id: int,
        store_name: str,
        flyer_map: Dict[Tuple[int, int], Tuple[float, str]],
        store_history: Dict[Tuple[int, int], Any],
        global_history: Dict[int, Any],
    ) -> PricePick:
        """In-memory version of _pick_price_for_item_store — no DB calls."""
        # 1) Active flyer price
        flyer = flyer_map.get((store_id, item_id))
        if flyer:
            unit_price, unit = flyer
            if _positive(unit_price):
                return PricePick(store_id=store_id, store_name=store_name,
                                 unit_price=unit_price, unit=unit, source="flyer")

        # 2) Most recent store-specific history
        pr = store_history.get((item_id, store_id))
        if pr and _positive(getattr(pr, "unit_price", None)):
            up = getattr(pr, "norm_unit_price", None)
            if up is None:
                up = pr.unit_price
            unit = str(getattr(pr, "norm_unit", None) or getattr(pr, "unit", None) or "each").strip().lower()
            return PricePick(
                store_id=store_id, store_name=store_name,
                unit_price=float(up),
                unit=unit,
                source="history_store",
            )

        # 3) Global any-store fallback
        pr2 = global_history.get(item_id)
        if pr2 and _positive(getattr(pr2, "unit_price", None)):
            up2 = getattr(pr2, "norm_unit_price", None)
            if up2 is None:
                up2 = pr2.unit_price
            unit2 = str(getattr(pr2, "norm_unit", None) or getattr(pr2, "unit", None) or "each").strip().lower()
            return PricePick(
                store_id=store_id, store_name=store_name,
                unit_price=float(up2),
                unit=unit2,
                source="history_any",
            )

        return PricePick(store_id=store_id, store_name=store_name,
                         unit_price=None, unit="each", source="unknown")

    def _pick_price_for_item_store(
        self,
        *,
        item_id: int,
        store_id: int,
        store_name: str,
        flyer_map: Dict[Tuple[int, int], Tuple[float, str]],
    ) -> PricePick:
        """
        Priority:
          1) Active flyer unit_price (if exists)
          2) Most recent historical price for that store
          3) Most recent historical price any store
          4) Unknown
        """
        # 1) Flyer
        flyer = flyer_map.get((store_id, item_id))
        if flyer:
            unit_price, unit = flyer
            if _positive(unit_price):
                return PricePick(store_id=store_id, store_name=store_name, unit_price=unit_price, unit=unit, source="flyer")

        # 2) Most recent store-specific history
        pr = prices_repo.get_most_recent_price(item_id=item_id, store_id=store_id)
        if pr and _positive(getattr(pr, "unit_price", None)):
            up = getattr(pr, "norm_unit_price", None)
            if up is None:
                up = pr.unit_price
            unit = str(getattr(pr, "norm_unit", None) or getattr(pr, "unit", None) or "each").strip().lower()
            return PricePick(
                store_id=store_id,
                store_name=store_name,
                unit_price=float(up),
                unit=unit,
                source="history_store",
            )

        # 3) Most recent any-store history (global estimate fallback)
        pr2 = prices_repo.get_most_recent_price(item_id=item_id, store_id=None)
        if pr2 and _positive(getattr(pr2, "unit_price", None)):
            up2 = getattr(pr2, "norm_unit_price", None)
            if up2 is None:
                up2 = pr2.unit_price
            unit2 = str(getattr(pr2, "norm_unit", None) or getattr(pr2, "unit", None) or "each").strip().lower()
            return PricePick(
                store_id=store_id,
                store_name=store_name,
                unit_price=float(up2),
                unit=unit2,
                source="history_any",
            )

        # 4) Unknown
        return PricePick(
            store_id=store_id,
            store_name=store_name,
            unit_price=None,
            unit="each",
            source="unknown",
        )

    def _best_store_for_item(
        self,
        item_id: int,
        store_ids: List[int],
        price_matrix: Dict[Tuple[int, int], PricePick],
    ) -> int:
        """
        Choose the store with the lowest known unit_price. If only one store has known price, pick it.
        """
        best_sid = store_ids[0]
        best_price: Optional[float] = None
        for sid in store_ids:
            pick = price_matrix.get((sid, item_id))
            p = pick.unit_price if pick else None
            if p is None:
                continue
            if best_price is None or p < best_price:
                best_price = p
                best_sid = sid
        return best_sid

    # ---------------------------------------------------------------------
    # Store selection
    # ---------------------------------------------------------------------

    def _score_store(self, s: Any, total: float, unknown: int) -> float:
        """
        Base store score: estimated cost + unknown-item penalty + favourite/priority bonus.
        Lower is better.
        """
        score = total + (unknown * 5.0)
        try:
            if bool(getattr(s, "is_favorite", False)):
                score *= 0.985
            pr = int(getattr(s, "priority", 0) or 0)
            if pr > 0:
                score *= max(0.97, 1.0 - (min(pr, 10) * 0.002))
        except Exception:
            pass
        return score

    def _choose_best_single_store(
        self,
        items: List[BasketItemPlan],
        stores: List[Any],
        price_matrix: Dict[Tuple[int, int], PricePick],
    ) -> int:
        """
        Best single store by estimated total cost, with a small bonus for favorites/priority.
        """
        best_id = int(stores[0].id)
        best_score: Optional[float] = None

        for s in stores:
            sid = int(s.id)
            total = 0.0
            unknown = 0
            for it in items:
                pick = price_matrix.get((sid, it.item_id))
                if not pick or pick.unit_price is None:
                    unknown += 1
                    continue
                total += pick.unit_price * it.quantity

            score = self._score_store(s, total, unknown)
            if best_score is None or score < best_score:
                best_score = score
                best_id = sid

        return best_id

    def _choose_best_two_stores(
        self,
        items: List[BasketItemPlan],
        stores: List[Any],
        price_matrix: Dict[Tuple[int, int], PricePick],
    ) -> List[int]:
        """
        Choose up to two stores. We:
          1) rank stores by single-store score
          2) evaluate store pairs among top K candidates
          3) pick pair with lowest basket assignment total + small travel penalty
        """
        if len(stores) == 1:
            return [int(stores[0].id)]

        # Step 1: rank by single-store score
        ranked = []
        for s in stores:
            sid = int(s.id)
            total = 0.0
            unknown = 0
            for it in items:
                pick = price_matrix.get((sid, it.item_id))
                if not pick or pick.unit_price is None:
                    unknown += 1
                    continue
                total += pick.unit_price * it.quantity
            ranked.append((self._score_store(s, total, unknown), sid))
        ranked.sort(key=lambda x: x[0])

        # Evaluate pairs among top K
        K = min(8, len(ranked))
        candidates = [sid for _score, sid in ranked[:K]]

        best_pair: List[int] = [candidates[0], candidates[1]]
        best_score: Optional[float] = None

        # Build once; used inside the pair loop for the favourite tie-breaker
        store_by_id = {int(s.id): s for s in stores}

        for i in range(len(candidates)):
            for j in range(i + 1, len(candidates)):
                a = candidates[i]
                b = candidates[j]
                total = 0.0
                unknown = 0
                items_at_a = 0
                items_at_b = 0

                for it in items:
                    pa = price_matrix.get((a, it.item_id))
                    pb = price_matrix.get((b, it.item_id))
                    ua = pa.unit_price if pa else None
                    ub = pb.unit_price if pb else None

                    if ua is None and ub is None:
                        unknown += 1
                        continue
                    if ua is None:
                        total += ub * it.quantity  # type: ignore
                        items_at_b += 1
                    elif ub is None:
                        total += ua * it.quantity
                        items_at_a += 1
                    else:
                        total += min(ua, ub) * it.quantity
                        if ua <= ub:
                            items_at_a += 1
                        else:
                            items_at_b += 1

                # Reject degenerate pairs where one stop covers zero items
                # (otherwise the optimizer recommends an empty second trip).
                if items_at_a == 0 or items_at_b == 0:
                    continue

                # Two-store travel penalty: 2× round-trips to the extra store.
                # Uses distance_km if set; falls back to flat $6 and discloses it.
                trip_penalty = _compute_trip_penalty(store_by_id.get(a), store_by_id.get(b))
                score = total + (unknown * 5.0) + trip_penalty
                try:
                    if (
                        bool(getattr(store_by_id.get(a), "is_favorite", False))
                        or bool(getattr(store_by_id.get(b), "is_favorite", False))
                    ):
                        score *= 0.99
                except Exception:
                    pass

                if best_score is None or score < best_score:
                    best_score = score
                    best_pair = [a, b]

        return best_pair

    # ---------------------------------------------------------------------
    # Flyer loading (active by valid_from/valid_to)
    # ---------------------------------------------------------------------

    def _load_active_flyer_unit_prices(
        self,
        *,
        item_ids: List[int],
        store_ids: List[int],
    ) -> Dict[Tuple[int, int], Tuple[float, str]]:
        """
        Returns {(store_id, item_id): (unit_price, unit)} for ACTIVE flyer sources.

        Uses:
          prices.source='flyer'
          prices.flyer_source_id -> flyer_sources(id)
          flyer_sources.valid_from/valid_to must include today
        """
        try:
            from Grocery_Sense.data.connection import connection_scope
        except Exception:
            return {}

        if not item_ids or not store_ids:
            return {}

        items = sorted({int(x) for x in item_ids if int(x) > 0})
        stores = sorted({int(x) for x in store_ids if int(x) > 0})
        if not items or not stores:
            return {}

        today = _today_date().isoformat()
        store_ph = ",".join("?" * len(stores))

        out: Dict[Tuple[int, int], Tuple[float, str]] = {}
        # Chunk the item IN list so a large basket never blows past SQLite's
        # variable limit (matches prices_repo._SQL_PARAM_CHUNK = 900). The old
        # code inlined every item id in one query and swallowed the resulting
        # "too many SQL variables" error, silently dropping all flyer prices.
        CHUNK = 900
        try:
            with connection_scope() as conn:
                for i in range(0, len(items), CHUNK):
                    chunk = items[i:i + CHUNK]
                    item_ph = ",".join("?" * len(chunk))
                    sql = (
                        "SELECT p.store_id, p.item_id, p.unit_price, COALESCE(p.unit, 'each') AS unit "
                        "FROM prices p "
                        "JOIN flyer_sources fs ON fs.id = p.flyer_source_id "
                        "WHERE p.source = 'flyer' "
                        f"  AND p.store_id IN ({store_ph}) "
                        f"  AND p.item_id  IN ({item_ph}) "
                        "  AND p.unit_price IS NOT NULL "
                        "  AND date(fs.valid_from) <= date(?) "
                        "  AND date(fs.valid_to)   >= date(?)"
                    )
                    for r in conn.execute(sql, (*stores, *chunk, today, today)).fetchall():
                        try:
                            sid = int(r["store_id"])
                            iid = int(r["item_id"])
                            up = float(r["unit_price"])
                            unit = str(r["unit"] or "each").strip().lower()
                        except Exception:
                            continue
                        key = (sid, iid)
                        if key not in out or up < out[key][0]:
                            out[key] = (up, unit)
        except sqlite3.OperationalError as e:
            # Early-prototype DBs may not have a flyer_sources table yet; treat
            # that as "no active flyer prices". Any OTHER operational error is a
            # real fault and must surface (fail loud), not silently degrade.
            if "flyer_sources" not in str(e).lower():
                raise

        return out
