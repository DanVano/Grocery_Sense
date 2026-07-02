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

---

## Phase 6 — Flyer pipeline

`FlyerDocIntClient` (Azure prebuilt-layout, API half) behind a new `IFlyerLayoutClient` Core seam +
`FlyerIngestService` (manual asset ingest, DB half) + `FlyerSyncService`/`FlyerSyncScheduler` (provider
sync, redesigned for mobile). `FlippClient` stays the empty `IFlyerProvider` stub. 262 tests, 0 skipped;
App head builds on net10.0-windows.

- **New seam `IFlyerLayoutClient` (not `IFlyerProvider`).** Two distinct flyer flows: manual photo →
  layout OCR → ingest, and auto-sync → provider deals. They need different abstractions. `IFlyerProvider`
  (FlippClient) is the sync provider; `IFlyerLayoutClient` (FlyerDocIntClient) is the layout client the
  ingest service depends on — mirrors `IReceiptOcrClient`, keeps `Core ↛ Integrations`.
- **FlyerDocIntClient mirrors AzureReceiptOcrClient** but uses `"prebuilt-layout"` (flyers are free-form
  pages, not a receipt schema) and returns the raw `analyzeResult` as `Dictionary<string,object?>`.
  Compile-verified only (`dotnet build GrocerySense.Integrations`); same cred path as receipts (one Azure
  DocumentIntelligence resource → reuses `azure_docint_endpoint`/`_api_key`), de-duplicated in the App root.
- **FlyerIngestService = single transaction (stronger than Python).** Python's `ingest_assets` wrote via
  per-call repo connections; C# does Azure + extraction + item-mapping/unit-norm prep pre-transaction (mapper
  opens its own conns, like receipts), then writes batch/asset/raw-json/deal rows in ONE tx with rollback.
  Deal rows are staged with placeholder `FlyerId`/`AssetId` and stamped via `record with` inside the tx (deals
  FK the just-inserted asset). Raw layout JSON is also dropped to disk as a reprocess cache; a rolled-back DB
  write leaves only those harmless files.
- **Flyers keep `item_id` NULL when unmapped** — unlike receipts, flyer ingest does NOT auto-create items
  (matches Python `_map_to_item`). Reuses the injected mapper's 0.78 accept (Python flyer used 0.75 — same
  3-point divergence noted in Phase 5).
- **Navigator handles plain dicts AND `JsonElement`.** `AsList` accepts `IReadOnlyList`/`IEnumerable`
  (canned test data) and `JsonElement` arrays (live Azure) — so the canned fake needs no JSON round-trip,
  unlike the receipt tests.
