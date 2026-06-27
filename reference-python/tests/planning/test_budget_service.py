"""
budget_service — full coverage

Covers:
  - get_budget_status() status branches: unset / ok / warning (≥85%) / over (>100%)
  - Edge: exactly 100% spent → 'warning' (not 'over'); over_budget=False
  - Edge: zero-budget guard prevents division-by-zero (pct_used forced to 0.0)
  - receipt_count and spent aggregation from DB
  - Past-month receipts are not counted toward current month
  - save_monthly_budget: positive amount saves; None / 0 / negative clears
  - get_trend: list structure, seeded data visible, ascending month order, lookback cap
"""

from __future__ import annotations

from datetime import datetime, timezone

import pytest

from Grocery_Sense.config import config_store
from Grocery_Sense.data.connection import get_connection
from Grocery_Sense.data.repositories.stores_repo import create_store
from Grocery_Sense.services import budget_service


# ---------------------------------------------------------------------------
# Test-local config isolation (same pattern as tests/preferences/conftest.py)
# ---------------------------------------------------------------------------


@pytest.fixture(autouse=True)
def tmp_config(tmp_path, monkeypatch):
    f = tmp_path / "user_config.json"
    cache = tmp_path / "deals_cache.json"
    monkeypatch.setattr(config_store, "_CONFIG_FILE", f)
    monkeypatch.setattr(config_store, "_CACHE_FILE", cache)
    monkeypatch.setattr(config_store, "_config_cache", None)
    monkeypatch.setattr(config_store, "_config_mtime_key", None)
    monkeypatch.setattr(config_store, "_deals_cache", None)
    monkeypatch.setattr(config_store, "_deals_cache_key", None)
    try:
        from Grocery_Sense.services import preferences_service
        preferences_service._invalidate_effective_cache()
    except Exception:
        pass


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _this_month() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m")


def _insert_receipt(store_id: int, total: float, *, month: str | None = None) -> int:
    """Insert a minimal receipt row. month defaults to current month."""
    m = month or _this_month()
    purchase_date = f"{m}-15"
    with get_connection() as c:
        cur = c.execute(
            """
            INSERT INTO receipts
                (store_id, purchase_date, subtotal_amount, tax_amount, total_amount,
                 source, file_path, image_overall_confidence, azure_request_id)
            VALUES (?, ?, ?, ?, ?, 'receipt', '/tmp/r.pdf', 4, 'req-1')
            """,
            (store_id, purchase_date, round(total * 0.9, 2), round(total * 0.1, 2), total),
        )
        rid = int(cur.lastrowid)
        c.commit()
    return rid


# ---------------------------------------------------------------------------
# get_budget_status
# ---------------------------------------------------------------------------


