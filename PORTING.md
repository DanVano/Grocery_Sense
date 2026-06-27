# Grocery Sense — Python → C# Porting Playbook

Step-by-step, ordered, checkbox guide for filling in the scaffold.

**Honest state of the scaffold:** every C# service/repo is a stub that
`throw new NotImplementedException("Port from reference-python/...")`. But the stubs only cover the
**primary public surface** — they are deliberately **incomplete**. Some Python APIs have no stub yet
(see Phase 0.B Contract Audit for known gaps). Do not assume "no stub = not needed."

- **Full I/O inventory + rationale:** `reference-python/ARCHITECTURE.md`.
- **Port service-layer inward**, not screen-by-screen. The money math is the product; UI is thin.
- **Never commit red.** Within a task: add test → implement → green → commit. Red exists only *inside*
  one task, never at a commit or phase boundary. Every phase ends with `dotnet test` green.
- **Rule of one:** finish + verify one task before the next.

Paths are relative to repo root (`Grocery_Sense_Main/`). C# solution lives in `Grocery_Sense/`.

---

## v1 Product Decisions (locked 2026-06-24)
Source of truth + rationale: `brainstorms/2026-06-24-grocery-sense-csharp-maui-port.md`. v1 = personal/household
tool shared to a friends-&-family **Android** test ring; public store product is **v2**.

- **In v1:** receipt scan → price intelligence → smart shopping list; **deal alerts**; multi-store **trip
  optimizer (redesigned — see below)**; **preferences** (single-profile allergies/excludes/oils) feeding the
  deal filter; **budget tracking**; **store management + postal** setup; **unit normalization** (core).
- **Platform:** Android only (Windows dev, no Mac). **Distribution:** raw APK sideload; Azure key embedded +
  **budget cap**; rotate if leaked; OCR proxy is the gate before v2.
- **UI:** **MudBlazor**, touch-first screens (Receipts, List, Deals, Plan, Preferences, Budget) — greenfield,
  do NOT clone the Tk windows.
- **Data:** **clean start.** 6 months of **paper** receipts scanned via the C# Azure-OCR pipeline — **no
  Python→C# migration.** Requires **bulk/multi-image scan** + reliable **printed-date extraction (+ manual
  fallback)**; **suppress alerts during the bulk backfill**.
- **Alerts:** on-device compute → in-app feed **+ local notification** (no backend, no background poll).
- **Flyers:** **manual flyer-photo import** via Azure layout OCR; Flipp stays a stub; behind `IFlyerProvider`.
- **Locale:** CAD / en-CA / Canadian postal; no i18n. **No accounts** (local-only per device).

**Optimizer redesign — NOT a port of `basket_optimizer_service` (full spec in Phase 4, grilled 2026-06-25):**
drop the `distance_km × gas_cost_per_km` trip penalty entirely (user decides travel; cut those inputs). Goal =
**fewest stores that still capture meaningful savings** via a **hybrid gate** — an item wants another store only
if it's **≥10% cheaper** there, and a store joins the plan only if its qualifying items save **≥$5** combined;
greedy up to **`maxStores` (default 3)**, with a per-run **"Fewest stops" vs "Best savings"** toggle. The three
thresholds (`maxStores` / `minItemSavingPct` / `minStoreSaving`) are **user settings**; defaults (3 / 10% / $5)
are **starting points to tune on real data after the 6-month backfill**. New logic (no reference impl) → owns a
spec + **8 golden tests** in Phase 4. **Build is gated on Phases 2–3** (data + price layer) — not standalone.
**Shopping time is a first-class value.**

**Deferred to v2:** meal planning (RecipeEngine/MealSuggestion/WeeklyPlanner/`recipes.json`); multi-member
households (master/secondary merge, consensus, star annotations, family meal-picks, member switching) +
cross-device sync; real flyer provider; OCR backend proxy; proactive push (FCM/APNs); iOS.

---

## Conventions — read once, apply everywhere

### C# type rules
| Concept | C# type | SQLite storage |
|---|---|---|
| Money (prices, totals, budgets) | `decimal` | **TEXT** (Microsoft.Data.Sqlite round-trips `decimal` losslessly as TEXT). **Never REAL** — `double` loses cents. |
| Confidence, distance_km, scores/weights | `double` | REAL |
| Dates (purchase_date, valid_from/to) | `DateOnly` | TEXT `yyyy-MM-dd` (matches Python strings → fixture parity) |
| Timestamps (created_at, last_seen) | `DateTimeOffset` | TEXT ISO-8601 round-trip (`"o"`) |
| IDs, counts, flags | `int` / `bool` | INTEGER (bool as 0/1) |
| Return shapes | typed `record` | — |
| **Raw** OCR / provider JSON only | `Dictionary<string, object?>` / `JsonElement` | TEXT |

