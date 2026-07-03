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

---

## Phase 9 — Platform glue & release readiness (partial)

Local-buildable scope done; the two items that need infra beyond this machine stay deferred.

- **Camera capture via MediaPicker.** The Receipts route now offers Take photo
  (`MediaPicker.CapturePhotoAsync`, guarded on `IsCaptureSupported`) alongside Import from library
  (`FilePicker`). Both return a `FileResult`, so the copy-to-app-data + cancel + retention flow from Phase 8
  is shared in one `AcquireAndIngestAsync(stage, acquirer)`; camera results with no extension fall back to
  `.jpg`.
- **SecureStorage credential entry.** Preferences gained a Cloud OCR (Azure) section that reads/writes
  `azure_docint_endpoint` + `azure_docint_api_key` — the exact keys `ServiceCollectionExtensions` already
  reads when building `AzureReceiptOcrClient`/`FlyerDocIntClient`. Separate Save button (SecureStorage is a
  different store from `user_config.json`); a cleared field calls `Remove` rather than storing empty; secure
  store unavailability surfaces as an error (no faked creds). This is the interim personal-use path.
- **Permissions declared:** Android `CAMERA` + optional camera feature; iOS/MacCatalyst
  `NSCameraUsageDescription` + `NSPhotoLibraryUsageDescription` (required or MediaPicker/FilePicker crash).
- **Deferred (unchanged from the plan):**
  - **OCR backend proxy + per-user rate limiting** — the real shipping story (a public build can't hold a
    shared Azure key). The SecureStorage entry is explicitly *not* that; it's dev/personal use.
  - **Apple heads (`net10.0-ios`, `net10.0-maccatalyst`)** — need a Mac/CI host to build and verify. DI +
    permissions are in place; only Windows + Android build on this machine. Verified on **net10.0-windows**.
- **No test delta** — Phase 9 is platform glue (pickers, secure store, manifests), none of it unit-testable
  in the offline xUnit host. Still 276 tests, 0 skipped; App head builds on net10.0-windows.

---

# v2 — Family release (planned in `V2_PLAN.md`, grilled 2026-07-02)

## v2 Phase 1 — Backfill on-ramp (bulk receipt import)

Split `ReceiptIngestionService` into `PrepareReceiptFileAsync` / `CommitPreparedReceipt`, added
`ImportBatchAsync`, and a `ReceiptDateDialog` + Backfill button on the Receipts route. 299 tests, 0 skipped;
Windows head builds 0/0.

- **The split is behavior-preserving by construction.** `Prepare` does steps 1–4 (file-hash dedupe → OCR →
  signature dedupe → `BuildIngest`); `Commit` does step 5 (the transaction). The one-shot
  `IngestReceiptFileAsync` = `Prepare` then `Commit` with no override, and `Commit`'s date resolves
  `confirmedDate ?? OcrDate ?? FallbackDate` — identical to the old inline `IsIsoDate(ocr) ? ocr :
  InferDate(file)`. The 7 pre-existing ingest tests are the regression net and stayed green untouched.
- **Date override is one field.** `ReceiptsRepo.IngestReceipt` stamps `r.PurchaseDate` on both the receipt
  row and every price row, so `prepared.Ingest with { PurchaseDate = date }` reaches both. No per-line date.
- **"Never default to today" is enforced in Core, not just the UI.** `ImportBatchAsync` only calls `Commit`
  when the resolver returns a non-null date; a null result is a `Skipped` item (no write). There is no
  today-fallback path in the batch. The dialog's Confirm-disabled-until-dated is the UI belt on top. The
  legacy single-scan path keeps its `FallbackDate` (mtime→today) — a fresh scan's mtime is fine; only the
  backfill of old paper is poisoned by a today default.
- **Alert suppression — scope finding.** The v1 code has **no ingest-time alert hook**: `PriceDropAlertService`
  is invoked only on-demand from Savings.razor, and `ScanRecentReceipts` is windowed to the last 21 days.
  So "suppress alerts during backfill" is delivered by (a) the batch never scanning alerts and (b) correct
  purchase dates keeping old rows outside the 21-day window and out of "current price". A test
  (`Backfilled_old_receipts_do_not_fire_the_recent_scan_but_a_fresh_one_does`) pins this: 4 receipts dated
  40–55 days ago fire 0, a fresh 20%-under receipt fires ≥1. Wiring scan-on-ingest + local notifications
  (v1 Q8, `Plugin.LocalNotification`, never built) stays a later phase.
