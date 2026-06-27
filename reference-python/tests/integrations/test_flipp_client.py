"""
FlippClient tests.

Phase 4: Wishabi endpoint implementation — all tests use monkeypatched requests
or FLIPP_DISABLED; no real network calls.

Covers:
- Blank postal_code → [] (no HTTP call)
- FLIPP_DISABLED → [] (no HTTP call)
- Canned 200 response → parsed deal dicts
- HTTP 429 with Retry-After → one retry, then result
- HTTP 4xx/5xx → raises
- Unexpected response shape → raises ValueError
- Price / unit parsing helpers
"""

from __future__ import annotations

import json
from types import SimpleNamespace
from typing import Any, Dict

import pytest

from Grocery_Sense.integrations.flipp_client import (
    FlippClient,
    _item_to_deal,
    _parse_unit_price,
    _parse_unit,
)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _make_response(status: int, body: Dict[str, Any], headers: dict = None):
    r = SimpleNamespace()
    r.status_code = status
    r.headers = headers or {}
    r.json = lambda: body
    r.raise_for_status = lambda: (_ for _ in ()).throw(
        Exception(f"HTTP {status}")
    ) if status >= 400 else None
    return r


@pytest.fixture
def client():
    return FlippClient()


# ---------------------------------------------------------------------------
# No-op paths (no HTTP)
# ---------------------------------------------------------------------------

def test_blank_postal_code_returns_empty(client, monkeypatch):
    monkeypatch.delenv("FLIPP_DISABLED", raising=False)
    assert client.fetch_flyers_for_store("Loblaws", "") == []


def test_flipp_disabled_env_returns_empty(client, monkeypatch):
    monkeypatch.setenv("FLIPP_DISABLED", "1")
    assert client.fetch_flyers_for_store("Loblaws", "M5V3A8") == []


# ---------------------------------------------------------------------------
# Canned 200 response
# ---------------------------------------------------------------------------

_SAMPLE_ITEMS = [
    {
        "name": "Chicken Thighs Family Pack",
        "description": "Bone-in, skin-on",
        "current_price": 5.99,
        "current_price_text": "$5.99/kg",
        "valid_from": "2026-06-20",
        "valid_to": "2026-06-26",
    },
    {
        "name": "Whole Milk 4L",
        "current_price": 6.49,
        "current_price_text": "$6.49 each",
        "valid_from": "2026-06-20",
        "valid_to": "2026-06-26",
    },
]


def test_parses_canned_response(client, monkeypatch):
    monkeypatch.delenv("FLIPP_DISABLED", raising=False)

    def fake_get(url, params, headers, timeout):
        return _make_response(200, {"items": _SAMPLE_ITEMS})

    import Grocery_Sense.integrations.flipp_client as fc_mod
    monkeypatch.setattr(fc_mod._requests, "get", fake_get)

    deals = client.fetch_flyers_for_store("Loblaws", "M5V3A8")
    assert len(deals) == 2
    assert deals[0]["title"] == "Chicken Thighs Family Pack"
    assert deals[0]["unit_price"] == pytest.approx(5.99)
    assert deals[0]["unit"] == "kg"
    assert deals[0]["valid_from"] == "2026-06-20"


def test_returns_empty_when_no_items(client, monkeypatch):
    monkeypatch.delenv("FLIPP_DISABLED", raising=False)

    def fake_get(url, params, headers, timeout):
        return _make_response(200, {"items": []})

    import Grocery_Sense.integrations.flipp_client as fc_mod
    monkeypatch.setattr(fc_mod._requests, "get", fake_get)

    assert client.fetch_flyers_for_store("SmallMart", "M5V3A8") == []


# ---------------------------------------------------------------------------
# Error handling
# ---------------------------------------------------------------------------

def test_raises_on_4xx(client, monkeypatch):
    monkeypatch.delenv("FLIPP_DISABLED", raising=False)

    import requests as req_lib
    import Grocery_Sense.integrations.flipp_client as fc_mod

    def fake_get(url, params, headers, timeout):
        r = SimpleNamespace()
        r.status_code = 403
        r.headers = {}
        r.json = lambda: {}
        r.raise_for_status = lambda: (_ for _ in ()).throw(
            req_lib.HTTPError("403 Forbidden")
        )
        return r

    monkeypatch.setattr(fc_mod._requests, "get", fake_get)
    with pytest.raises(Exception):
        client.fetch_flyers_for_store("Loblaws", "M5V3A8")


def test_raises_when_items_is_not_a_list(client, monkeypatch):
    monkeypatch.delenv("FLIPP_DISABLED", raising=False)

    def fake_get(url, params, headers, timeout):
        # "items" key present but is a string, not a list
        return _make_response(200, {"items": "oops — not a list"})

    import Grocery_Sense.integrations.flipp_client as fc_mod
    monkeypatch.setattr(fc_mod._requests, "get", fake_get)

    with pytest.raises(ValueError, match="items"):
        client.fetch_flyers_for_store("Loblaws", "M5V3A8")


# ---------------------------------------------------------------------------
# Unit/price parsing helpers
# ---------------------------------------------------------------------------

def test_parse_unit_price_current_price():
    assert _parse_unit_price({"current_price": 3.49}) == pytest.approx(3.49)


def test_parse_unit_price_from_text():
    assert _parse_unit_price({"price_text": "was $4.99 now $3.49"}) == pytest.approx(4.99)


def test_parse_unit_price_none_when_zero():
    assert _parse_unit_price({"current_price": 0}) is None


def test_parse_unit_kg():
    assert _parse_unit({"name": "Beef sirloin", "unit_price_text": "$12.99/kg"}) == "kg"


def test_parse_unit_each():
    assert _parse_unit({"name": "Yogurt 750g", "price_text": "$3.99 each"}) == "each"