- **Scope:** only `ingest_assets` ported (the v1 manual-photo path, the scaffold's surface). Python's
  `ingest_dealrecords_json` (pre-extracted JSON) has no v1 route → skipped until one needs it.
- **Money-parse guards** (`SafeFloatMoney` strict-money regex, `ExtractPriceText` flyer forms) ported as
  `internal` + `InternalsVisibleTo("GrocerySense.Tests")` for direct unit coverage — they are the
  "never fabricate a price" path (CLAUDE: fail loud, never fake).
- **Scheduler redesign (locked decision): sync-on-resume + manual button, no background timer.** Python armed
  a `threading.Timer` hourly poll; iOS/Android restrict background execution. `FlyerSyncScheduler` exposes
  `CheckOnResumeAsync` (throttled) + `RequestSyncAsync` (manual force), single-flighted via a `SemaphoreSlim`
  (a resume tick + a button press can race; the loser returns `"busy"`), and fires `SyncCompleted` after a run
  so the App can trigger the price-drop alert check (the C# analog of Python's `on_sync_complete`). The App
  wires these to lifecycle/button (deferred — UI is Phase 8).
- **Sync throttle meta lives beside the DB** (`flyer_sync_meta.json` in the app-data dir, derived from
  `factory.DbPath`), atomic temp→replace. Unreadable/malformed meta counts as "never synced" (fail toward
  syncing). 3.5-day interval; `force` bypasses it.
- **Sync persists raw provider deals (no mapping).** `RunSyncAsync` maps provider dicts → `flyer_deals` rows
  (mirrors Python `insert_deals`: `deal_total` from `deal_total`/`price`, no unit-norm/item-map) in one tx per
  store, with per-store error capture. FlippClient stub returns `[]`, so production inserts nothing until a
  real provider lands — by design (don't fabricate deals).
- **`FlyerIngestResult` reshaped** from the speculative `(BatchesCreated, DealsCreated, SkippedUrls, Errors)`
  to `(FlyerId, AssetsCount, DealsCount, RawJsonCount)` to match what a per-call ingest produces.
  `FlyerSyncResult` was already correct.

---

## Phase 8 — UI: Blazor routes

Seven routes (Home + Receipts, Shopping List, Deals, Plan, Budget, Preferences, Stores) on MudBlazor
9.6.0. 276 tests, 0 skipped; App head builds on net10.0-windows.

- **PlanningService implemented as a prerequisite** (Phase 4 left it a stub; the Plan route needs it).
  Straight port of `planning_service.py` — greedy cheapest-store-per-item, store scoring (+0.5 favorite,
  +0.1 x priority), favorite fallback when there's no history, and cost/coverage/baseline from the same
  batched avg-price maps the optimizer uses. **Return type is typed, not the Python dict**
  (`StorePlanResult`/`PlanStoreGroup`/`PlanCosts`/`PlanCoverage`) — the UI binds a model. Ported
  `test_planning_service.py` (14 cases).
- **DealsService stayed a stub — v1 doesn't need it.** The Deals route reads `FlyersRepo.ListActiveDeals`
  directly; `DealsService` is provider-search + min-trip selection (v2, Flipp stub). Confirmed the two
  Phase-8 routes that the handoff flagged (Deals, Plan) do NOT require it.
- **MudBlazor over the template's Bootstrap.** Dropped Counter/Weather/NavMenu + the bootstrap payload;
  `MainLayout` is a MudLayout appbar+drawer. `AddMudServices` + the four MudBlazor providers in the layout;
  css/js swapped in `index.html`.
- **Startup state machine** (`AppStartup`): `Database.Initialize` runs on a background thread via a
  single-flight `Lazy<Task>`; `MainLayout` renders spinner/error(MudAlert)/`@Body` off the status — the
  first frame never blocks on migrations, and a DB error is shown verbatim (no silent retry).
- **Sync-on-resume hook landed here** (Phase 6 deferred it to the UI). `App.CreateWindow` subscribes
  `Window.Resumed -> FlyerSyncScheduler.CheckOnResumeAsync`, gated on DB-ready and swallowing background
  errors (the next manual sync surfaces them). Manual "Sync flyers" button on the Deals route uses
  `RequestSyncAsync` with a `CancellationToken` + cancel button.
- **Receipt input is stream-first (mobile rule).** `FilePicker` -> copy the stream into
  `AppDataDirectory/receipts` BEFORE ingest (Android picker paths aren't persistent), then
  `IngestReceiptFileAsync` with cancel + staged progress. Failed/duplicate/cancelled imports delete the
  copied file so nothing orphans.
- **Retention lever:** a receipt image lives beside the DB for as long as its row. The Receipts route has
  delete-receipt (rows backed up, image file removed) and delete-image-keep-data. No receipt contents or
  secrets are logged.
- **Deal filtering reuses the optimizer's `PhraseSafeHit`** (deal titles are free text): household
  hard-excludes hide deals (hidden count shown), soft-excludes get an "avoid" chip.
- **Preferences = single-profile editor** over ConfigStore: postal/city, allergies + hard/soft excludes
  (comma lists on the master member profile, **other profile keys preserved** on save), and the three
  optimizer knobs (percent in the UI, stored as a fraction). Save read-modify-Saves the whole UserConfig.
- **All DB/service calls run off the UI thread** (`Task.Run`) with errors funneled into a MudAlert per page
  — never block the WebView thread, never swallow.
