"""
Grocery_Sense.services.list_audit_service

Pre-shop audit of the active shopping list: for every list item with price
history, find its best current price across stores (flyer + latest), compare to
the household's usual price, and flag items you'd be OVERPAYING on right now.

The savings angle is "what NOT to buy today": a staple priced above its usual is
better left for a future trip. Complements basket_optimizer (which splits a trip
across stores) by judging each line on price, not geography.

Reuses the same batch price queries the price-drop alert engine uses, so an
audit of a full list is a handful of queries, not N per item.
"""

from __future__ import annotations

from typing import Any, Dict, List, Optional, Tuple

from Grocery_Sense.data.repositories import stores_repo
from Grocery_Sense.data.repositories.prices_repo import (
    get_active_flyer_prices_batch,
    get_most_recent_prices_by_store_batch,
    get_most_recent_prices_global_batch,
    get_usual_unit_price_batch,
)
from Grocery_Sense.services.shopping_list_service import get_active_items


# Classification thresholds mirror PriceHistoryService.classify_deal so the two
# surfaces tell the same story. percent is (usual - current) / usual * 100;
# positive = cheaper than usual.
_GREAT_PCT = 15.0
_GOOD_PCT = 5.0
_TYPICAL_FLOOR_PCT = -5.0  # below this => "expensive" (overpay)

USUAL_LOOKBACK_DAYS = 180
MIN_USUAL_SAMPLES = 3  # softer than the alert engine's 4 — audit is advisory


def _classify(best: float, usual: Optional[float]) -> Tuple[str, Optional[float]]:
    if usual is None or usual <= 0:
        return "unknown", None
    pct = (usual - best) / usual * 100.0
    if pct >= _GREAT_PCT:
        return "great", pct
    if pct >= _GOOD_PCT:
        return "good", pct
    if pct > _TYPICAL_FLOOR_PCT:
        return "typical", pct
    return "expensive", pct