class TestGetBudgetStatus:
    def test_no_budget_returns_unset(self, isolated_db):
        s = budget_service.get_budget_status()
        assert s["status"] == "unset"
        assert s["budget"] is None
        assert s["remaining"] is None
        assert s["pct_used"] is None
        assert s["over_budget"] is None
        assert s["spent"] == 0.0
        assert s["receipt_count"] == 0

    def test_month_key_matches_current_month(self, isolated_db):
        s = budget_service.get_budget_status()
        assert s["month"] == _this_month()

    def test_no_receipts_is_ok(self, isolated_db):
        budget_service.save_monthly_budget(200.0)
        s = budget_service.get_budget_status()
        assert s["status"] == "ok"
        assert s["spent"] == 0.0
        assert s["pct_used"] == pytest.approx(0.0)
        assert s["over_budget"] is False
        assert s["remaining"] == pytest.approx(200.0)

    def test_below_85_pct_is_ok(self, isolated_db):
        store = create_store(name="Mart")
        budget_service.save_monthly_budget(100.0)
        _insert_receipt(store.id, 84.0)  # 84 %
        s = budget_service.get_budget_status()
        assert s["status"] == "ok"
        assert s["pct_used"] == pytest.approx(0.84)
        assert s["over_budget"] is False

    def test_at_85_pct_is_warning(self, isolated_db):
        store = create_store(name="Mart")
        budget_service.save_monthly_budget(100.0)
        _insert_receipt(store.id, 85.0)  # exactly 85 %
        s = budget_service.get_budget_status()
        assert s["status"] == "warning"
        assert s["pct_used"] == pytest.approx(0.85)
        assert s["over_budget"] is False

    def test_above_85_below_100_is_warning(self, isolated_db):
        store = create_store(name="Mart")
        budget_service.save_monthly_budget(100.0)
        _insert_receipt(store.id, 95.0)
        s = budget_service.get_budget_status()
        assert s["status"] == "warning"

    def test_at_100_pct_is_warning_not_over(self, isolated_db):
        """
        pct_used == 1.0 satisfies >= 0.85 but NOT > 1.0, so status='warning'.
        over_budget uses (remaining < 0), and remaining == 0.0 → False.
        """
        store = create_store(name="Mart")
        budget_service.save_monthly_budget(100.0)
        _insert_receipt(store.id, 100.0)
        s = budget_service.get_budget_status()
        assert s["status"] == "warning"
        assert s["over_budget"] is False
        assert s["remaining"] == pytest.approx(0.0)

    def test_over_100_pct_is_over(self, isolated_db):
        store = create_store(name="Mart")
        budget_service.save_monthly_budget(100.0)
        _insert_receipt(store.id, 110.0)
        s = budget_service.get_budget_status()
        assert s["status"] == "over"
        assert s["over_budget"] is True
        assert s["remaining"] == pytest.approx(-10.0)

    def test_receipt_count_and_sum_aggregated(self, isolated_db):
        store = create_store(name="Mart")
        budget_service.save_monthly_budget(500.0)
        _insert_receipt(store.id, 50.0)
        _insert_receipt(store.id, 60.0)
        _insert_receipt(store.id, 70.0)
        s = budget_service.get_budget_status()
        assert s["receipt_count"] == 3
        assert s["spent"] == pytest.approx(180.0)

    def test_past_month_receipts_excluded(self, isolated_db):
        store = create_store(name="Mart")
        budget_service.save_monthly_budget(100.0)
        _insert_receipt(store.id, 999.0, month="2020-01")
        s = budget_service.get_budget_status()
        assert s["spent"] == 0.0
        assert s["receipt_count"] == 0
        assert s["status"] == "ok"

    def test_zero_budget_pct_guard_no_division_error(self, isolated_db):
        """
        budget=0 can't be persisted via save_monthly_budget (cleared to None),
        but the division guard (pct_used=0 when budget<=0) must not raise.
        Force via direct config to cover the defensive branch.
        """
        cfg = config_store.load_config()
        cfg.monthly_budget = 0.0
        config_store.save_config(cfg)
        s = budget_service.get_budget_status()
        assert s["pct_used"] == pytest.approx(0.0)
        assert s["status"] == "ok"


# ---------------------------------------------------------------------------
# save_monthly_budget
# ---------------------------------------------------------------------------


class TestSaveMonthlyBudget:
    def test_positive_amount_persists(self, isolated_db):
        budget_service.save_monthly_budget(250.0)
        cfg = config_store.load_config()
        assert cfg.monthly_budget == pytest.approx(250.0)

    def test_none_clears_budget(self, isolated_db):
        budget_service.save_monthly_budget(100.0)
        budget_service.save_monthly_budget(None)
        cfg = config_store.load_config()
        assert cfg.monthly_budget is None

    def test_zero_clears_budget(self, isolated_db):
        budget_service.save_monthly_budget(100.0)
        budget_service.save_monthly_budget(0.0)
        cfg = config_store.load_config()
        assert cfg.monthly_budget is None

    def test_negative_clears_budget(self, isolated_db):
        budget_service.save_monthly_budget(100.0)
        budget_service.save_monthly_budget(-50.0)
        cfg = config_store.load_config()
        assert cfg.monthly_budget is None

    def test_budget_reflected_in_get_status(self, isolated_db):
        budget_service.save_monthly_budget(300.0)
        s = budget_service.get_budget_status()
        assert s["budget"] == pytest.approx(300.0)


# ---------------------------------------------------------------------------
# get_trend
# ---------------------------------------------------------------------------


class TestGetTrend:
    def test_empty_db_returns_empty_list(self, isolated_db):
        assert budget_service.get_trend(months=12) == []

    def test_returns_list_type(self, isolated_db):
        assert isinstance(budget_service.get_trend(months=3), list)

    def test_seeded_receipt_appears_in_trend(self, isolated_db):
        store = create_store(name="Mart")
        _insert_receipt(store.id, 75.0)
        trend = budget_service.get_trend(months=12)
        this_month = _this_month()
        entry = next((e for e in trend if e["month"] == this_month), None)
        assert entry is not None
        assert entry["total"] == pytest.approx(75.0)
        assert entry["receipt_count"] == 1

    def test_trend_is_ascending_by_month(self, isolated_db):
        store = create_store(name="Mart")
        _insert_receipt(store.id, 50.0, month="2026-01")
        _insert_receipt(store.id, 60.0, month="2026-02")
        trend = budget_service.get_trend(months=24)
        months = [e["month"] for e in trend]
        assert months == sorted(months)

    def test_lookback_cap_excludes_old_receipts(self, isolated_db):
        store = create_store(name="Mart")
        _insert_receipt(store.id, 999.0, month="2020-01")
        trend = budget_service.get_trend(months=12)
        assert all(e["month"] != "2020-01" for e in trend)