- **Batch runs sequentially on the UI thread** (not `Task.Run`): the date resolver must show a MudDialog,
  which needs the UI sync context. OCR is async I/O (doesn't block); `Commit` is a sync SQLite write but
  runs between multi-second human date confirms, so UI-thread time is negligible. `IProgress<int>` reports
  files-done for the "N / total" caption.
- **Retention:** the batch copies all picks up front, then keeps the copied image only for `Imported`
  outcomes — duplicates/skips/failures/cancellations get their copy deleted (mirrors the single-import
  orphan cleanup). Cancel-mid-batch: the per-receipt transaction means committed receipts stay, nothing
  partial; remaining files are marked `Cancelled` and their copies dropped.
- **`ReceiptPrepared.Duplicate`** carries the short-circuit outcome when file/signature dedupe decides
  before the user is asked anything (`Ingest == null`), so the batch records Duplicate without a date prompt.

## v2 Phase 2 — Item-manager + alias correction

`ItemsAdminRepo` (search / rename / merge / correct) + `/items` route + a per-line Fix action on the
receipt browser, sharing a reusable `ItemPickerDialog`. 305 tests, 0 skipped; Windows head builds 0/0.

- **FK enumeration was verified against the live schema, and it corrected two source docs.** Seven tables
  carry `item_id`: prices, receipt_line_items, shopping_list, item_aliases, watchlist, **flyer_deals, and
  price_drop_alerts**. The Python `_ITEM_ID_TABLES` lists only five (adds flyer_deals but omits
  price_drop_alerts and watchlist); V2_PLAN listed five (omitted flyer_deals and price_drop_alerts). The
  test's `ItemIdTables` array is the guard — a new item_id table that isn't added to both the repo and the
  test will show up as a non-zero orphan count in the FK-sweep test.
- **No table has a UNIQUE(item_id)**, so the Python merge's SAVEPOINT-per-table + `ON CONFLICT`→delete dance
  is unnecessary here — reference moves are plain `UPDATE … SET item_id`. The one real duplication risk is
  `watchlist` (no UNIQUE, so a blind UPDATE leaves two active watches for the target); handled explicitly:
  `DELETE source watch WHERE target already has one, else UPDATE`. Documented with a ponytail note; any new
  item_id table with a UNIQUE constraint would need the collision handling reconsidered.
- **Merge and correction take a required `SqliteTransaction`** (like `ReceiptsRepo.IngestReceipt`) — both
  touch several tables and must be all-or-nothing. The atomic-rollback test proves it by calling `MergeItems`
  then `tx.Rollback()` and asserting every table reverted. Read-only `SearchItems` and single-statement
  `RenameItem` keep the optional-tx convention.
- **`CorrectLineMapping` keys the price row by `(receipt_id, raw_name, old item_id)`** — `raw_name` is the
  original line description (`ReceiptsRepo.IngestReceipt` binds `$raw = li.Description`), so it re-points
  exactly the price row this line produced. An unmapped line (old item_id null) has no price row; the line +
  alias are still fixed. No retro-sweep across other receipts — that is deliberately merge's job.
- **No `ensure_schema`** — `items.is_tracked`/`default_unit` are in the migration ledger, so the Python
  runtime-ALTER is gone (same call the Phase-3 notes make).
- **`ItemsAdminRepo` is a static class** (repo convention); no DI registration — pages call it via the
  injected `SqliteConnectionFactory`. The picker/merge confirm uses MudBlazor **9.6's `ShowMessageBoxAsync`**
  (the sync-named `ShowMessageBox` was removed).

## v2 Phase 4 — Meal planning

`RecipeEngine` + `MealSuggestionService` + `WeeklyPlannerService` ported, `/meals` route, meal-profile
returned to Preferences. All 4 Python planning suites ported (59 tests). 364 total, 0 skipped; Windows 0/0.

- **recipes.json (62) is an EmbeddedResource in Core**, loaded via the assembly manifest stream, so
  `RecipeEngine` has no MAUI/file dependency and is testable in the plain xUnit host. Tests point the engine
  at a file fixture (`Fixtures/recipes_sample.json`, 8 recipes) instead. Parse via a source-gen
  `RecipeJsonContext` (AOT rule). Accepts a bare list or `{"recipes":[…]}`; a bare string throws
  (`InvalidDataException`) rather than becoming one bogus recipe.
- **Three deliberate deviations from the Python (each a fix, not a regression):**
  1. *Injected engine is used.* Python exposed module-level load/filter/get delegating to a hidden singleton,
     and `MealSuggestionService` accidentally used that singleton instead of its injected `recipe_engine`.
     C# has no module singleton — the engine is injected and used. The Python "injected engine is ignored"
     test is inverted to assert the injected engine drives results.
  2. *Dropped Python-only quirk tests:* `str()`-coercion of non-string ingredients (C# is typed, and the
     catalog test guarantees valid strings) and the module-singleton-delegation tests.
  3. *Null profile = empty profile in the service*, not a hidden `get_meal_profile()` read. The service stays
     free of the config layer (testable); the `/meals` route resolves the real profile via an injected
     `Func<MealProfile>` provider wired in DI to `PreferencesService.GetMealProfile`.
- **`PreferencesService.GetMealProfile` was rebuilt** (v1 removed it). Single-profile: `allergies` = hard
  excludes; `no_<protein>` restrictions + `avoid_meats` from hard-excluded proteins; `prefer_meats` from
  protein weights > 1.0; `favorite_tags` from favorite cuisines — all read from the one master-member
  profile via `ComputeEffectivePreferences`. Preferences UI writes `preferred_protein_weights` as
  `{protein: 2.0}` (any weight > 1 = preferred), plus `excluded_proteins` / `favorite_cuisines`.
- **Stable sort matters:** Python's `sort` is stable, so equal-score recipes keep catalog order. `List.Sort`
  is not stable — used `OrderByDescending` (LINQ, stable) in both the engine filter and the suggestion rank.
- **Deals price column** is read with `CAST(COALESCE(unit_price, norm_unit_price, deal_total) AS REAL)` so
  the TEXT money columns and the REAL norm column all come back as a double; no per-row string parsing.
- **`/meals` "Add to list"** rebuilds the plan with `persistToShoppingList: true` rather than persisting a
  cached plan — the scoring is deterministic, so the rebuilt plan equals the displayed one, and the persist
  stays inside `WeeklyPlannerService` (one transaction) instead of leaking a persist path into the UI.

## v2 Phase 5 — Family members + meal-picks

`member_requests` (migration 5) + `MemberRequestsRepo` + ConfigStore member CRUD + `FamilyRequestsService`
+ `/family` route + member management in Preferences + a nav badge. 13 tests; 377 total, 0 skipped.

- **Names-only members, single shared profile.** `ConfigStore.AddMember` creates a secondary with the
  canonical default profile, but preferences are always read from the master member
  (`ComputeEffectivePreferences` → `GetMasterMember`), so secondary profiles are inert. `DeleteMember`
  refuses the master and the last-remaining member and resets `active` to primary if the active member was
  removed. No shopping-list migration was needed — `added_by`/`added_by_member_id` shipped in v1.
- **`member_requests` has no member FK** (members live in config JSON, not a DB table — matches Python).
  `item_row_ids` is a JSON array of the shopping_list ids the pick created; decoded with `JsonDocument`
  (AOT-safe) tolerating NULL/junk → `[]` rather than crashing the review screen.
- **Flow ported verbatim (the crown-jewel is the safety net):** `PickableRecipes` runs every recipe through
  the hard-profile filter (household allergens/hard-excludes) so a secondary can never pick an allergen
  recipe; a review request is created **only for a secondary** picker; `RemoveRequest` soft-deletes exactly
  the created rows then marks reviewed. Picks are not wrapped in one transaction (items added per-call, then
  the request row) — matches Python; a partial failure leaves attributed items on the list without a review
  row, which is recoverable, not corrupting.
- **Family nav badge is best-effort**, refreshed on `NavigationManager.LocationChanged` and gated on
  `Startup.Status == Ready`, errors swallowed (a stale/absent badge must never break the shell). No live
  event from the service — the `/family` page itself always shows the accurate count.
- **`Family.razor` injects the service as `Picks`, not `Family`** — a Razor component can't have a member
  named after its own generated type (`CS0542`).
- **Process note:** one commit briefly went red because `dotnet test | tail` in an `&&` chain masks the test
  exit code (the pipe returns `tail`'s status). It was amended clean immediately. Run `dotnet test` as its
  own command before committing, or check `$?` explicitly — never gate a commit on a piped test run.
