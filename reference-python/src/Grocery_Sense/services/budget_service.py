from __future__ import annotations

from datetime import datetime, timezone
from typing import Any, Dict, List, Optional

from Grocery_Sense.config import config_store
from Grocery_Sense.data.repositories import receipts_repo


def _current_year_month() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m")


def get_budget_status() -> Dict[str, Any]:
    """
    Return this month's spend vs the configured budget.

    Keys:
      month          - 'YYYY-MM'
      spent          - float, sum of receipt totals this month
      receipt_count  - int
      budget         - float | None  (None means not set)
      remaining      - float | None
      pct_used       - float | None  (0–1+)
      over_budget    - bool | None
      status         - 'ok' | 'warning' | 'over' | 'unset'
    """
    month = _current_year_month()
    row = receipts_repo.get_month_spend(month)
    spent = row["total"]
    receipt_count = row["receipt_count"]

    cfg = config_store.load_config()
    budget = cfg.monthly_budget

    if budget is None:
        return {
            "month": month,
            "spent": spent,
            "receipt_count": receipt_count,
            "budget": None,
            "remaining": None,
            "pct_used": None,
            "over_budget": None,
            "status": "unset",
        }

    remaining = budget - spent
    pct_used = spent / budget if budget > 0 else 0.0
    if pct_used > 1.0:
        status = "over"
    elif pct_used >= 0.85:
        status = "warning"
    else:
        status = "ok"

    return {
        "month": month,
        "spent": spent,
        "receipt_count": receipt_count,
        "budget": budget,
        "remaining": remaining,
        "pct_used": pct_used,
        "over_budget": remaining < 0,
        "status": status,
    }


def get_trend(months: int = 12) -> List[Dict[str, Any]]:
    """Return monthly spend for the last N months (oldest first)."""
    return receipts_repo.get_spend_trend(months=months)


def save_monthly_budget(amount: Optional[float]) -> None:
    """Persist a new monthly budget (None to clear)."""
    cfg = config_store.load_config()
    cfg.monthly_budget = amount if (amount is not None and amount > 0) else None
    config_store.save_config(cfg)


def save_gas_cost_per_km(rate: float) -> None:
    """Persist gas cost per km (must be positive)."""
    if rate <= 0:
        raise ValueError(f"gas_cost_per_km must be positive, got {rate}")
    cfg = config_store.load_config()
    cfg.gas_cost_per_km = float(rate)
    config_store.save_config(cfg)


def get_gas_cost_per_km() -> float:
    return config_store.load_config().gas_cost_per_km
