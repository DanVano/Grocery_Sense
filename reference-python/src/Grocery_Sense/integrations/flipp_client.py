"""
Grocery_Sense.integrations.flipp_client

Flipp/Wishabi API client.

Uses the undocumented backflipp.wishabi.com search endpoint — reverse-engineered,
no official support. Confirmed working as of 2026-06 for Canadian postal codes.
No API key required; standard browser User-Agent is sufficient.

Risk: endpoint may change or rate-limit without notice. The client fails loud on
HTTP errors (CLAUDE.md rule) and the sync pipeline falls back gracefully to an
empty deal list per store.

Enable by setting FLIPP_POSTAL_CODE env var (or via household postal_code in
config). If postal_code is blank, fetch_flyers_for_store() returns [] without
making any HTTP call.

Expected deal dict keys returned by fetch_flyers_for_store():
    title       : str          e.g. "Chicken Thighs Family Pack"
    description : str          optional longer description
    price_text  : str          e.g. "$5.99/kg"  (display string)
    unit_price  : float|None   numeric price per unit
    unit        : str          "kg", "lb", "each", etc.
    valid_from  : str          "YYYY-MM-DD"  (can be empty if unknown)
    valid_to    : str          "YYYY-MM-DD"  (can be empty if unknown)
"""

from __future__ import annotations

import os
import re
import time
from typing import Any, Dict, List, Optional

try:
    import requests as _requests
    _HAS_REQUESTS = True
except ImportError:
    _HAS_REQUESTS = False

# ---------------------------------------------------------------------------
# Endpoint configuration (override via env for testing / future migration)
# ---------------------------------------------------------------------------

_SEARCH_URL = os.environ.get(
    "FLIPP_SEARCH_URL",
    "https://backflipp.wishabi.com/flipp/items/search",
)
_TIMEOUT_SECONDS = 15
_USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
_MAX_ITEMS_PER_STORE = 200

# Retry-After handling: if the server returns 429, honour the header up to this ceiling.
_RETRY_AFTER_MAX = 30


def _parse_unit_price(item: Dict[str, Any]) -> Optional[float]:
    """Extract the best numeric price from a Wishabi item dict."""
    for key in ("current_price", "sale_price", "pre_price"):
        v = item.get(key)
        if v is not None:
            try:
                f = float(v)
                if f > 0:
                    return f
            except (TypeError, ValueError):
                pass
    # Fall back to parsing the price_text / unit_price_text strings
    for key in ("unit_price_text", "price_text", "current_price_text"):
        text = str(item.get(key) or "")
        m = re.search(r"\$?([\d]+\.[\d]{1,2})", text)
        if m:
            try:
                f = float(m.group(1))
                if f > 0:
                    return f
            except ValueError:
                pass
    return None


def _parse_unit(item: Dict[str, Any]) -> str:
    """Guess the unit from the item dict."""
    text = " ".join(
        str(item.get(k) or "")
        for k in ("name", "unit_price_text", "price_text", "current_price_text")
    )
    text = text.lower()
    if "/kg" in text or "per kg" in text:
        return "kg"
    if "/lb" in text or "per lb" in text:
        return "lb"
    if "/l" in text or "per litre" in text or "per liter" in text:
        return "L"
    if "each" in text or "/ea" in text:
        return "each"
    return "each"


def _item_to_deal(item: Dict[str, Any]) -> Dict[str, Any]:
    return {
        "title": str(item.get("name") or item.get("brand") or "").strip(),
        "description": str(item.get("description") or "").strip(),
        "price_text": str(item.get("current_price_text") or item.get("price_text") or "").strip(),
        "unit_price": _parse_unit_price(item),
        "unit": _parse_unit(item),
        "valid_from": str(item.get("valid_from") or "").strip(),
        "valid_to": str(item.get("valid_to") or "").strip(),
    }


class FlippClient:
    """
    Flipp/Wishabi API client.

    Returns [] when:
    - `requests` is not installed
    - postal_code is blank
    - FLIPP_DISABLED env var is set (useful for CI)
    Raises on HTTP errors (fail loud per CLAUDE.md).
    """

    def fetch_flyers_for_store(
        self,
        store_name: str,
        postal_code: str,
    ) -> List[Dict[str, Any]]:
        """
        Fetch active flyer deals for a single store from the Wishabi search API.

        Returns a list of deal dicts (see module docstring for schema).
        Returns [] (no HTTP call) when postal_code is blank or requests unavailable.
        """
        if not _HAS_REQUESTS:
            return []

        if os.environ.get("FLIPP_DISABLED"):
            return []

        pc = (postal_code or "").strip().replace(" ", "").upper()
        if not pc:
            return []

        # The Wishabi search API: search by store name as the query term so
        # we get items that mention that retailer. A per-store flyer endpoint
        # exists but requires flyer IDs obtained separately; this is simpler
        # and covers the same data for small-batch family use.
        params = {
            "locale": "en-ca",
            "postal_code": pc,
            "q": store_name,
        }
        headers = {"User-Agent": _USER_AGENT}

        response = _requests.get(
            _SEARCH_URL,
            params=params,
            headers=headers,
            timeout=_TIMEOUT_SECONDS,
        )

        if response.status_code == 429:
            retry_after = int(response.headers.get("Retry-After", "5"))
            retry_after = min(retry_after, _RETRY_AFTER_MAX)
            time.sleep(retry_after)
            # Retry once
            response = _requests.get(
                _SEARCH_URL,
                params=params,
                headers=headers,
                timeout=_TIMEOUT_SECONDS,
            )

        response.raise_for_status()

        data = response.json()
        items = data.get("items") or []
        if not isinstance(items, list):
            raise ValueError(
                f"Unexpected Flipp API response shape for {store_name!r}: "
                f"expected 'items' list, got {type(items).__name__}"
            )

        deals = []
        for raw in items[:_MAX_ITEMS_PER_STORE]:
            if not isinstance(raw, dict):
                continue
            deal = _item_to_deal(raw)
            if deal["title"]:
                deals.append(deal)

        return deals