- The scaffold's `Dictionary<string, object?>` return types (receipts rows, stats, plan/audit/budget
  results) are **placeholders** — replace each with a typed `record` as you port that method. Keep dicts
  only where the payload is genuinely raw Azure/Flipp JSON.

### SQLite rules
- Set `PRAGMA encoding = 'UTF-8'` when the DB is first created; UTF-8 everywhere.
- **Rounding:** keep full precision for intermediate unit-price math; round only at the write/display
  boundary — currency totals to 2 dp, normalized unit prices to 4 dp, `MidpointRounding.AwayFromZero`.
  Document the chosen mode once in `SqliteConnectionFactory`/a `Money` helper and reuse it.
- Pragmas (port from `connection.py`): `foreign_keys=ON`, `journal_mode=WAL`, `synchronous=NORMAL`,
  `busy_timeout=5000`; `integrity_check` once per DB path.

### Atomicity & transactions (load-bearing)
Wrap in a **single transaction with rollback on failure**:
- Receipt ingestion · flyer ingestion
- Item merge (re-points many FK tables)
- Optimizer plan write-back to the list
- **Every migration step**

Required tests: **"no partial rows after failure"** (inject a mid-write throw → assert zero orphan rows)
and **migration idempotence** (run migrations twice → identical schema + rows).

---

## Phase 0 — Setup, runtime upgrade, contract audit, structural fixes  ☐

### 0.A Upgrade to .NET 10 / MAUI 10 — mostly done, one elevated step left
.NET MAUI **9 support ended 2026-05-12**. The scaffold is now on **.NET 10**.

- [x] .NET 10 SDK installed (`10.0.301`).
- [x] `global.json` at repo root pins the SDK:
      ```json
      { "sdk": { "version": "10.0.301", "rollForward": "latestFeature" } }
      ```
- [x] TFMs retargeted: class libs `net10.0`; App heads `net10.0-android/ios/maccatalyst/windows`.
- [x] Bumped `SQLitePCLRaw.bundle_e_sqlite3` → `3.0.3` in Data — clears the **NU1903 HIGH** advisory
      that Microsoft.Data.Sqlite pulled in transitively (lib.e_sqlite3 2.1.11).
- [x] Verified: class libs + Tests build & pass on net10 (`dotnet test` green).
- [ ] **Run ELEVATED** (CLI workload install needs admin — it cancels under a non-elevated shell):
      `dotnet workload restore Grocery_Sense/GrocerySense.sln` (or `dotnet workload install maui`). The
      VS-installed `android`/`maui-windows` workloads do **not** satisfy the net10 MAUI build, which wants
      the full `maui` set (maui-android/ios/maccatalyst/tizen). **The App head won't build until this runs.**
      Verify after: `dotnet build Grocery_Sense/GrocerySense.App/GrocerySense.App.csproj -f net10.0-windows10.0.19041.0`.

### 0.B Contract audit — ✅ DONE (see `CONTRACT_AUDIT.md`, 2026-06-26)
The scaffold is incomplete. Build the coverage source-of-truth so nothing is silently dropped.
Result: full Port/Replace/Defer ledger — ~70 Port, ~12 Replace, ~40 Defer; all 22 `prices_repo` functions
enumerated (the batch readers the scaffold omitted). Phase 2 can proceed without method-discovery churn.

- [x] Create `CONTRACT_AUDIT.md`: one row per **public** Python API (every function/method in
      `reference-python/src/Grocery_Sense/{data,services,integrations,config,recipes}`):
      `Python API | Status (Port / Replace / Defer) | C# target | Done?`
