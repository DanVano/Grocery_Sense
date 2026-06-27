from __future__ import annotations

import re
import threading
from dataclasses import dataclass
from typing import Optional, Tuple

from Grocery_Sense.data.connection import _TEST_DB_PATH, connection_scope, get_connection, get_db_path


# Schema-ready cache keyed by resolved DB path, so swapping DBs (e.g. pytest
# fixtures) automatically invalidates without each caller having to reset.
_SCHEMA_READY: set = set()
# Serialises the check-then-ALTER so two background threads can't both run the
# column-add and raise "duplicate column name".
_SCHEMA_LOCK = threading.Lock()


def _current_db_key() -> str:
    from Grocery_Sense.data import connection as _conn
    return _conn._TEST_DB_PATH or str(get_db_path())


LB_TO_KG = 0.45359237
KG_TO_LB = 2.2046226218

# Volume conversions (all relative to 1 litre)
ML_PER_L = 1000.0
FL_OZ_PER_L = 33.8140226
CUP_PER_L = 4.22675284
TBSP_PER_L = 67.6280454
TSP_PER_L = 202.884136
GAL_PER_L = 0.264172052
PINT_PER_L = 2.11337642

# Weight oz
G_PER_OZ = 28.3495231


# Module-level conversion factor maps (hoisted out of _convert to avoid rebuilding
# the dicts on every normalize() call).
_WEIGHT_KG_PER_UNIT = {
    "kg": 1.0,
    "g": 0.001,
    "lb": LB_TO_KG,
    "oz": G_PER_OZ / 1000.0,
}

_VOLUME_L_PER_UNIT = {
    "L": 1.0,
    "ml": 1.0 / ML_PER_L,
    "fl_oz": 1.0 / FL_OZ_PER_L,
    "cup": 1.0 / CUP_PER_L,
    "tbsp": 1.0 / TBSP_PER_L,
    "tsp": 1.0 / TSP_PER_L,
    "gal": 1.0 / GAL_PER_L,
    "pint": 1.0 / PINT_PER_L,
}


@dataclass(frozen=True)
class NormalizedPrice:
    norm_unit_price: float
    norm_unit: str
    note: str


