"""
Grocery_Sense.services.demo_seed_service

Demo seeder for the prototype.

Creates:
- 3 stores
- 30 items
- 200 price points across ~90 days

Designed to be deterministic (same seed = same dataset) so demos are repeatable.

Usage:
    from Grocery_Sense.services.demo_seed_service import seed_demo_data
    seed_demo_data(reset_first=True)
"""

from __future__ import annotations

import random
import sqlite3
from dataclasses import dataclass
from datetime import date, timedelta
from typing import Dict, List, Optional, Tuple

from Grocery_Sense.data.connection import connection_scope, get_connection
from Grocery_Sense.data.schema import initialize_database
from Grocery_Sense.data.repositories.stores_repo import create_store, list_stores
from Grocery_Sense.data.repositories import items_repo
from Grocery_Sense.data.repositories.prices_repo import add_price_points


# -----------------------------
# Demo catalog
# -----------------------------

@dataclass(frozen=True)
class DemoItemSpec:
    canonical_name: str
    category: str
    unit: str               # 'each' | 'kg'
    base_price: float       # base unit price (CAD), per unit
    price_volatility: float # max +/- % randomization around base


def _demo_stores() -> List[dict]:
    # tweak these to match your real environment later
    return [
        {
            "name": "Walmart",
            "address": "Demo Address 1",
            "city": "Surrey",
            "postal_code": "V3T 0A1",
            "flipp_store_id": None,
            "is_favorite": True,
            "priority": 3,
            "notes": "Demo seed store",
        },
        {
            "name": "Save-On-Foods",
            "address": "Demo Address 2",
            "city": "Surrey",
            "postal_code": "V3T 0A1",
            "flipp_store_id": None,
            "is_favorite": False,
            "priority": 2,
            "notes": "Demo seed store",
        },
        {
            "name": "Real Canadian Superstore",
            "address": "Demo Address 3",
            "city": "Surrey",
            "postal_code": "V3T 0A1",
            "flipp_store_id": None,
            "is_favorite": False,
            "priority": 1,
            "notes": "Demo seed store",
        },
    ]


def _demo_items() -> List[DemoItemSpec]:
    # 30 common items, mix of each + kg
    return [
        DemoItemSpec("milk 2l", "dairy", "each", 4.79, 0.12),
        DemoItemSpec("eggs dozen", "dairy", "each", 4.49, 0.15),
        DemoItemSpec("butter", "dairy", "each", 6.49, 0.18),
        DemoItemSpec("cheddar cheese", "dairy", "each", 6.99, 0.20),
        DemoItemSpec("greek yogurt", "dairy", "each", 5.99, 0.18),

        DemoItemSpec("chicken breast", "meat", "kg", 15.99, 0.20),
        DemoItemSpec("ground beef", "meat", "kg", 17.99, 0.18),
        DemoItemSpec("pork chops", "meat", "kg", 12.99, 0.22),
        DemoItemSpec("salmon fillet", "meat", "kg", 28.99, 0.20),
        DemoItemSpec("bacon", "meat", "each", 6.99, 0.22),

        DemoItemSpec("rice 2kg", "pantry", "each", 6.49, 0.18),
        DemoItemSpec("pasta", "pantry", "each", 2.49, 0.25),
        DemoItemSpec("pasta sauce", "pantry", "each", 3.49, 0.22),
        DemoItemSpec("black beans", "pantry", "each", 1.49, 0.30),
        DemoItemSpec("canned tomatoes", "pantry", "each", 1.79, 0.28),

        DemoItemSpec("bread loaf", "bakery", "each", 3.29, 0.20),
        DemoItemSpec("tortillas", "bakery", "each", 3.99, 0.18),
        DemoItemSpec("bagels", "bakery", "each", 3.79, 0.18),

        DemoItemSpec("apples", "produce", "kg", 4.49, 0.22),
        DemoItemSpec("bananas", "produce", "kg", 1.79, 0.25),
        DemoItemSpec("oranges", "produce", "kg", 3.99, 0.20),
        DemoItemSpec("tomatoes", "produce", "kg", 4.99, 0.25),
        DemoItemSpec("onions", "produce", "kg", 2.29, 0.22),
        DemoItemSpec("potatoes", "produce", "kg", 2.49, 0.25),
        DemoItemSpec("carrots", "produce", "kg", 2.19, 0.22),
        DemoItemSpec("broccoli", "produce", "kg", 4.99, 0.22),
        DemoItemSpec("lettuce", "produce", "each", 2.99, 0.25),
        DemoItemSpec("garlic", "produce", "each", 1.49, 0.30),

        DemoItemSpec("olive oil", "pantry", "each", 9.99, 0.18),
        DemoItemSpec("coffee", "pantry", "each", 12.99, 0.20),
    ]


