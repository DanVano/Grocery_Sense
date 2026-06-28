# Implementation Notes

Running log of non-obvious decisions made during the Python → C# port. One section per task.
Cross-reference: `PORTING.md` (playbook), `CONTRACT_AUDIT.md` (Port/Replace/Defer ledger).

---

## Phase 2 · Task 10 — ConfigStore

- **Path:** ctor takes the app-data dir; DI derives it from the db path's directory (config json sits
  beside `grocery.db`). No source-relative path — mobile revokes those.
- **Scope (per CONTRACT_AUDIT lines 142–144):** implemented the stub's exact surface = the v1
  single-profile subset.
  - **Deferred to v2:** all multi-member CRUD (add/rename/delete/primary, member switching).
  - **Deferred to consumers:** `get_user_profile` (only consumer is MealSuggestion, Phase 7/v2),
    `get_postal_code` / `get_store_priority` (trivial reads, add when Phase 4 needs them).
  - **Kept structural, deferred field-level:** the 50-line `ensure_member_profile_defaults` sanitizer →
    `EnsureHousehold` only fixes structure; per-field sanitization moves to PreferencesService Phase 3
    (audit marks it Replace there).
- **GasCostPerKm** kept in the record (cut by the optimizer redesign) but never surfaced —
  normalized-valid, not deleted. Deleting it is a Models change for later.
- **Skipped `sort_keys`** (no STJ built-in; only affects diff stability, not behavior).

---

## Phase 3 — Price math + preferences

Tasks: UnitNormalization, MultiBuy, IngredientMapping, PriceHistory, single-profile Preferences.
All Phase-1 skipped fixtures now run green + a preference-merge test. 184 tests, 0 skipped.

- **No `ensure_schema` anywhere.** Python lazily `ALTER TABLE`s `items.default_unit` and
  `prices.norm_unit_price/norm_unit/norm_note` on first use; in C# those columns come from the migration
  ledger (`Database.cs`), so the runtime DDL dance is gone.
- **Connection model:** services that touch the DB take the caller's `conn` (+ optional `tx`) or inject
  `SqliteConnectionFactory` and open per call (mirrors Python's `connection_scope`) — never a global.
  DI resolves the factory, so no registration changes. UnitNorm's DB methods (`Normalize`/`GetItemDefaultUnit`/
  `SetItemDefaultUnitIfMissing`) gained a `conn` param vs the stub so Phase-5 ingest can backfill `default_unit`
  inside its own transaction.
- **IngredientMapping / FuzzySharp scoring (per PORTING):** FuzzySharp `TokenSortScorer` returns 0–100; we
  divide by 100 and compare against the fractional thresholds (accept 0.78, learn 0.90), documented at the
  call site. The `alias_ambiguity` collision guards (`oil`↛`olive oil`, `cream`↛`ice cream`, bare `chicken`
  across multiple canonicals) all pass, so FuzzySharp tracks rapidfuzz `token_sort_ratio` closely enough — no
  custom scorer needed. Auto-learned aliases + cache touches stay buffered and flushed in one transaction.
- **PriceHistory dict returns → typed records** (`ItemStats`, `StoreStats`, `DealClassification`) per the
  convention. Ported the full public surface (incl. `record_manual_price`, `get_baseline_prices`,
  `stats_for_item_by_store`, `describe_item_history`) since Phase-4 Planning/Optimizer consume them.
- **PreferencesService = single-profile Replace, not a port.** Implemented only
  `ComputeEffectivePreferences`; **removed (not stubbed)** the v2 / Phase-8-UI methods (`GetMealProfile`,
  `GetHouseholdBaselineProfile`, `GetEffectiveEditStateForMember`, `ValidateAddExclude`,
  `ResetSecondaryMemberToHouseholdBaseline`).
  - `EffectivePreferences` rebuilt as a single-profile data class: hard = allergies + hard_excludes;
    soft = soft_excludes; proteins/oils/weights from the profile. Member-name **starring** and **strong-soft
    consensus** dropped (both need ≥2 members → v2); the old `SoftExcluders`/`IsStrongSoftExcluded` API replaced
    with `IsSoftExcluded`.
  - **Field-level profile sanitization is done lazily at read time**, not as a ported
    `ensure_member_profile_defaults`: `Compute()` coerces each value via `NormList`/`NormWeights` (handles both
    fresh `List`/`Dictionary` and reloaded `JsonElement`). This is where the Task-10 "deferred field-level
    sanitization" actually landed.
  - Cache invalidates via the `ConfigStore.Changed` event from Task 10 (Save only; an out-of-band file edit
    won't refresh until next Save — fine for the single-user app that owns the file).

---

## Phase 5 — Receipt ingest

`ReceiptIngestionService` (DB half) + `AzureReceiptOcrClient` (API half) behind `IReceiptOcrClient`.
Pipeline: file-SHA256 dedupe -> OCR -> signature dedupe (merchant+date+total) -> per-line resolve
(IngredientMapping + UnitNormalization + MultiBuy) -> single-transaction write of
receipts/raw_json/line_items/prices + dedupe links. Item/alias/unit writes happen BEFORE the receipt
transaction (matches Python). 212 tests, 0 skipped.

- **SQL stays in `ReceiptsRepo`.** Added `FindReceiptIdByFileHash`, `FindReceiptIdBySignature`, and a
  transactional `IngestReceipt(...)`; the service owns the receipt transaction. Failure leaves zero
  receipt/raw/line/price/dedupe rows; item/alias prep may already have happened.
- **Raw-JSON parser uses the Azure shape.** Tests serialize their canned dictionaries through `JsonSerializer`
  first, so parser coverage uses the same top-level `Dictionary` + nested `JsonElement` shape as live OCR.
- **OCR client returns the raw `analyzeResult` JSON** (REST field shape: `valueString`/`valueArray`/
  `valueCurrency.amount`/...), not the typed SDK model — so the dict matches what the parser navigates. Real
  Azure SDK 1.0.0 signature confirmed against the installed package:
  `new AnalyzeDocumentOptions("prebuilt-receipt", BinaryData.FromBytes(bytes)) { Locale = … }` ->
  `AnalyzeDocumentAsync(WaitUntil.Completed, options, ct)`; operation id via `operation.Id` (try/catch -> GUID).
- **`AzureReceiptOcrClient` is compile-verified only** — needs a live endpoint + key. App composition reads env
  (`GROCERY_SENSE_AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT` / `_API_KEY`) or SecureStorage keys
  (`azure_docint_endpoint` / `azure_docint_api_key`). Behavior is confirmed on-device later; Phase 9 routes it
  through a backend proxy before release. `dotnet test` (Tests project) does not build Integrations, so this file
  is checked with a separate `dotnet build GrocerySense.Integrations`.
- **Ingest uses the injected mapper's 0.78 accept threshold**; Python receipt-ingest used 0.75 — a 3-point
  divergence, not worth a second mapper instance + DI default-value risk.
- **Money binding:** receipts + line-items bound as `decimal` (TEXT columns); prices `unit_price`/`total_price`
  also bound as `decimal`. Both read back cleanly (the prices layer reads `unit_price` as `double`, which
  parses the same TEXT). `norm_unit_price`/`quantity` stay `double` (REAL).
- **`IngestOutcome` expanded** with `DuplicateReason` ("file_hash"|"signature") and `ReplacedExisting`.
