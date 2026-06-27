from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple

from rapidfuzz import fuzz, process  # pip install rapidfuzz

from Grocery_Sense.data.connection import connection_scope
from Grocery_Sense.data.repositories.item_aliases_repo import ItemAliasesRepo


@dataclass
class MappingResult:
    item_id: Optional[int]
    canonical_name: Optional[str]
    confidence: float
    method: str               # "alias", "fuzzy", "none"
    normalized_input: str
    matched_text: Optional[str]
    debug: Dict[str, str]


class IngredientMappingService:
    """
    Map noisy user/receipt ingredient strings to a canonical item_id.

    Strategy:
      1) Normalize + expand abbreviations
      2) Check exact alias cache (item_aliases table)
      3) Fuzzy match against canonical item names
      4) Optionally auto-learn high-confidence matches into alias cache
    """

    # You can expand this over time.
    DEFAULT_ABBREV: Dict[str, str] = {
        "chk": "chicken",
        "thg": "thigh",
        "thgh": "thigh",
        "brst": "breast",
        "grnd": "ground",
        "bf": "beef",
        "pork": "pork",
        "skls": "skinless",
        "bnls": "boneless",
        "bp": "boneless",   # NOTE: may not always mean boneless; you can tweak later.
        "pkg": "pack",
        "vp": "value pack",
        "lg": "large",
        "sm": "small",
        "org": "organic",
    }

    STOPWORDS = {
        "fresh", "large", "small", "pack", "value", "bulk",
        "club", "family", "tray", "super", "store",
    }

    def __init__(
        self,
        items_repo,
        aliases_repo: Optional[ItemAliasesRepo] = None,
        abbrev_map: Optional[Dict[str, str]] = None,
        auto_learn: bool = True,
        learn_threshold: float = 0.90,
        accept_threshold: float = 0.78,
    ) -> None:
        """
        items_repo must provide:
            - list_all_item_names() -> List[Tuple[int, str]]
              (item_id, canonical_name)

        If yours is different, tell me and I’ll adapt it.
        """
        self.items_repo = items_repo
        self.aliases_repo = aliases_repo or ItemAliasesRepo()
        self.abbrev_map = abbrev_map or self.DEFAULT_ABBREV
        self.auto_learn = auto_learn
        self.learn_threshold = learn_threshold
        self.accept_threshold = accept_threshold

        # Candidate-name list is loaded once per run and reused: callers map many
        # strings against the same catalog in a loop, so re-querying
        # list_all_item_names() per call was a full-table read per ingredient.
        self._choices_cache: Optional[List[Tuple[int, str]]] = None
        self._choice_names: Optional[List[str]] = None
        # High-confidence auto-learns are buffered and written in ONE transaction
        # via flush_learned_aliases(); committing one alias per matched line
        # inside an ingest loop caused write-amplification and DB-lock contention.
        self._pending_learns: List[Tuple[str, int, float, str]] = []
        # Alias cache hits also defer their mark_seen bump to the same flush so
        # N cache hits during a mapping loop don't open N separate write txns.
        self._pending_touches: List[str] = []

    # ---------------- Candidate cache / deferred writes ----------------

    def _get_choices(self) -> Tuple[List[Tuple[int, str]], List[str]]:
        if self._choices_cache is None:
            self._choices_cache = self.items_repo.list_all_item_names()
            self._choice_names = [name for _, name in self._choices_cache]
        return self._choices_cache, self._choice_names  # type: ignore[return-value]

    def invalidate_choices(self) -> None:
        """Drop the cached candidate list.

        Call this whenever a NEW item is created mid-run so subsequent fuzzy
        matches see it (otherwise the cached list would be stale).
        """
        self._choices_cache = None
        self._choice_names = None

    def flush_learned_aliases(self) -> None:
        """Persist buffered auto-learned aliases in a single transaction.

        map_to_item buffers high-confidence learns instead of committing one per
        call. Callers MUST flush after their mapping loop (receipt/flyer ingest,
        weekly plan); for receipt ingest, flush BEFORE opening the receipt
        transaction so alias writes stay outside it.
        """
        if not self._pending_learns and not self._pending_touches:
            return
        pending = self._pending_learns
        self._pending_learns = []
        touches = self._pending_touches
        self._pending_touches = []
        with connection_scope() as conn:
            for alias_text, item_id, confidence, source in pending:
                self.aliases_repo.upsert_alias(
                    alias_text=alias_text,
                    item_id=item_id,
                    confidence=confidence,
                    source=source,
                    conn=conn,
                )
            for alias_text in touches:
                self.aliases_repo.mark_seen(alias_text, conn=conn)

    # ---------------- Normalization ----------------

    def _normalize(self, text: str) -> str:
        t = text.strip().lower()
        t = re.sub(r"[^a-z0-9\s]", " ", t)
        t = re.sub(r"\s+", " ", t).strip()
        return t

    def _expand_abbrev(self, text: str) -> str:
        tokens = text.split()
        expanded = []
        for tok in tokens:
            expanded.append(self.abbrev_map.get(tok, tok))
        return " ".join(expanded)

    def _remove_stopwords(self, text: str) -> str:
        tokens = [t for t in text.split() if t not in self.STOPWORDS]
        return " ".join(tokens)

    def _normalize_pipeline(self, raw: str) -> str:
        t = self._normalize(raw)
        t = self._expand_abbrev(t)
        t = self._normalize(t)
        t = self._remove_stopwords(t)
        t = self._normalize(t)
        return t

    # ---------------- Matching ----------------

    def map_to_item(self, raw_text: str) -> MappingResult:
        normalized = self._normalize_pipeline(raw_text)

        debug = {"raw": raw_text, "normalized": normalized}

        if not normalized:
            return MappingResult(
                item_id=None,
                canonical_name=None,
                confidence=0.0,
                method="none",
                normalized_input=normalized,
                matched_text=None,
                debug=debug,
            )

        # 1) Alias cache hit
        alias = self.aliases_repo.get_by_alias(normalized)
        if alias:
            self._pending_touches.append(normalized)
            return MappingResult(
                item_id=alias.item_id,
                canonical_name=None,  # caller can look up canonical name by id if needed
                confidence=float(alias.confidence),
                method="alias",
                normalized_input=normalized,
                matched_text=normalized,
                debug={**debug, "alias_source": alias.source},
            )

        # 2) Fuzzy match against canonical items (candidate list cached per run)
        choices, names = self._get_choices()
        if not choices:
            return MappingResult(
                item_id=None,
                canonical_name=None,
                confidence=0.0,
                method="none",
                normalized_input=normalized,
                matched_text=None,
                debug={**debug, "error": "No items found in DB"},
            )

        # `names` is the cached list of canonical names aligned with `choices`.
        best = process.extractOne(
            normalized,
            names,
            scorer=fuzz.token_sort_ratio,
        )

        if not best:
            return MappingResult(
                item_id=None,
                canonical_name=None,
                confidence=0.0,
                method="none",
                normalized_input=normalized,
                matched_text=None,
                debug=debug,
            )

        best_name, best_score, best_index = best
        confidence = float(best_score) / 100.0
        best_item_id = choices[best_index][0]

        debug.update({
            "best_name": best_name,
            "best_score": str(best_score),
        })

        if confidence < self.accept_threshold:
            return MappingResult(
                item_id=None,
                canonical_name=None,
                confidence=confidence,
                method="none",
                normalized_input=normalized,
                matched_text=best_name,
                debug=debug,
            )

        # Optional auto-learn: if we’re VERY confident, buffer the alias and let
        # flush_learned_aliases() persist all learns in one transaction.
        if self.auto_learn and confidence >= self.learn_threshold:
            self._pending_learns.append((normalized, best_item_id, confidence, "auto_fuzzy"))

        return MappingResult(
            item_id=best_item_id,
            canonical_name=best_name,
            confidence=confidence,
            method="fuzzy",
            normalized_input=normalized,
            matched_text=best_name,
            debug=debug,
        )
