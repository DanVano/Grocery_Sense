"""
Phase 3 — trip cost modelling + schema version ledger.

#5: distance_km stored per store; trip penalty uses it; unknown → flat fallback.
#7: schema_version stamped at 1 on first migrate; re-running is idempotent.
"""
from __future__ import annotations

import sqlite3
from pathlib import Path

import pytest

from Grocery_Sense.data.repositories.stores_repo import (
    create_store,
    get_store_by_id as get_store,
    set_store_distance_km,
)
from Grocery_Sense.data.schema import _get_schema_version, _SCHEMA_VERSION
from Grocery_Sense.data.connection import connection_scope, current_db_path
from Grocery_Sense.services.basket_optimizer_service import _compute_trip_penalty


# ---------------------------------------------------------------------------
# #5 — distance_km column + trip penalty
# ---------------------------------------------------------------------------

def test_distance_km_stored_and_retrieved(isolated_db):
    store = create_store("FarMart")
    set_store_distance_km(store.id, 12.5)
    refreshed = get_store(store.id)
    assert refreshed is not None
    assert refreshed.distance_km == pytest.approx(12.5)


def test_distance_km_cleared(isolated_db):
    store = create_store("NearMart")
    set_store_distance_km(store.id, 5.0)
    set_store_distance_km(store.id, None)
    refreshed = get_store(store.id)
    assert refreshed.distance_km is None


class _FakeStore:
    def __init__(self, distance_km):
        self.distance_km = distance_km
        self.is_favorite = False


def test_trip_penalty_with_known_distances():
    # 2 * max(5, 10) km * 0.18 $/km = 3.60
    a = _FakeStore(5.0)
    b = _FakeStore(10.0)
    penalty = _compute_trip_penalty(a, b)
    assert penalty == pytest.approx(2.0 * 10.0 * 0.18, rel=0.01)


def test_trip_penalty_fallback_when_no_distances():
    from Grocery_Sense.services.basket_optimizer_service import _FLAT_TRIP_PENALTY
    a = _FakeStore(None)
    b = _FakeStore(None)
    assert _compute_trip_penalty(a, b) == pytest.approx(_FLAT_TRIP_PENALTY)


def test_trip_penalty_one_known_one_unknown():
    a = _FakeStore(8.0)
    b = _FakeStore(None)
    # only a's distance known → use it
    penalty = _compute_trip_penalty(a, b)
    assert penalty == pytest.approx(2.0 * 8.0 * 0.18, rel=0.01)


# ---------------------------------------------------------------------------
# #7 — schema version ledger
# ---------------------------------------------------------------------------

def test_schema_version_stamped(isolated_db):
    with connection_scope() as conn:
        cur = conn.cursor()
        version = _get_schema_version(cur)
    assert version == _SCHEMA_VERSION


def test_schema_version_idempotent(isolated_db):
    from Grocery_Sense.data.schema import _migrate
    with connection_scope() as conn:
        _migrate(conn)  # run again
        cur = conn.cursor()
        version = _get_schema_version(cur)
    assert version == _SCHEMA_VERSION


def test_schema_version_row_count_is_one(isolated_db):
    with connection_scope() as conn:
        rows = conn.execute("SELECT COUNT(*) FROM schema_version").fetchone()
    assert rows[0] == 1