- [ ] Seed it with the **known gaps already in the scaffold**:
  - `PricesRepo`: missing **most batch readers** — `get_most_recent_prices_by_store_batch`,
    `get_most_recent_prices_global_batch`, `get_active_flyer_prices_batch`, `get_usual_unit_price_batch`,
    `get_six_month_low_batch`, `get_recent_avg_unit_price_*_batch`, `get_purchase_cadence_batch`,
    `get_price_stats_batch`, `get_last_seen_at_or_below*`. → **Port** (Planning/Optimizer depend on them).
  - `StoresRepo`: missing `UpdateStoreAddress` (Port), `DeleteStore` (Defer — test-only in Python).
  - `DealsService`: missing `collect_favorite_ingredients`, `rank_recipes_by_deals`,
    `suggest_stores_for_term`. → Port/Defer per the v1 Deals route.
  - Basket result records are marked **"trimmed"** — `BasketItemPlan`, full `PricePick`/`StorePlan`
    fields. → Port when implementing the optimizer.
  - Other omitted helpers: `ShoppingListRepo` bulk variants, `ReceiptsRepo.list_deleted_backups` /
    restore-conflict shape, etc. → audit them all.

### 0.C Fix dependency direction (interfaces) — do before porting logic  ✅ DONE (2026-06-26)
Current scaffold has `Core → Integrations` (concrete `AzureReceiptOcrClient`), which makes canned-OCR
tests awkward and inverts the testable direction.

- [x] Define `IReceiptOcrClient` and `IFlyerProvider` in **Core** (`GrocerySense.Core/Abstractions/`).
- [x] Make **Integrations reference Core** and implement them
      (`AzureReceiptOcrClient : IReceiptOcrClient`, `FlippClient : IFlyerProvider`).
- [x] **Remove the `Core → Integrations` project reference.** `ReceiptIngestionService` depends on
      `IReceiptOcrClient`, never the concrete class.
- [x] App composition root binds concrete → interface (`IReceiptOcrClient`→`AzureReceiptOcrClient`,
      `IFlyerProvider`→`FlippClient`). Cred supply still TODO with the Phase-5 client build.
- [x] New graph: `Data→none`, `Core→Data`, `Integrations→Core`, `App`/`Tests→all`.
      **Deviation:** Integrations references **Core only**, not `Core+Data` — the integration clients
      are pure API-in/JSON-out (no DB), so a Data ref would be dead. Add Data if that ever changes.

> Payoff: Core unit-tests run against a fake `IReceiptOcrClient` returning canned JSON — no Azure, no network.

### 0.D Creds  ☐ (store wired; ctor-read deferred to Phase 5)
- [x] `dotnet user-secrets init` on `GrocerySense.Integrations` (UserSecretsId in csproj). Nothing
      hardcoded — `AzureReceiptOcrClient` ctor already takes endpoint/apiKey params (default null).
- [ ] Read creds in the OCR client ctor — **deferred to Phase 5**: the client is a throwing stub with
      no `DocumentIntelligenceClient` construction yet, so config wiring now would be dead code.

**Done when:** net10 build + test green; `CONTRACT_AUDIT.md` exists and is seeded; interfaces in place;
DI still resolves.

---

## Phase 1 — Test harness & fixtures (stays GREEN)  ☐
Set up the machinery the later phases turn red→green *within their own tasks*. This phase does **not**
leave anything red.

- [ ] Build a **Python↔C# fixture parity** harness: export canonical input→expected cases from
      `reference-python/tests/price_intelligence/test_unit_normalization.py`,
      `test_multibuy_parser.py`, and `tests/ingestion/test_ingredient_mapping.py` (incl. the
      `alias_ambiguity` fixtures) into JSON under `GrocerySense.Tests/Fixtures/`.
- [ ] Write the xUnit `[Theory]` loaders. Mark the math assertions `[Fact(Skip="impl in Phase 3")]` (so
      the suite is green) — the skip is removed *inside* the implementing task.
- [ ] **DI resolution smoke test:** build the provider from `AddGrocerySenseServices`, resolve every
      registered service, assert non-null. Catches graph/wiring breaks immediately.

**Done when:** fixtures load, DI smoke test passes, `dotnet test` green.

---

## Phase 2 — Data foundation + ConfigStore  ☐
ConfigStore is here (it precedes `PreferencesService` in Phase 3 — Python depends on it).

| Port FROM | Port INTO |
|---|---|
| `data/connection.py` | `GrocerySense.Data/SqliteConnectionFactory.cs` |
| `data/schema.py` | `GrocerySense.Data/Database.cs` |
| `data/repositories/*.py` (9) | `GrocerySense.Data/Repositories/*.cs` |
| `config/config_store.py` | `GrocerySense.Core/Services/ConfigStore.cs` |

- [ ] Connection factory: pragmas + UTF-8 encoding + per-path integrity check.
- [ ] `Database`: convert `_migrate` into a **numbered migration ledger** (`schema_version` + ordered
      steps); fold in the feature-local DDL Python self-creates (flyers, unit-norm columns, receipt
      dedupe). Each migration in a transaction; idempotent.