# -----------------------------
# Reset / cleanup
# -----------------------------

def reset_all_demo_data() -> None:
    """
    Clears tables (demo-friendly reset) so seeding is repeatable.
    """
    initialize_database()

    with connection_scope() as conn:
        cur = conn.cursor()

        # child → parent order. Tables added since v0 may not exist on an
        # ancient DB; guard each with a try/except so the reset stays robust.
        _optional_tables = (
            "prices",
            "receipt_line_items",
            "receipt_raw_json",
            "receipt_file_hashes",
            "receipt_signatures",
            "deleted_receipt_backups",
            "receipts",
            "flyer_deals",
            "flyer_assets",
            "flyer_raw_json",
            "flyer_batches",
            "price_drop_alerts",
            "flyer_sources",
            "shopping_list",
            "item_aliases",
            "items",
            "stores",
        )
        for tbl in _optional_tables:
            try:
                cur.execute(f"DELETE FROM {tbl};")
            except sqlite3.OperationalError:
                pass

        # Reset autoincrement counters so reseeded ids start from 1
        try:
            cur.execute("DELETE FROM sqlite_sequence;")
        except sqlite3.OperationalError:
            pass

        conn.commit()


# -----------------------------
# Seeding
# -----------------------------

def seed_demo_data(
    reset_first: bool = True,
    n_price_points: int = 200,
    days_back: int = 90,
    seed: int = 42,
) -> Dict[str, int]:
    """
    Seed the database with a small, believable dataset.

    Returns counts:
        {"stores": 3, "items": 30, "price_points": 200}
    """
    initialize_database()

    if reset_first:
        reset_all_demo_data()

    rng = random.Random(seed)

    # 1) Stores
    created_store_ids: List[int] = []
    for s in _demo_stores():
        st = create_store(**s)
        created_store_ids.append(st.id)

    # 2) Items
    item_specs = _demo_items()
    created_items: List[Tuple[int, DemoItemSpec]] = []

    for spec in item_specs:
        it = items_repo.create_item(
            canonical_name=spec.canonical_name,
            category=spec.category,
            default_unit=spec.unit,
            typical_package_size=None,
            typical_package_unit=None,
            is_tracked=True,
            notes="Demo seed item",
        )
        created_items.append((it.id, spec))

    # 3) Price points (spread across dates + stores)
    start = date.today() - timedelta(days=days_back)
    price_points_created = 0

    # Store multipliers so stores feel consistently different
    # (e.g., Walmart tends to be slightly cheaper on avg)
    store_bias: Dict[int, float] = {}
    for idx, sid in enumerate(created_store_ids):
        if idx == 0:
            store_bias[sid] = 0.94  # Walmart cheaper
        elif idx == 1:
            store_bias[sid] = 1.03  # Save-On a bit higher
        else:
            store_bias[sid] = 0.99  # Superstore near baseline

    # Create prices (accumulate, then one bulk insert instead of N commits)
    price_rows: List[Tuple] = []
    while price_points_created < n_price_points:
        item_id, spec = rng.choice(created_items)
        store_id = rng.choice(created_store_ids)

        # pick a day in range
        d = start + timedelta(days=rng.randint(0, max(1, days_back)))
        d_str = d.isoformat()

        # base price ± volatility
        # and add store bias + small random noise
        vol = spec.price_volatility
        drift = 1.0 + rng.uniform(-vol, vol)
        noise = 1.0 + rng.uniform(-0.03, 0.03)
        biased = store_bias.get(store_id, 1.0)

        unit_price = round(spec.base_price * drift * noise * biased, 2)

        # quantity + total_price
        if spec.unit == "kg":
            quantity = round(rng.uniform(0.5, 2.0), 2)
            total_price = round(unit_price * quantity, 2)
        else:
            quantity = 1.0
            total_price = round(unit_price * quantity, 2)

        # tuple order matches add_price_points: (item_id, store_id, receipt_id,
        # flyer_source_id, source, date, unit_price, unit, quantity, total_price,
        # raw_name, confidence)
        price_rows.append((
            item_id,
            store_id,
            None,
            None,
            "manual",
            d_str,
            float(unit_price),
            spec.unit,
            float(quantity),
            float(total_price),
            spec.canonical_name,
            5,
        ))

        price_points_created += 1

    add_price_points(price_rows)

    return {
        "stores": len(created_store_ids),
        "items": len(created_items),
        "price_points": price_points_created,
    }