def audit_active_list(*, window_days: int = USUAL_LOOKBACK_DAYS) -> Dict[str, Any]:
    """
    Audit the active (un-checked-off) shopping list.

    Returns a dict:
        {
          "line_items": [ {row_id, item_id, name, qty, unit, best_unit,
                           best_store, best_source, usual_unit, classification,
                           pct_vs_usual, est_line_cost} ],
          "unmatched": [display_name, ...],   # list rows with no canonical item_id
          "priced_count": int,
          "unknown_price_count": int,         # mapped items with no price history
          "estimated_total": float,           # sum(best_unit * qty) over priced lines
          "savings_vs_usual": float,          # net $ vs usual (positive = saving)
          "overpay_items": [line_item, ...],  # classification == "expensive"
          "overpay_excess": float,            # sum((best-usual)*qty) over overpays
          "estimate_caveat": bool,            # est totals assume qty unit == price unit
        }

    Note on totals: list quantities are loose (often "1 each") and may not match
    the unit a price is quoted in (e.g. $/kg). est_line_cost is best_unit * qty —
    a rough figure flagged by estimate_caveat. The reliable signal is per-line
    classification (the overpay flag), not the dollar total.
    """
    rows = list(get_active_items(include_checked_off=False) or [])

    line_items: List[Dict[str, Any]] = []
    unmatched: List[str] = []

    mapped = [r for r in rows if getattr(r, "item_id", None) is not None]
    for r in rows:
        if getattr(r, "item_id", None) is None:
            unmatched.append(getattr(r, "display_name", "") or "")

    if not mapped:
        return {
            "line_items": [],
            "unmatched": unmatched,
            "priced_count": 0,
            "unknown_price_count": 0,
            "estimated_total": 0.0,
            "savings_vs_usual": 0.0,
            "overpay_items": [],
            "overpay_excess": 0.0,
            "estimate_caveat": False,
        }

    item_ids = list({int(r.item_id) for r in mapped})
    stores = stores_repo.list_stores()
    store_ids = [s.id for s in stores]
    store_name_map = {s.id: s.name for s in stores}

    # Batch-load all price signals upfront (mirror the alert engine).
    flyer_quotes = get_active_flyer_prices_batch(item_ids, store_ids) if store_ids else {}
    store_quotes = get_most_recent_prices_by_store_batch(item_ids, store_ids) if store_ids else {}
    global_quotes = get_most_recent_prices_global_batch(item_ids)
    usual_map = get_usual_unit_price_batch(
        item_ids,
        receipt_only=False,
        min_samples=MIN_USUAL_SAMPLES,
        since_days=window_days,
    )

    # Resolve the single best (lowest) current price per item.
    best_by_item: Dict[int, Tuple[float, str, str, str]] = {}  # id -> (unit, store, source, unit_label)
    for item_id in item_ids:
        best_unit: Optional[float] = None
        best_store = ""
        best_source = "unknown"
        best_unit_label = ""
        for s in stores:
            q = flyer_quotes.get((item_id, s.id))
            unit_label = ""
            if q is None:
                pr = store_quotes.get((item_id, s.id))
                if pr and pr.unit_price is not None and pr.unit_price > 0:
                    q = {"unit_price": float(pr.unit_price), "source": pr.source or "latest"}
                    unit_label = (pr.unit or "").strip()
            if not q:
                continue
            unit = q.get("unit_price")
            if unit is None or float(unit) <= 0:
                continue
            if best_unit is None or float(unit) < best_unit:
                best_unit = float(unit)
                best_store = str(s.name)
                best_source = str(q.get("source") or "latest")
                best_unit_label = unit_label
        if best_unit is None:
            g = global_quotes.get(item_id)
            if g and g.unit_price is not None and float(g.unit_price) > 0:
                best_unit = float(g.unit_price)
                best_store = store_name_map.get(int(g.store_id or 0), "Unknown")
                best_source = str(g.source or "global_latest")
                best_unit_label = (g.unit or "").strip()
        if best_unit is not None and best_unit > 0:
            best_by_item[item_id] = (best_unit, best_store, best_source, best_unit_label)

    priced_count = 0
    unknown_price_count = 0
    estimated_total = 0.0
    savings_vs_usual = 0.0
    overpay_excess = 0.0
    overpay_items: List[Dict[str, Any]] = []
    estimate_caveat = False

    for r in mapped:
        item_id = int(r.item_id)
        qty = float(getattr(r, "quantity", 1.0) or 1.0)
        best = best_by_item.get(item_id)
        if best is None:
            unknown_price_count += 1
            line_items.append({
                "row_id": getattr(r, "id", None),
                "item_id": item_id,
                "name": getattr(r, "display_name", "") or "",
                "qty": qty,
                "unit": getattr(r, "unit", "") or "",
                "best_unit": None,
                "best_store": "",
                "best_source": "",
                "usual_unit": None,
                "classification": "no_data",
                "pct_vs_usual": None,
                "est_line_cost": None,
            })
            continue

        best_unit, best_store, best_source, best_unit_label = best
        usual, _samples, _basis = usual_map.get(item_id, (None, 0, "unknown"))
        usual_f = float(usual) if usual is not None else None

        classification, pct = _classify(best_unit, usual_f)
        est_line_cost = best_unit * qty
        priced_count += 1
        estimated_total += est_line_cost

        # Flag when the list unit and the price unit disagree (e.g. "each" vs "kg").
        list_unit = (getattr(r, "unit", "") or "").strip().lower()
        if best_unit_label and list_unit and best_unit_label.lower() != list_unit:
            estimate_caveat = True

        if usual_f is not None and usual_f > 0:
            savings_vs_usual += (usual_f - best_unit) * qty
            if classification == "expensive":
                overpay_excess += (best_unit - usual_f) * qty

        line = {
            "row_id": getattr(r, "id", None),
            "item_id": item_id,
            "name": getattr(r, "display_name", "") or "",
            "qty": qty,
            "unit": getattr(r, "unit", "") or "",
            "best_unit": best_unit,
            "best_store": best_store,
            "best_source": best_source,
            "usual_unit": usual_f,
            "classification": classification,
            "pct_vs_usual": pct,
            "est_line_cost": est_line_cost,
        }
        line_items.append(line)
        if classification == "expensive":
            overpay_items.append(line)

    return {
        "line_items": line_items,
        "unmatched": unmatched,
        "priced_count": priced_count,
        "unknown_price_count": unknown_price_count,
        "estimated_total": estimated_total,
        "savings_vs_usual": savings_vs_usual,
        "overpay_items": overpay_items,
        "overpay_excess": overpay_excess,
        "estimate_caveat": estimate_caveat,
    }