- [ ] Repos least→most dependent (v1 set): `Stores`, `Items`, `ItemAliases`, `ShoppingList`,
      `Receipts`, `Flyers`, `Prices`. Port the **batch readers from the contract audit** (not optional).
      **Skip in v1:** `MemberRequests` (family picks → v2) and `ItemsAdmin` merge/rename (→ v2; `ItemAliases`
      itself IS v1 — fuzzy mapping needs it).
- [ ] Keep `FlyersRepo` CRUD-only — move its preference filtering into `PreferencesService` (Phase 3).
- [ ] **ConfigStore v1: retain JSON behavior** (atomic temp→flush→replace, mtime cache, invalidate
      preferences cache on save). **Defer** moving household/preferences into SQLite until device-sync
      exists — that migration only pays off with sync.

**Verify:** temp-file SQLite migration test (old-shape → migrate → rows preserved) **+ idempotence** (run
twice) + a repo round-trip test.

---

## Phase 3 — Price math + preferences (red→green per task)  ☐

| Port FROM | Port INTO |
|---|---|
| `services/unit_normalization_service.py` | `.../UnitNormalizationService.cs` |
| `services/multibuy_deal_service.py` | `.../MultiBuyDealService.cs` |
| `services/ingredient_mapping_service.py` | `.../IngredientMappingService.cs` |
| `services/price_history_service.py` | `.../PriceHistoryService.cs` |
| `services/preferences_service.py` | `.../PreferencesService.cs` + `EffectivePreferences.cs` |

- [ ] UnitNorm + MultiBuy: implement until the Phase-1 fixtures go green (remove their `Skip`).
- [ ] **IngredientMapping / FuzzySharp scoring:** `TokenSortRatio` returns **0–100**; Python thresholds
      are **0.78 / 0.90**. Either divide the score by 100 or compare against **78 / 90** — pick one,
      document it at the call site. Keep `FlushLearnedAliases` deferred-write batching. Port the
      ingredient-mapping + `alias_ambiguity` tests.
- [ ] `PreferencesService` — **v1 is single-profile** (see locked decisions). **No multi-member merge.**
      Read ONE profile's allergies + hard/soft excludes + oils for the deal filter. Skip master/secondary,
      consensus/strong-soft, star annotations, member switching (all v2). Keep the profile shape
      forward-compatible (could become the v2 "master member") so multi-member needs no migration.

**Verify:** unit-norm/multibuy/ingredient-mapping fixtures green + preference-merge test.

---

## Phase 4 — Planning (red→green)  ☐

| Port FROM | Port INTO |
|---|---|
| `services/shopping_list_service.py` | `.../ShoppingListService.cs` |
| `services/planning_service.py` | `.../PlanningService.cs` |
| `services/basket_optimizer_service.py` | `.../BasketOptimizerService.cs` (+ full result records) |
| `services/price_drop_alert_service.py` | `.../PriceDropAlertService.cs` |
| `services/deals_service.py` | `.../DealsService.cs` (only what the v1 Deals route needs) |

- [ ] Confirm `PricesRepo` batch readers are done (hard dependency).
- [ ] **BasketOptimizer = REDESIGN, not a port — full spec grilled 2026-06-25.** **Drop**
      `distance_km × gas_cost_per_km` and cut those inputs (user decides travel). Algorithm:
  - **Primary store** = cheapest single store for the basket (keep single-store scoring + favorite/priority tie-break).
  - **Hybrid add-a-store gate (BOTH required):** an item "wants" another store only if it's **≥ `minItemSavingPct`
    (default 10%)** cheaper there than the current plan's best price for it; a store joins the plan only if its
    qualifying items save **≥ `minStoreSaving` (default $5)** combined. Greedy: add the best qualifying store,
    repeat until none qualifies or **`maxStores` (default 3)** reached. Must handle **1..N** stores (current code only 1/2).
  - **Trip-mode toggle:** "Fewest stops" → force single store (gate ignored); "Best savings" → hybrid up to maxStores.
  - **3 user settings** (single-profile config): `maxStores`=3, `minItemSavingPct`=10%, `minStoreSaving`=$5.
  - **Unknown price:** assign to primary, flag "price unknown", exclude from total + partial-estimate note; cannot
    pull a store; **never fabricate** a fill-in price.
  - **Excludes:** hard/allergy → **pulled OUT** of the plan (safety net via `PhraseSafeHit`), surfaced separately;
    **soft = Deal-feed-only, NO optimizer effect** (drop the member-aware star machinery — single profile).
  - Keep flyer→store-history→global price pick; savings lines vs usual-avg(180d) + lowest(180d). Fill the full
    `BasketItemPlan`/`PricePick`/`StorePlan` records. **Plan write-back in a transaction.**
  - Reference for KEEP-parts only: `reference-python/.../basket_optimizer_service.py` — do NOT port `_compute_trip_penalty`.
