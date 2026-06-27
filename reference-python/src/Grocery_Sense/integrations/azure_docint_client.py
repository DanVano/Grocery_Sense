"""
Grocery_Sense.integrations.azure_docint_client

Azure AI Document Intelligence (prebuilt-receipt) -> JSON -> DB ingest

Includes:
- Dedupe layer:
  (1) file hash dedupe (no Azure call needed)
  (2) receipt signature dedupe (merchant+date+total) to catch rescans
- Unit normalization v1:
  - items.default_unit
  - prices.norm_unit_price / prices.norm_unit / prices.norm_note
  - lb <-> kg, g <-> kg
- Multi-buy deal normalization v1:
  - "2/$5", "3 for 10", "2 @ 4.00", "BOGO"
  - compute effective unit price / corrected qty when possible

Default behavior:
- If duplicate found, DO NOT insert a new receipt.
- Returns IngestOutcome with was_duplicate=True, and receipt_id = existing receipt.

Optional:
- replace_existing=True deletes existing receipt + derived rows and ingests new one.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import threading
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional, Tuple

from azure.ai.documentintelligence import DocumentIntelligenceClient
from azure.core.credentials import AzureKeyCredential
from azure.core.exceptions import HttpResponseError, ServiceRequestError
from rapidfuzz import fuzz, process

from Grocery_Sense.data.connection import connection_scope, get_connection
from Grocery_Sense.data.repositories import items_repo as items_repo_module
from Grocery_Sense.data.repositories.item_aliases_repo import ItemAliasesRepo
from Grocery_Sense.data.repositories.stores_repo import create_store, list_stores
from Grocery_Sense.services.ingredient_mapping_service import IngredientMappingService
from Grocery_Sense.services.unit_normalization_service import UnitNormalizationService
from Grocery_Sense.services.multibuy_deal_service import MultiBuyDealService


# =============================================================================
# Dedupe schema helpers
# =============================================================================

_DEDUPE_TABLES_READY = False
_INGEST_TABLES_READY = False
_DOTENV_LOADED = False


def _reset_schema_cache_for_tests() -> None:
    global _DEDUPE_TABLES_READY, _INGEST_TABLES_READY
    _DEDUPE_TABLES_READY = False
    _INGEST_TABLES_READY = False


def _load_dotenv_once() -> None:
    """Idempotently load a top-level `.env` so credentials work per CLAUDE.md
    ("load from .env or config_store.py"). Walks up from this file to find the
    nearest `.env`; sets variables only when not already in os.environ."""
    global _DOTENV_LOADED
    if _DOTENV_LOADED:
        return
    _DOTENV_LOADED = True

    here = Path(__file__).resolve()
    # .../src/Grocery_Sense/integrations/azure_docint_client.py -> parents[3] is the
    # repo root. Bound the search there so an unrelated ancestor .env (e.g. another
    # project's secrets) is never imported into this process.
    repo_root = here.parents[3] if len(here.parents) > 3 else here.parents[-1]
    # Build candidates list from here up to repo_root (inclusive)
    candidates = []
    for parent in here.parents:
        candidates.append(parent)
        if parent == repo_root:
            break

    # Traverse root-first so the repo-root .env wins over a nested one
    for parent in reversed(candidates):
        candidate = parent / ".env"
        if candidate.exists():
            try:
                text = candidate.read_text(encoding="utf-8")
            except OSError as e:
                raise RuntimeError(
                    f"Found .env at {candidate} but could not read it: {e}"
                ) from e
            for line in text.splitlines():
                line = line.strip()
                if not line or line.startswith("#") or "=" not in line:
                    continue
                k, _, v = line.partition("=")
                k = k.strip()
                v = v.strip().strip('"').strip("'")
                if k and k not in os.environ:
                    os.environ[k] = v
            return


def _ensure_dedupe_tables() -> None:
    """No-op: canonical DDL now lives in data/schema.py:create_tables.
    Retained for backwards-compatibility with existing call sites + tests."""
    global _DEDUPE_TABLES_READY
    _DEDUPE_TABLES_READY = True


def _compute_file_sha256(file_path: str | Path, chunk_size: int = 1024 * 1024) -> str:
    p = Path(file_path)
    h = hashlib.sha256()
    with p.open("rb") as f:
        while True:
            chunk = f.read(chunk_size)
            if not chunk:
                break
            h.update(chunk)
    return h.hexdigest()


def _find_receipt_by_file_hash(file_hash: str) -> Optional[int]:
    _ensure_dedupe_tables()
    with connection_scope() as conn:
        row = conn.execute(
            "SELECT receipt_id FROM receipt_file_hashes WHERE file_hash = ?",
            (file_hash,),
        ).fetchone()
        return int(row[0]) if row else None


def _find_receipt_by_signature(signature: str) -> Optional[int]:
    _ensure_dedupe_tables()
    with connection_scope() as conn:
        row = conn.execute(
            "SELECT receipt_id FROM receipt_signatures WHERE signature = ?",
            (signature,),
        ).fetchone()
        return int(row[0]) if row else None


def _link_hash_to_receipt(file_hash: str, receipt_id: int, file_path: str) -> None:
    _ensure_dedupe_tables()
    with connection_scope() as conn:
        conn.execute(
            """
            INSERT OR REPLACE INTO receipt_file_hashes (file_hash, receipt_id, file_path, created_at)
            VALUES (?, ?, ?, ?);
            """,
            (file_hash, int(receipt_id), str(file_path), _now_utc_iso()),
        )
        conn.commit()


def _link_signature_to_receipt(signature: str, receipt_id: int) -> None:
    _ensure_dedupe_tables()
    with connection_scope() as conn:
        conn.execute(
            """
            INSERT OR REPLACE INTO receipt_signatures (signature, receipt_id, created_at)
            VALUES (?, ?, ?);
            """,
            (signature, int(receipt_id), _now_utc_iso()),
        )
        conn.commit()


def _delete_receipt_cascade(receipt_id: int) -> None:
    """
    Deletes a receipt and derived data. Safe if tables exist.
    """
    _ensure_ingest_tables()
    _ensure_dedupe_tables()

    with connection_scope() as conn:
        # child -> parent order
        conn.execute("DELETE FROM prices WHERE receipt_id = ?;", (int(receipt_id),))
        conn.execute("DELETE FROM receipt_line_items WHERE receipt_id = ?;", (int(receipt_id),))
        conn.execute("DELETE FROM receipt_raw_json WHERE receipt_id = ?;", (int(receipt_id),))
        conn.execute("DELETE FROM receipt_file_hashes WHERE receipt_id = ?;", (int(receipt_id),))
        conn.execute("DELETE FROM receipt_signatures WHERE receipt_id = ?;", (int(receipt_id),))
        conn.execute("DELETE FROM receipts WHERE id = ?;", (int(receipt_id),))
        conn.commit()


# =============================================================================
# PART 1: Azure upload/analyze + raw JSON saving
# =============================================================================

@dataclass(frozen=True)
class AzureReceiptResult:
    operation_id: str
    analyze_result: Dict[str, Any]
    saved_json_path: Path


@dataclass(frozen=True)
class IngestOutcome:
    receipt_id: int
    was_duplicate: bool
    duplicate_reason: Optional[str] = None  # "file_hash" | "signature"
    replaced_existing: bool = False
    existing_receipt_id: Optional[int] = None  # if duplicate, which one it matched


def _retry_after_seconds(exc: object) -> Optional[float]:
    """Parse Retry-After (delta-seconds) from an Azure error response, if present.
    Returns None when the header is absent or non-numeric (e.g. HTTP-date form).
    """
    try:
        response = getattr(exc, "response", None)
        if response is None:
            return None
        headers = getattr(response, "headers", None) or {}
        raw = headers.get("Retry-After") or headers.get("retry-after")
        if raw is None:
            return None
        secs = float(str(raw).strip())
        return secs if secs >= 0 else None
    except (TypeError, ValueError):
        return None


class AzureReceiptClient:
    def __init__(
        self,
        endpoint: Optional[str] = None,
        api_key: Optional[str] = None,
        locale: str = "en-US",
    ) -> None:
        _load_dotenv_once()
        self.endpoint = endpoint or os.environ.get("DOCUMENTINTELLIGENCE_ENDPOINT", "").strip()
        self.api_key = api_key or os.environ.get("DOCUMENTINTELLIGENCE_API_KEY", "").strip()
        self.locale = locale

        if not self.endpoint or not self.api_key:
            raise RuntimeError(
                "Missing Azure Document Intelligence credentials.\n"
                "Set DOCUMENTINTELLIGENCE_ENDPOINT and DOCUMENTINTELLIGENCE_API_KEY environment variables."
            )

        self.client = DocumentIntelligenceClient(
            endpoint=self.endpoint,
            credential=AzureKeyCredential(self.api_key),
        )

    def analyze_receipt_file(
        self,
        file_path: str | Path,
        *,
        max_attempts: int = 3,
        base_delay: float = 2.0,
        max_retry_after: float = 60.0,
    ) -> Tuple[str, Dict[str, Any]]:
        p = Path(file_path)
        if not p.exists():
            raise FileNotFoundError(str(p))

        last_exc: Exception = RuntimeError("No attempts made")
        for attempt in range(max_attempts):
            try:
                with p.open("rb") as f:
                    poller = self.client.begin_analyze_document(
                        "prebuilt-receipt",
                        body=f,
                        locale=self.locale,
                    )
                result = poller.result()

                operation_id = str(poller.details.get("operation_id") or "")
                if not operation_id:
                    operation_id = f"op_{datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')}_{p.stem}"

                return operation_id, result.as_dict()

            except HttpResponseError as exc:
                status = exc.status_code if exc.status_code is not None else 0
                # Non-retriable: bad request, auth, not found
                if status in (400, 401, 403, 404):
                    raise
                # Retriable: throttle (429) or server errors (5xx)
                last_exc = exc
                retry_after = _retry_after_seconds(exc)

            except ServiceRequestError as exc:
                # Network-level transient error — always retriable
                last_exc = exc
                retry_after = None

            if attempt < max_attempts - 1:
                backoff = base_delay * (2 ** attempt)
                delay = max(backoff, retry_after) if retry_after is not None else backoff
                delay = min(delay, max_retry_after)
                time.sleep(delay)

        raise last_exc

    def analyze_and_save_json(
        self,
        file_path: str | Path,
        raw_json_dir: str | Path,
    ) -> AzureReceiptResult:
        raw_dir = Path(raw_json_dir)
        raw_dir.mkdir(parents=True, exist_ok=True)

        operation_id, result_dict = self.analyze_receipt_file(file_path)

        src = Path(file_path)
        safe_name = re.sub(r"[^a-zA-Z0-9_\-]+", "_", src.stem)[:80]
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        out_path = raw_dir / f"{safe_name}__{operation_id}__{stamp}.json"
        out_path.write_text(json.dumps(result_dict, ensure_ascii=False, indent=2), encoding="utf-8")

        return AzureReceiptResult(operation_id=operation_id, analyze_result=result_dict, saved_json_path=out_path)


# =============================================================================
# PART 2: Parse receipt JSON + store into Grocery Sense DB
# =============================================================================

def _now_utc_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def _confidence_to_1_5(conf: Optional[float]) -> Optional[int]:
    if conf is None:
        return None
    try:
        c = float(conf)
    except Exception:
        return None
    if c >= 0.90:
        return 5
    if c >= 0.75:
        return 4
    if c >= 0.60:
        return 3
    if c >= 0.40:
        return 2
    return 1


def _safe_float(x: Any) -> Optional[float]:
    if x is None:
        return None
    if isinstance(x, (int, float)):
        return float(x)
    s = str(x).strip()
    # European decimal: "1.234,56" or "1234,56" → comma is the decimal separator.
    if re.match(r"^-?\d{1,3}(\.\d{3})+,\d{1,2}$", s) or re.match(r"^-?\d+,\d{1,2}$", s):
        s = s.replace(".", "").replace(",", ".")
    else:
        s = s.replace(",", "")
    s = re.sub(r"[^\d\.\-]", "", s)
    if not s:
        return None
    try:
        return float(s)
    except Exception:
        return None


def _pick_field(fields: Dict[str, Any], names) -> Optional[Dict[str, Any]]:
    if not fields:
        return None
    lower = {k.lower(): k for k in fields.keys()}
    for n in names:
        key = lower.get(n.lower())
        if key and isinstance(fields.get(key), dict):
            return fields[key]
    return None


def _field_value(field: Optional[Dict[str, Any]]) -> Tuple[Any, Optional[float]]:
    if not field:
        return None, None
    conf = field.get("confidence")
    for k in (
        "valueString",
        "valueNumber",
        "valueDate",
        "valueTime",
        "valuePhoneNumber",
        "valueCurrency",
        "valueInteger",
        "valueBoolean",
    ):
        if k in field:
            return field.get(k), conf
    if "content" in field:
        return field.get("content"), conf
    return None, conf


def _currency_amount(v: Any) -> Optional[float]:
    if v is None:
        return None
    if isinstance(v, dict):
        return _safe_float(v.get("amount"))
    return _safe_float(v)


def _normalize_merchant_name(s: str) -> str:
    s = (s or "").lower().strip()
    s = re.sub(r"\s+", " ", s)
    s = re.sub(r"[^a-z0-9 \-]", "", s)
    return s


def _make_receipt_signature(merchant: str, purchase_date: str, total: Optional[float]) -> Optional[str]:
    """
    Signature to catch duplicates across different photos/scans.
    """
    if not merchant or not purchase_date or total is None:
        return None
    m = _normalize_merchant_name(merchant)
    # 4-decimal format avoids banker's-rounding 0.5-cent collisions.
    return f"{m}|{purchase_date}|{float(total):.4f}"


def _ensure_ingest_tables() -> None:
    """No-op: canonical DDL now lives in data/schema.py:create_tables.
    Retained for backwards-compatibility with existing call sites + tests."""
    global _INGEST_TABLES_READY
    _INGEST_TABLES_READY = True


def _get_or_create_store_id(
    merchant_name: str,
    threshold: int = 85,
    *,
    known_stores: Optional[List[Any]] = None,
) -> int:
    merchant_name = (merchant_name or "").strip() or "Unknown Store"
    stores = known_stores if known_stores is not None else list_stores(only_favorites=False, order_by_priority=True, include_archived=True)
    if not stores:
        return int(create_store(name=merchant_name).id)

    store_names = [s.name for s in stores]
    # One scoring pass: process.extract returns (name, score, index) for all
    # candidates; tie-break from that result instead of re-scoring every store.
    results = process.extract(
        merchant_name, store_names, scorer=fuzz.token_set_ratio, limit=len(store_names)
    )

    if results:
        best_score = results[0][1]
        if best_score >= threshold:
            # Tie-break by shortest matching name (most specific), then alphabetic,
            # for deterministic linking across syncs.
            ties = [stores[idx] for (_n, sc, idx) in results if sc == best_score]
            ties.sort(key=lambda s: (len(s.name), s.name))
            return int(ties[0].id)

    return int(create_store(name=merchant_name).id)


def _insert_receipt_row(
    store_id: int,
    purchase_date: str,
    subtotal: Optional[float],
    tax: Optional[float],
    total: Optional[float],
    source: str,
    file_path: str,
    image_confidence_1_5: Optional[int],
    azure_request_id: str,
) -> int:
    with connection_scope() as conn:
        cur = conn.execute(
            """
            INSERT INTO receipts (
                store_id, purchase_date, subtotal_amount, tax_amount, total_amount,
                source, file_path, image_overall_confidence, keep_image_until,
                azure_request_id, created_at
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
            """,
            (
                store_id,
                purchase_date,
                subtotal,
                tax,
                total,
                source,
                file_path,
                image_confidence_1_5,
                None,
                azure_request_id,
                _now_utc_iso(),
            ),
        )
        rid = int(cur.lastrowid)
        conn.commit()
        return rid


def _save_raw_json_row(receipt_id: int, operation_id: str, json_path: Path, raw_json_dict: Dict[str, Any]) -> None:
    with connection_scope() as conn:
        conn.execute(
            """
            INSERT OR REPLACE INTO receipt_raw_json (receipt_id, operation_id, json_path, raw_json, created_at)
            VALUES (?, ?, ?, ?, ?);
            """,
            (
                int(receipt_id),
                operation_id,
                str(json_path),
                json.dumps(raw_json_dict, ensure_ascii=False),
                _now_utc_iso(),
            ),
        )
        conn.commit()


def _upsert_item_from_mapping(raw_desc: str, mapping: Any) -> Tuple[int, Optional[int]]:
    if getattr(mapping, "item_id", None):
        conf = getattr(mapping, "confidence", None)
        return int(mapping.item_id), _confidence_to_1_5(conf)

    cleaned = (raw_desc or "").strip() or "Unknown Item"
    created = items_repo_module.create_item(canonical_name=cleaned)
    item_id = int(created.id)

    try:
        aliases = ItemAliasesRepo()
        aliases.upsert_alias(alias_text=raw_desc, item_id=item_id, confidence=0.60, source="receipt_auto")
    except Exception:
        pass

    return item_id, 2


def _insert_price_point(
    *,
    item_id: int,
    store_id: int,
    receipt_id: int,
    date: str,
    unit_price: float,
    unit: str,
    quantity: Optional[float],
    total_price: Optional[float],
    raw_name: str,
    confidence_1_5: Optional[int],
    norm_unit_price: Optional[float],
    norm_unit: Optional[str],
    norm_note: Optional[str],
) -> None:
    """
    Inserts into prices, including optional normalization fields.
    Unit normalization schema is ensured by UnitNormalizationService.ensure_schema().
    """
    with connection_scope() as conn:
        conn.execute(
            """
            INSERT INTO prices (
                item_id, store_id, receipt_id, flyer_source_id, source, date,
                unit_price, unit, quantity, total_price, raw_name, confidence,
                norm_unit_price, norm_unit, norm_note,
                created_at
            )
            VALUES (?, ?, ?, NULL, 'receipt', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
            """,
            (
                int(item_id),
                int(store_id),
                int(receipt_id),
                date,
                float(unit_price),
                unit,
                quantity,
                total_price,
                raw_name,
                confidence_1_5,
                norm_unit_price,
                norm_unit,
                norm_note,
                _now_utc_iso(),
            ),
        )
        conn.commit()


def _insert_receipt_line_item(
    receipt_id: int,
    line_index: int,
    item_id: Optional[int],
    description: str,
    quantity: Optional[float],
    unit_price: Optional[float],
    line_total: Optional[float],
    discount: Optional[float],
    confidence_1_5: Optional[int],
) -> None:
    with connection_scope() as conn:
        conn.execute(
            """
            INSERT INTO receipt_line_items (
                receipt_id, line_index, item_id, description, quantity,
                unit_price, line_total, discount, confidence, created_at
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
            """,
            (
                int(receipt_id),
                int(line_index),
                int(item_id) if item_id else None,
                description,
                quantity,
                unit_price,
                line_total,
                discount,
                confidence_1_5,
                _now_utc_iso(),
            ),
        )
        conn.commit()


def _extract_header_for_signature(analyze_result: Dict[str, Any]) -> Tuple[str, str, Optional[float]]:
    """
    Extract merchant, purchase_date (YYYY-MM-DD), total for signature check BEFORE inserting anything.
    """
    docs = analyze_result.get("documents") or []
    if not docs:
        return "", "", None

    receipt_doc = docs[0]
    fields = receipt_doc.get("fields") or {}

    merchant_val, _ = _field_value(_pick_field(fields, ["MerchantName", "Merchant"]))
    merchant = (merchant_val or "").strip() if isinstance(merchant_val, str) else str(merchant_val or "").strip()

    tx_date_val, _ = _field_value(_pick_field(fields, ["TransactionDate", "Date"]))
    if isinstance(tx_date_val, str) and re.match(r"^\d{4}-\d{2}-\d{2}$", tx_date_val.strip()):
        purchase_date = tx_date_val.strip()
    else:
        purchase_date = ""

    total_val, _ = _field_value(_pick_field(fields, ["Total"]))
    total = _currency_amount(total_val)

    return merchant, purchase_date, total


def ingest_analyzed_receipt_into_db(
    *,
    file_path: str | Path,
    operation_id: str,
    analyze_result: Dict[str, Any],
    saved_json_path: Path,
    store_match_threshold: int = 85,
    file_hash: Optional[str] = None,
) -> int:
    """
    Inserts:
      - receipts row
      - receipt_raw_json row
      - receipt_line_items rows
      - prices rows (with norm fields + multi-buy notes)
    Also links file_hash + signature to receipt for dedupe.

    Item resolution (fuzzy mapping, alias upserts, unit-default writes) runs
    BEFORE opening the outer transaction so its nested writes don't deadlock
    against our own write lock. The final inserts run inside a single
    explicit BEGIN/COMMIT for atomicity.
    """
    _ensure_ingest_tables()
    _ensure_dedupe_tables()

    docs = analyze_result.get("documents") or []
    if not docs:
        raise ValueError("No documents found in AnalyzeResult JSON.")
    if len(docs) > 1:
        # Multi-receipt batches are not currently supported; flag for the caller
        # so they can surface a messagebox.showwarning rather than silently
        # dropping the additional documents.
        import warnings
        warnings.warn(
            f"Azure returned {len(docs)} documents; only the first will be ingested.",
            stacklevel=2,
        )

    receipt_doc = docs[0]
    fields = receipt_doc.get("fields") or {}

    # Header fields
    merchant_name_val, merchant_conf = _field_value(_pick_field(fields, ["MerchantName", "Merchant"]))
    merchant_name = (merchant_name_val or "").strip() if isinstance(merchant_name_val, str) else str(merchant_name_val or "").strip()
    store_id = _get_or_create_store_id(merchant_name, threshold=store_match_threshold)

    tx_date_val, tx_date_conf = _field_value(_pick_field(fields, ["TransactionDate", "Date"]))
    if isinstance(tx_date_val, str) and re.match(r"^\d{4}-\d{2}-\d{2}$", tx_date_val.strip()):
        purchase_date = tx_date_val.strip()
    else:
        # File mtime fallback gives a stable per-file date so re-ingest of the
        # same PDF yields the same signature even when Azure can't extract one.
        # Use LOCAL date, not UTC: purchase_date is a calendar day on the
        # receipt (the Azure-extracted path above is a bare local date), so a
        # UTC stamp future-dates evening receipts for UTC-behind timezones.
        try:
            mtime = Path(file_path).stat().st_mtime
            purchase_date = datetime.fromtimestamp(mtime).strftime("%Y-%m-%d")
        except Exception:
            purchase_date = datetime.now().strftime("%Y-%m-%d")
        # Disclose the inferred date: it feeds price-history windows, so a wrong
        # date skews "usual price" / 6-month-low math. Don't pretend it's real.
        import warnings
        warnings.warn(
            f"No transaction date on receipt '{file_path}'; using inferred date "
            f"{purchase_date}. Price-history dating for this receipt is approximate.",
            stacklevel=2,
        )

    subtotal_val, subtotal_conf = _field_value(_pick_field(fields, ["Subtotal"]))
    tax_val, tax_conf = _field_value(_pick_field(fields, ["TotalTax", "Tax"]))
    total_val, total_conf = _field_value(_pick_field(fields, ["Total"]))

    subtotal = _currency_amount(subtotal_val)
    tax = _currency_amount(tax_val)
    total = _currency_amount(total_val)

    signature = _make_receipt_signature(merchant_name, purchase_date, total)

    confs = [c for c in [merchant_conf, tx_date_conf, subtotal_conf, tax_conf, total_conf] if isinstance(c, (int, float))]
    overall_conf_float = (sum(float(x) for x in confs) / len(confs)) if confs else None
    overall_conf_1_5 = _confidence_to_1_5(overall_conf_float)

    # Mapping engine + normalization (each opens its own connection).
    mapping_service = IngredientMappingService(
        items_repo=items_repo_module,
        aliases_repo=ItemAliasesRepo(),
        auto_learn=True,
        learn_threshold=0.90,
        accept_threshold=0.75,
    )
    unit_norm = UnitNormalizationService()
    unit_norm.ensure_schema()
    deals = MultiBuyDealService()

    # PASS 1 (pre-transaction): parse + resolve item_ids + run unit-norm so that
    # any DB writes those services do (alias upserts, default_unit, item create)
    # are finished before we BEGIN the outer transaction.
    items_field = _pick_field(fields, ["Items", "ItemList", "LineItems"])
    value_array = items_field.get("valueArray") if isinstance(items_field, dict) else None
    if not isinstance(value_array, list):
        value_array = []

    resolved: List[Dict[str, Any]] = []
    for idx, elem in enumerate(value_array):
        obj = (elem or {}).get("valueObject") if isinstance(elem, dict) else None
        if not isinstance(obj, dict):
            continue

        desc_val, desc_conf = _field_value(_pick_field(obj, ["Description", "Name", "Item"]))
        qty_val, qty_conf = _field_value(_pick_field(obj, ["Quantity", "Qty"]))
        unit_price_val, unit_price_conf = _field_value(_pick_field(obj, ["UnitPrice", "Price"]))
        total_price_val, total_price_conf = _field_value(_pick_field(obj, ["TotalPrice", "LineTotal", "Amount"]))
        discount_val, discount_conf = _field_value(_pick_field(obj, ["Discount", "DiscountAmount"]))

        description = (desc_val or "").strip() if isinstance(desc_val, str) else str(desc_val or "").strip()
        if not description:
            continue

        q_parsed = _safe_float(qty_val)
        qty_known = q_parsed is not None and q_parsed > 0
        quantity = q_parsed if qty_known else 1.0
        # OCR reported a quantity but it was unusable (<=0 / non-numeric): we
        # default to 1, which distorts unit_price for weight-priced lines. Flag
        # it rather than store a plausible-but-wrong per-unit price silently.
        qty_reported_but_invalid = (qty_val is not None) and not qty_known
        unit_price = _currency_amount(unit_price_val)
        line_total = _currency_amount(total_price_val)
        discount = _currency_amount(discount_val)

        if line_total is not None and line_total < 0:
            line_total = None
        if unit_price is not None and unit_price < 0:
            unit_price = None
        if unit_price is None and line_total is not None and quantity:
            unit_price = float(line_total) / float(quantity)
        if line_total is None and unit_price is not None and quantity:
            line_total = float(unit_price) * float(quantity)

        adj = deals.adjust(
            description=description,
            quantity=quantity,
            unit_price=unit_price,
            line_total=line_total,
            discount=discount,
        )
        quantity = adj.quantity
        unit_price = adj.unit_price
        line_total = adj.line_total
        deal_note = adj.deal_note
        if qty_reported_but_invalid:
            deal_note = f"{deal_note};qty_invalid_defaulted" if deal_note else "qty_invalid_defaulted"

        conf_candidates = [c for c in [desc_conf, qty_conf, unit_price_conf, total_price_conf, discount_conf] if isinstance(c, (int, float))]
        line_conf_float = (sum(float(x) for x in conf_candidates) / len(conf_candidates)) if conf_candidates else None
        line_conf_1_5 = _confidence_to_1_5(line_conf_float)

        mapping = mapping_service.map_to_item(description)
        item_id, map_conf_1_5 = _upsert_item_from_mapping(description, mapping)
        if not getattr(mapping, "item_id", None):
            # A new item was just created — drop the cached candidate list so
            # later lines in this receipt can fuzzy-match against it.
            mapping_service.invalidate_choices()

        observed_unit = "each"
        guessed = unit_norm.guess_unit_from_text(description)
        if guessed != "unknown":
            observed_unit = guessed

        norm = None
        if unit_price is not None:
            norm = unit_norm.normalize(
                item_id=item_id,
                unit_price=float(unit_price),
                observed_unit=observed_unit,
                description=description,
            )

        resolved.append({
            "idx": idx,
            "description": description,
            "item_id": item_id,
            "quantity": quantity,
            "unit_price": unit_price,
            "line_total": line_total,
            "discount": discount,
            "observed_unit": observed_unit,
            "confidence_1_5": line_conf_1_5 or map_conf_1_5,
            "deal_note": deal_note,
            "norm": norm,
        })

    # Persist all buffered auto-learned aliases in one transaction now, BEFORE
    # the receipt BEGIN below — keeps alias writes out of the receipt
    # transaction and avoids a per-matched-line commit during the loop above.
    mapping_service.flush_learned_aliases()

    # PASS 2 (transactional): insert receipts row + raw_json + line_items + prices
    # + dedupe links in one atomic BEGIN/COMMIT.
    now_iso = _now_utc_iso()
    raw_json_str = json.dumps(analyze_result, ensure_ascii=False)

    with connection_scope() as conn:
        conn.execute("BEGIN;")
        try:
            cur = conn.execute(
                """
                INSERT INTO receipts (
                    store_id, purchase_date, subtotal_amount, tax_amount, total_amount,
                    source, file_path, image_overall_confidence, keep_image_until,
                    azure_request_id, created_at
                )
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                """,
                (
                    store_id, purchase_date, subtotal, tax, total,
                    "receipt", str(file_path), overall_conf_1_5, None,
                    operation_id, now_iso,
                ),
            )
            receipt_id = int(cur.lastrowid)

            conn.execute(
                """
                INSERT OR REPLACE INTO receipt_raw_json (receipt_id, operation_id, json_path, raw_json, created_at)
                VALUES (?, ?, ?, ?, ?);
                """,
                (receipt_id, operation_id, str(saved_json_path), raw_json_str, now_iso),
            )

            line_rows: List[Tuple[Any, ...]] = []
            price_rows: List[Tuple[Any, ...]] = []
            for r in resolved:
                line_rows.append((
                    receipt_id,
                    r["idx"],
                    int(r["item_id"]) if r["item_id"] else None,
                    r["description"],
                    r["quantity"],
                    r["unit_price"],
                    r["line_total"],
                    r["discount"],
                    r["confidence_1_5"],
                    now_iso,
                ))

                if r["unit_price"] is None:
                    continue
                norm = r["norm"]
                combined_note = (f"{norm.note};{r['deal_note']}" if r['deal_note'] else norm.note) if norm else r['deal_note']
                price_rows.append((
                    int(r["item_id"]),
                    int(store_id),
                    int(receipt_id),
                    None,  # flyer_source_id
                    "receipt",
                    purchase_date,
                    float(r["unit_price"]),
                    r["observed_unit"],
                    r["quantity"],
                    r["line_total"],
                    r["description"],
                    r["confidence_1_5"],
                    float(norm.norm_unit_price) if norm and norm.norm_unit_price is not None else None,
                    norm.norm_unit if norm else None,
                    combined_note,
                    now_iso,
                ))

            if line_rows:
                conn.executemany(
                    """
                    INSERT INTO receipt_line_items (
                        receipt_id, line_index, item_id, description, quantity,
                        unit_price, line_total, discount, confidence, created_at
                    )
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                    """,
                    line_rows,
                )

            if price_rows:
                conn.executemany(
                    """
                    INSERT INTO prices (
                        item_id, store_id, receipt_id, flyer_source_id, source, date,
                        unit_price, unit, quantity, total_price, raw_name, confidence,
                        norm_unit_price, norm_unit, norm_note, created_at
                    )
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                    """,
                    price_rows,
                )

            if file_hash:
                conn.execute(
                    """
                    INSERT OR REPLACE INTO receipt_file_hashes (file_hash, receipt_id, file_path, created_at)
                    VALUES (?, ?, ?, ?);
                    """,
                    (file_hash, int(receipt_id), str(file_path), now_iso),
                )
            if signature:
                conn.execute(
                    """
                    INSERT OR REPLACE INTO receipt_signatures (signature, receipt_id, created_at)
                    VALUES (?, ?, ?);
                    """,
                    (signature, int(receipt_id), now_iso),
                )

            conn.commit()
        except Exception:
            conn.rollback()
            raise

    return receipt_id


# =============================================================================
# Public entrypoints
# =============================================================================

# Per-file-hash ingest locks: serialize concurrent imports of the SAME receipt
# file (a double-click, or two import windows) so the check-then-insert dedupe
# can't race. Without this, two threads can both pass the file-hash dedupe before
# either inserts, spending two Azure calls and creating a duplicate receipt. The
# loser now blocks, then sees the winner's committed hash and returns a clean
# was_duplicate outcome. Single-process desktop app, so an in-process lock is
# sufficient (a multi-process story is out of scope).
_ingest_locks_guard = threading.Lock()
_ingest_locks: Dict[str, threading.Lock] = {}


def _lock_for_file_hash(file_hash: str) -> threading.Lock:
    with _ingest_locks_guard:
        lk = _ingest_locks.get(file_hash)
        if lk is None:
            lk = threading.Lock()
            _ingest_locks[file_hash] = lk
        return lk


def ingest_receipt_file_outcome(
    file_path: str | Path,
    *,
    raw_json_dir: str | Path = "azure_raw_json",
    locale: str = "en-US",
    store_match_threshold: int = 85,
    replace_existing: bool = False,
) -> IngestOutcome:
    """Serialize same-file imports under a per-file-hash lock, then run the
    dedupe + ingest pipeline (see _ingest_receipt_outcome_impl)."""
    p = Path(file_path)
    if not p.exists():
        raise FileNotFoundError(str(p))
    with _lock_for_file_hash(_compute_file_sha256(p)):
        return _ingest_receipt_outcome_impl(
            file_path=file_path,
            raw_json_dir=raw_json_dir,
            locale=locale,
            store_match_threshold=store_match_threshold,
            replace_existing=replace_existing,
        )


def _ingest_receipt_outcome_impl(
    file_path: str | Path,
    *,
    raw_json_dir: str | Path = "azure_raw_json",
    locale: str = "en-US",
    store_match_threshold: int = 85,
    replace_existing: bool = False,
) -> IngestOutcome:
    """
    Sequential ingest for ONE receipt file with dedupe logic.

    Behavior:
      1) file-hash dedupe before Azure call
      2) analyze with Azure + save JSON
      3) signature dedupe (merchant+date+total) before DB insert
      4) ingest into DB
    """
    p = Path(file_path)
    if not p.exists():
        raise FileNotFoundError(str(p))

    _ensure_ingest_tables()
    _ensure_dedupe_tables()

    # ---- 1) FILE HASH DEDUPE (no Azure call) ----
    file_hash = _compute_file_sha256(p)
    existing = _find_receipt_by_file_hash(file_hash)
    replaced = False
    backup_id_for_restore: Optional[int] = None
    if existing is not None:
        if replace_existing:
            # Take a snapshot before destroying the prior receipt so we can
            # restore it if the subsequent ingest fails.
            from Grocery_Sense.data.repositories.receipts_repo import delete_receipt_with_backup
            backup_id_for_restore = int(delete_receipt_with_backup(int(existing)))
            replaced = True
        else:
            return IngestOutcome(
                receipt_id=int(existing),
                was_duplicate=True,
                duplicate_reason="file_hash",
                replaced_existing=False,
                existing_receipt_id=int(existing),
            )

    # ---- 2) AZURE ANALYZE ----
    client = AzureReceiptClient(locale=locale)
    az = client.analyze_and_save_json(file_path=p, raw_json_dir=raw_json_dir)

    # ---- 3) SIGNATURE DEDUPE (catches rescans) ----
    merchant, purchase_date, total = _extract_header_for_signature(az.analyze_result)
    signature = _make_receipt_signature(merchant, purchase_date, total)
    if signature:
        existing_sig = _find_receipt_by_signature(signature)
        if existing_sig is not None:
            if replace_existing:
                from Grocery_Sense.data.repositories.receipts_repo import delete_receipt_with_backup
                backup_id_for_restore = int(delete_receipt_with_backup(int(existing_sig)))
                replaced = True
            else:
                # discard the new attempt (don't add to DB)
                try:
                    az.saved_json_path.unlink(missing_ok=True)
                except Exception:
                    pass
                return IngestOutcome(
                    receipt_id=int(existing_sig),
                    was_duplicate=True,
                    duplicate_reason="signature",
                    replaced_existing=False,
                    existing_receipt_id=int(existing_sig),
                )

    # ---- 4) INGEST INTO DB ----
    try:
        new_receipt_id = ingest_analyzed_receipt_into_db(
            file_path=p,
            operation_id=az.operation_id,
            analyze_result=az.analyze_result,
            saved_json_path=az.saved_json_path,
            store_match_threshold=store_match_threshold,
            file_hash=file_hash,
        )
    except Exception:
        # Replace-existing fails: auto-restore the pre-delete backup so the
        # user doesn't silently lose the original receipt.
        if backup_id_for_restore is not None:
            try:
                from Grocery_Sense.data.repositories.receipts_repo import restore_receipt_from_backup
                restore_receipt_from_backup(backup_id_for_restore)
            except Exception:
                pass
        raise

    return IngestOutcome(
        receipt_id=int(new_receipt_id),
        was_duplicate=False,
        duplicate_reason=None,
        replaced_existing=replaced,
        existing_receipt_id=None,
    )


def ingest_receipt_file(
    file_path: str | Path,
    raw_json_dir: str | Path = "azure_raw_json",
    locale: str = "en-US",
    store_match_threshold: int = 85,
) -> int:
    """
    Backwards compatible: returns receipt_id.
    Uses dedupe default behavior (skip duplicates).
    """
    outcome = ingest_receipt_file_outcome(
        file_path=file_path,
        raw_json_dir=raw_json_dir,
        locale=locale,
        store_match_threshold=store_match_threshold,
        replace_existing=False,
    )
    return outcome.receipt_id