class UnitNormalizationService:
    """
    Unit normalization v1

    Stores:
      - items.default_unit (TEXT)
      - prices.norm_unit_price (REAL)
      - prices.norm_unit (TEXT)
      - prices.norm_note (TEXT)

    Rules:
      - If item has no default_unit, set it to the observed unit (if meaningful).
      - If observed unit differs from item default_unit, convert when possible:
          lb <-> kg
          g  <-> kg
      - If unit is unknown, treat as 'each' (no conversion).
    """

    # ----------------------------
    # Schema ensure
    # ----------------------------

    def ensure_schema(self) -> None:
        key = _current_db_key()
        if key in _SCHEMA_READY:
            return
        with _SCHEMA_LOCK:
            if key in _SCHEMA_READY:  # re-check inside the lock
                return
            self._ensure_items_default_unit_column()
            self._ensure_prices_norm_columns()
            _SCHEMA_READY.add(key)

    def _column_exists(self, table: str, col: str) -> bool:
        with connection_scope() as conn:
            rows = conn.execute(f"PRAGMA table_info({table});").fetchall()
        return any((r[1] == col) for r in rows)  # r[1] = column name

    def _ensure_items_default_unit_column(self) -> None:
        if self._column_exists("items", "default_unit"):
            return
        with connection_scope() as conn:
            conn.execute("ALTER TABLE items ADD COLUMN default_unit TEXT;")
            conn.commit()

    def _ensure_prices_norm_columns(self) -> None:
        # Add norm columns if missing
        with connection_scope() as conn:
            # We must check each one; SQLite doesn't support ALTER COLUMN IF NOT EXISTS
            rows = conn.execute("PRAGMA table_info(prices);").fetchall()
            existing = {r[1] for r in rows}

            if "norm_unit_price" not in existing:
                conn.execute("ALTER TABLE prices ADD COLUMN norm_unit_price REAL;")
            if "norm_unit" not in existing:
                conn.execute("ALTER TABLE prices ADD COLUMN norm_unit TEXT;")
            if "norm_note" not in existing:
                conn.execute("ALTER TABLE prices ADD COLUMN norm_note TEXT;")

            conn.commit()

    # ----------------------------
    # Default unit getters/setters
    # ----------------------------

    def get_item_default_unit(self, item_id: int) -> Optional[str]:
        self.ensure_schema()
        with connection_scope() as conn:
            row = conn.execute(
                "SELECT default_unit FROM items WHERE id = ?;",
                (int(item_id),),
            ).fetchone()
        if not row:
            return None
        v = row[0]
        if not v:
            return None
        return str(v).strip().lower() or None

    def set_item_default_unit_if_missing(self, item_id: int, observed_unit: str) -> None:
        """
        If items.default_unit is NULL/empty, set it to observed_unit.
        Only sets meaningful (recognized) units.
        """
        self.ensure_schema()
        observed_unit = self._normalize_unit(observed_unit)

        if observed_unit == "unknown":
            return

        cur = self.get_item_default_unit(item_id)
        if cur:
            return

        with connection_scope() as conn:
            conn.execute(
                "UPDATE items SET default_unit = ? WHERE id = ?;",
                (observed_unit, int(item_id)),
            )
            conn.commit()

    # ----------------------------
    # Public normalization API
    # ----------------------------

    def normalize(
        self,
        *,
        item_id: int,
        unit_price: float,
        observed_unit: str,
        description: Optional[str] = None,
    ) -> NormalizedPrice:
        """
        Returns normalized price into the item's default unit.
        If no default unit, we set it to observed unit and treat that as default.
        """
        self.ensure_schema()

        obs = self._normalize_unit(observed_unit)
        if obs == "unknown":
            # try infer from description
            guessed = self.guess_unit_from_text(description or "")
            obs = guessed

        # If still unknown -> each
        if obs == "unknown":
            obs = "each"

        # Ensure default exists
        self.set_item_default_unit_if_missing(item_id, obs)
        default_unit = self.get_item_default_unit(item_id) or obs

        # If already matches, no conversion
        if default_unit == obs:
            return NormalizedPrice(
                norm_unit_price=float(unit_price),
                norm_unit=default_unit,
                note="no_conversion",
            )

        # Convert between lb and kg (and g<->kg)
        converted = self._convert(unit_price=float(unit_price), from_unit=obs, to_unit=default_unit)
        if converted is None:
            # Can't convert -> keep observed
            return NormalizedPrice(
                norm_unit_price=float(unit_price),
                norm_unit=obs,
                note=f"no_conversion_possible({obs}->{default_unit})",
            )

        return NormalizedPrice(
            norm_unit_price=float(converted),
            norm_unit=default_unit,
            note=f"converted({obs}->{default_unit})",
        )

    # ----------------------------
    # Unit inference
    # ----------------------------

    def guess_unit_from_text(self, text: str) -> str:
        """
        Infer unit from receipt/flyer text.

        Weight:  kg, g, lb, oz
        Volume:  L, ml, fl oz, cup, tbsp, tsp, gal, pint
        Count:   each, pack, case, bunch, dozen
        """
        t = (text or "").lower()

        # --- weight ---
        if re.search(r"\bkg\b", t) or re.search(r"\bkilogram(s)?\b", t):
            return "kg"
        if re.search(r"(\d+(\.\d+)?)\s*g\b", t) or re.search(r"\bgrams?\b", t):
            return "g"
        if re.search(r"\blb(s)?\b", t) or re.search(r"\bpound(s)?\b", t) or re.search(r"\b#\b", t):
            return "lb"
        # fluid oz before plain oz so "fl oz" is caught first
        if re.search(r"\bfl\.?\s*oz\b", t) or re.search(r"\bfluid\s+ounce(s)?\b", t):
            return "fl_oz"
        if re.search(r"\boz\b", t) or re.search(r"\bounce(s)?\b", t):
            return "oz"

        # --- volume ---
        if re.search(r"\blitres?\b", t) or re.search(r"\bliters?\b", t) or re.search(r"\b(\d+(\.\d+)?)\s*l\b", t):
            return "L"
        if re.search(r"\bml\b", t) or re.search(r"\bmillilitres?\b", t) or re.search(r"\bmilliliters?\b", t):
            return "ml"
        if re.search(r"\bcups?\b", t):
            return "cup"
        if re.search(r"\btbsp\b", t) or re.search(r"\btablespoons?\b", t):
            return "tbsp"
        if re.search(r"\btsp\b", t) or re.search(r"\bteaspoons?\b", t):
            return "tsp"
        if re.search(r"\bgallons?\b", t) or re.search(r"\bgal\b", t):
            return "gal"
        if re.search(r"\bpints?\b", t):
            return "pint"

        # --- count / pack ---
        if re.search(r"\bdozen\b", t):
            return "dozen"
        if re.search(r"\bbunch(es)?\b", t):
            return "bunch"
        if re.search(r"\bcase(s)?\b", t):
            return "case"
        if re.search(r"\bpack(s|age|ages)?\b", t):
            return "pack"
        if re.search(r"\b(ea|each|unit(s)?|ct|count)\b", t):
            return "each"

        return "unknown"

    # ----------------------------
    # Internals
    # ----------------------------

    def _normalize_unit(self, u: str) -> str:
        if not u:
            return "unknown"
        s = str(u).strip().lower()

        # weight
        if s in ("ea", "each", "unit", "units", "ct", "count"):
            return "each"
        if s in ("lb", "lbs", "#", "pound", "pounds"):
            return "lb"
        if s in ("kg", "kgs", "kilogram", "kilograms"):
            return "kg"
        if s in ("g", "gram", "grams"):
            return "g"
        if s in ("oz", "ounce", "ounces"):
            return "oz"

        # volume
        if s in ("l", "litre", "litres", "liter", "liters"):
            return "L"
        if s in ("ml", "millilitre", "millilitres", "milliliter", "milliliters"):
            return "ml"
        if s in ("fl oz", "fl_oz", "floz", "fluid oz", "fluid ounce", "fluid ounces"):
            return "fl_oz"
        if s in ("cup", "cups"):
            return "cup"
        if s in ("tbsp", "tablespoon", "tablespoons"):
            return "tbsp"
        if s in ("tsp", "teaspoon", "teaspoons"):
            return "tsp"
        if s in ("gal", "gallon", "gallons"):
            return "gal"
        if s in ("pint", "pints", "pt"):
            return "pint"

        # count / pack
        if s in ("dozen", "doz"):
            return "dozen"
        if s in ("bunch", "bunches"):
            return "bunch"
        if s in ("case", "cases"):
            return "case"
        if s in ("pack", "packs", "package", "packages", "pkg"):
            return "pack"

        return "unknown"

    def _convert(self, *, unit_price: float, from_unit: str, to_unit: str) -> Optional[float]:
        """
        Convert a per-unit price between compatible units.

        Strategy: convert both units to a common base, then scale.
          Weight base: kg
          Volume base: L
          Count base:  each (dozen only)
        """
        from_unit = self._normalize_unit(from_unit)
        to_unit = self._normalize_unit(to_unit)

        if from_unit == to_unit:
            return float(unit_price)

        p = float(unit_price)

        if from_unit in _WEIGHT_KG_PER_UNIT and to_unit in _WEIGHT_KG_PER_UNIT:
            factor = _WEIGHT_KG_PER_UNIT[to_unit] / _WEIGHT_KG_PER_UNIT[from_unit]
            return p * factor

        if from_unit in _VOLUME_L_PER_UNIT and to_unit in _VOLUME_L_PER_UNIT:
            factor = _VOLUME_L_PER_UNIT[to_unit] / _VOLUME_L_PER_UNIT[from_unit]
            return p * factor

        # dozen <-> each
        if from_unit == "dozen" and to_unit == "each":
            return p / 12.0
        if from_unit == "each" and to_unit == "dozen":
            return p * 12.0

        return None