- [ ] DealsService: defer `rank_recipes_by_deals` / `suggest_stores_for_term` unless a v1 route uses them.
- [ ] **BudgetService is v1** (`services/budget_service.py` → `Core/Services/BudgetService.cs`): month
      spend vs budget + trend. The gas-cost field is now unused (optimizer redesign) — don't surface it.

**Verify:** optimizer **golden tests — 8 spec-driven cases**: (1) 5 items/5 stores all <X% apart → 1 store;
(2) 2 items ≥X% cheaper at B saving ≥$Y → 2 stores; (3) lone item ≥X% cheaper but <$Y → stays at primary;
(4) 4 stores qualify, maxStores=3 → top-3; (5) Fewest-stops → 1 store; (6) unknown price → primary+flagged,
excluded from total, pulls no store; (7) allergen/hard-exclude → pulled OUT; (8) maxStores=1 ≡ fewest-stops.
Plus planning tests (mirror `tests/planning/`) + no-partial-rows test on plan write-back.

---

## Phase 5 — Receipt ingest (interface-based)  ☐

| Port FROM | Port INTO |
|---|---|
| `integrations/azure_docint_client.py` (API half) | `Integrations/AzureReceiptOcrClient.cs : IReceiptOcrClient` |
| `integrations/azure_docint_client.py` (DB half) | `Core/Services/ReceiptIngestionService.cs` (depends on `IReceiptOcrClient`) |

- [ ] OCR client: prebuilt-receipt call → raw JSON. **No DB writes.**
- [ ] `ReceiptIngestionService`: file-hash dedupe (before any API call) → OCR → signature dedupe →
      unit-norm + multibuy → write stores/items/receipts/line_items/prices. **Whole ingest in one
      transaction; failure leaves zero partial rows.** `replaceExisting` = only delete+re-ingest path.
- [ ] **Mobile:** accept an image **stream** (or copy the picked file to a temp path first); do **not**
      depend on a persistent picker path that the OS may revoke.
- [ ] Tests: canned `IReceiptOcrClient` fake (no network) → dedupe tests + **no-partial-rows-on-failure** test.

---

## Phase 6 — Flyer pipeline (interface-based)  ☐

| Port FROM | Port INTO |
|---|---|
| `integrations/flyer_docint_client.py` | `Integrations/FlyerDocIntClient.cs` |
| `integrations/flipp_client.py` | `Integrations/FlippClient.cs : IFlyerProvider` (stub, empty) |
| `services/flyer_ingest_service.py` | `Core/Services/FlyerIngestService.cs` |
| `services/flyer_sync_service.py` + `flyer_sync_scheduler.py` | `Core/Services/FlyerSyncService.cs` + `FlyerSyncScheduler.cs` |

- [ ] `FlippClient` stays empty — **don't fabricate deals.** Behind `IFlyerProvider` so a real provider
      drops in later.
- [ ] Flyer ingestion in a transaction.
- [ ] Mobile: replace the `threading.Timer` scheduler with **sync-on-resume / manual button**
      (iOS/Android restrict background execution); hook app lifecycle in the App project.

---

## Phase 7 — Recipes & meal planning — DEFERRED TO v2 (do NOT build in v1)  ☐

> Locked decision (2026-06-24): meal planning is **v2**. Section kept as the v2 spec; **skip in v1**.

| Port FROM | Port INTO |
|---|---|
| `recipes/recipe_engine.py` | `Core/Services/RecipeEngine.cs` |
| `services/meal_suggestion_service.py` | `Core/Services/MealSuggestionService.cs` |
| `services/weekly_planner_service.py` | `Core/Services/WeeklyPlannerService.cs` |
| `recipes/recipes.json` | **EmbeddedResource in `GrocerySense.Core`** (load via assembly manifest stream) |

- [ ] Embed `recipes.json` in **Core**, not as a MAUI asset — keeps `RecipeEngine` free of any MAUI
      dependency (so it's testable in the plain xUnit host).
- [ ] Port the full meal flow: RecipeEngine filter → MealSuggestion scoring → WeeklyPlanner aggregation +
      ingredient mapping.
- [ ] Port tests: `test_recipe_engine.py`, `test_recipes_catalog.py`, `test_meal_suggestion.py`,
      `test_weekly_planner.py`.

**Verify:** all four recipe/meal tests green.

---

## Phase 8 — UI: 6 Blazor routes (NOT all 16 windows)  ☐

| Route | Replaces (reference Tk windows) | Drives |
|---|---|---|
| Receipts | `receipt_import_window.py`, `receipt_browser_window.py` | `ReceiptIngestionService`, `ReceiptsRepo` |
| Shopping List | inline list in `tk_main.py` | `ShoppingListService` |
| Deals | `deal_feed_window.py` | `FlyersRepo.ListActiveDeals` + `PreferencesService` |
| Plan | `store_plan_window.py`, `basket_optimizer_window.py` | `PlanningService`, `BasketOptimizerService` |
| Preferences | `preference_window.py` (+ wizard) | `ConfigStore`, `PreferencesService` (single-profile) |
| Budget | `budget_window.py` | `BudgetService` |
| _(setup)_ Stores | `stores_management_window.py`, `store_settings_window.py` | `StoresRepo` (postal + shop-here; NO distance/gas) |

- [ ] Replace the template's Home/Counter/Weather pages with these routes; bind via DI.
- [ ] Async/`await` + `InvokeAsync` for long work — never block the UI thread.

### Mobile requirements (apply across the UI/ingest paths)
- [ ] Receipt input as **streams**; copy temp files before processing; no reliance on persistent picker paths.
- [ ] **Startup state machine**: DB initialize → loading / ready / **error** states surfaced in the UI;
      don't block the first frame on DB init.
- [ ] **Cancellation + progress** on ingest/sync (`CancellationToken` + `IProgress<T>`).
- [ ] **Retention policy** for receipt images + raw OCR JSON: define when they're deleted and give the
      user control; don't hoard PII forever.
- [ ] **Never log secrets or full receipt data** — redact PII and keys in all logs.

---

## Phase 9 — Platform glue & release readiness  ☐
- [ ] `MediaPicker` / `FilePicker` (stream-based) for receipt capture.
- [ ] `SecureStorage` for any per-user token.
- [ ] **Before public release** (deferred for now): route OCR through a backend proxy you control
      (app → your endpoint → Azure) + per-user rate limiting — a shipped app can't safely hold a shared key.
- [ ] Apple heads (`net10.0-ios`, `net10.0-maccatalyst`) need a Mac/CI host. Android + Windows build locally.

---

## v1 scope — explicit DEFER list
**v1 = Receipts, List, Deals, Plan, Preferences, Budget** + their deps + store management/postal setup +
unit normalization. **Defer** (leave stubbed; exclude from DI):
- **Meal planning:** `RecipeEngine`, `MealSuggestionService`, `WeeklyPlannerService`, `recipes.json` (Phase 7 → v2).
- **Multi-member households:** member merge/consensus/star annotations, member switching, `FamilyRequestsService`
  + `MemberRequestsRepo` (→ v2).
- `ItemsAdminRepo` / full item-manager (catalog merge/rename) → v2. *(A minimal alias-correction UX may be
  pulled into v1 to de-risk fuzzy mapping — see Phase 3.)*
- `DemoSeedService`, `DbMaintenanceService`, `ListAuditService`.

**Now v1 (NOT deferred):** `BudgetService`; store management + postal/shop-here. **Cut entirely:**
`distance_km` / `gas_cost_per_km` (optimizer redesign — user decides travel).
Revisit deferred items only when a concrete route needs them.

---

## Verification — every phase ends GREEN
Gate each phase on the relevant checks:
- [ ] **Python/C# fixture parity** test passes (where math exists)
- [ ] **Temp-file SQLite migration** test + **idempotence** test
- [ ] **DI resolution smoke** test (resolve all registered services)
- [ ] **No-partial-rows-after-failure** test for each transactional write
- [ ] App builds on **at least one head** (Windows or Android)
- [ ] grep shows **no `NotImplementedException`** left in files claimed done for the phase
- [ ] `dotnet test Grocery_Sense/GrocerySense.Tests/GrocerySense.Tests.csproj` green

**Per-task definition of done:** add test → implement → green → commit. Red only mid-task, never at a
commit or phase boundary.
