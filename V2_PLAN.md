# Grocery Sense — v2 Implementation Plan (Family Release)

> **Status 2026-07-11: all v2 feature code (Phases 1/2/4/5/6) is DONE.** What's still open lives in
> `V2_FOLLOWUPS.md` — Phase 0's toolchain/keystore hand-offs (§1 there), Phase 3 tuning (blocked on the
> physical backfill), and the Phase-6 release step. This file is the phase record + the Phase 0 command
> reference; don't re-plan work from it.

Scope grilled + locked 2026-07-02: `brainstorms/2026-07-02-grocery-sense-v2-plan.md` (Q&A + rationale).
Companion docs: `PORTING.md` (v1 playbook + conventions — **all v1 conventions still apply**),
`CONTRACT_AUDIT.md` (API ledger for the deferred surface), `archive/BACKLOG.md` (v1-era backlog, superseded by this doc).

**v2 = family/household feature release to the existing friends-&-family Android ring.**
Same trust model as v1: sideload APK, local-only, no backend, no accounts.

## v2 Product Decisions (locked 2026-07-02)

- **In v2:** bulk receipt backfill on-ramp (multi-image import + date confirm + alert suppression);
  item-manager (merge/rename) + manual alias-correction UX; real-data tuning pass (fuzzy/optimizer/alert
  thresholds); meal planning (straight Phase-7 port, 62-recipe catalog); family members (**names only**)
  + meal-picks → parent review; DB backup/export via share sheet; sideload release.
- **Members = names only.** Household preferences stay a single profile (v1 finding still true: no
  differing allergies/needs). Members are lightweight identities (id, name, master/secondary role) so
  meal-picks attribute correctly on the one shared phone.
- **Explicitly OUT (v3 parking lot):** accounts/auth · multi-device sync · OCR backend proxy + rate
  limiting · real flyer provider (ToS/legal) · proactive push (FCM/APNs) · iOS/Apple heads · auto-update
  channel · starter dataset / central price DB · custom-recipe entry · per-member preference profiles
  (merge/consensus/star machinery) · list-audit · demo-seed · `deals_service` search path ·
  `planning_service` cost-view.
- **Baseline:** verify + commit `feat/family-savings`, merge → `main`, tag `v1`. v2 phase branches cut
  from `main`. The uncommitted stub deletions are intentional — commit them; v2 features are built fresh
  from `reference-python/`, not by filling stubs. Stale `v3-expanding-core-features-*` branches: delete.
- **Sequencing rationale:** data before intelligence. The 6-month backfill is what makes alerts,
  optimizer, savings, and budget history real; correction tooling must exist *while* scanning, not after.
  The physical scan-in session happens after Phase 2, before Phase 3.

Confidence shown per phase = implementation confidence after the grill (target ≥95%). The residual risk
is named so it can be watched, not discovered.

---

## Phase 0 — Baseline hygiene & release plumbing  ◑   (confidence: 97%)

No new features. Make `main` real, make Android build, make signing durable.
**Status 2026-07-02:** git baseline + savings/Windows verification DONE; three items handed off (need
elevated install / secrets / external portal).

- [x] **Verify the savings feature** (grill Q9). Service math verified: **292 tests green, 0 skipped** —
      watchlist (4 tests: dedupe, target hit, below-usual, no-hit), budget forecast (5: status grades +
      projection with/without budget + grading + clear), wait-for-sale (2: unplanned-when-not-on-sale,
      planned-when-on-sale) + priority selection. **Windows head builds clean (0 warn / 0 err)** — Savings
      UI compiles, DI resolves. _Remaining:_ on-device click-through (needs a running device; deferred to
      after the Android build works).
- [x] Stub deletions committed (already on the branch: `d745969` Tizen head, `458e3ab` services/repos).
- [x] Git baseline (local, **not pushed** — origin push is the user's call): `main` created at the tip
      `458e3ab`; annotated tag **`v1` at `84e1e21`** (last pre-savings commit — v1 = as shipped to F&F,
      savings + cleanup start the v2 line). `v3-expanding-core-features-phase1/2` deleted.
      **origin/main was an empty disjoint orphan** (2-line README, no shared history) — adopting the
      branch as main; pushing to origin will be a force/unrelated push.
- [ ] **Android SDK — BLOCKED, needs elevated install (handed off).** Root cause is deeper than the
      recorded `XA5207`: this machine has **only JDK 8**; .NET 10 Android + sdkmanager 12 need **JDK 17**,
      and `platforms;android-36` is missing (SDK lives in Program Files → elevation to write). Fix:
      `winget install Microsoft.OpenJDK.17`, then elevated
      `sdkmanager --sdk_root="C:\Program Files (x86)\Android\android-sdk" "platforms;android-36" "build-tools;36.0.0"`,
      then `dotnet build GrocerySense.App -f net10.0-android`.
- [ ] **Signing keystore — handed off (secret, user-owned).** keytool present in the bundled JDK 8; NOT
      generated here (release-key password must be the user's, backed up off-machine).
      `keytool -genkeypair -v -keystore grocerysense-release.keystore -alias grocerysense -keyalg RSA -keysize 2048 -validity 10000`.
      Losing it → testers reinstall, local data wiped (BACKLOG flag).
- [ ] **Azure budget cap — handed off (external portal).** Sanity-check per-page OCR cost before the
      ~50–150-receipt backfill session.

**Done when:** `main` holds v1+savings, tagged ✅; Windows head builds ✅; Android head builds ⛔ (blocked
on JDK 17 + android-36); keystore safeguarded ⛔; `dotnet test` green ✅.

*Residual:* the three handed-off items are environment/secret/portal actions, not code risk. On-device
savings smoke still pending a working Android build.

---

## Phase 1 — Backfill on-ramp: bulk receipt import  ✅ DONE (2026-07-02)   (confidence: 95%)

The v2 data on-ramp. New capability, but it composes the existing, tested ingest pipeline.
Branch `V2_Features_Implementation_Phase1`; commits `6c369f1` (Core) + `e66832f` (UI). 299 tests green;
Windows head builds 0/0.

| Builds on (exists, tested) | New |
|---|---|
| `ReceiptIngestionService` (SHA-256 + signature dedupe, single-tx write, `TransactionDate` parsing) | Two-stage ingest: `Prepare` (OCR + parse → preview) / `Commit` (date override + tx write) |
| Receipts route stream-import + cancel + staged progress | Batch session UI over `FilePicker.PickMultipleAsync` |
| `PriceDropAlertService.ScanRecentReceipts` (date-windowed, on-demand) | Backfill: not scanned during import; correct dates keep old rows out of the window |

- [x] **Prepare/Commit split** (`6c369f1`): `PrepareReceiptFileAsync` = hash-dedupe → OCR → signature
      dedupe → parse to `ReceiptPrepared` (merchant, total, line count, `OcrDate` + `OcrFoundDate`).
      `CommitPreparedReceipt(prepared, confirmedDate)` = date override on receipt **and** every price row
      (both read `r.PurchaseDate`) → single-tx write. The one-shot `IngestReceiptFileAsync` recomposes them
      (`Commit` with no override = `OcrDate ?? FallbackDate`) — behavior unchanged, existing 7 tests green.
- [x] **Batch session** (`ImportBatchAsync` + `ReceiptDateDialog` + Backfill button): multi-pick →
      per-receipt date-confirm-only stop → commit. **Never defaults to today:** a null resolver result
      SKIPS the receipt (no write); the dialog disables Confirm until a date is entered when OCR found
      none. Enforced in Core (`ImportBatchAsync` only commits with an explicit date) — a test asserts it.
- [x] **Alert suppression** — *scope finding:* there is **no ingest-time alert hook** in the v1 code;
      alerts are computed **on-demand** in Savings.razor, and `ScanRecentReceipts` is windowed to the last
      21 days. So the load-bearing suppression mechanism is **correct purchase dates** (the batch never
      scans alerts, and correctly-dated old receipts fall outside the 21-day window and aren't "current").
      A test demonstrates: a batch of 40–55-day-old receipts fires 0 on `ScanRecentReceipts`, a fresh cheap
      receipt fires ≥1. Wiring scan-on-ingest + local notifications stays a later phase (v1 Q8, never built).
- [x] **Batch summary:** per-file `BatchImportStatus` (Imported / DuplicateFile / DuplicateSignature /
      Skipped / Failed / Cancelled); `BatchImportSummary` counts. Retention keeps the copied image only for
      Imported receipts; the rest are deleted. Per-receipt tx means cancel-mid-batch leaves nothing partial.
- [x] **Tests (7 new, 299 total):** date override reaches receipt + price rows; commit-without-override
      uses the OCR date; prepare flags missing OCR date; batch mixed outcomes; in-run signature duplicate;
      never-default-to-today; old-backfill-vs-fresh scan suppression.

**Done:** 3-file canned batch runs date-confirm→commit in tests; single-receipt path regression-green;
`dotnet test` green (299/0); Windows head builds 0/0.

*Residual 5% (unchanged):* MAUI multi-pick + the date dialog only prove out on a real device (picker
limits, huge images, dialog UX) — not drivable headless, same ceiling as Phase 8/9.

---

## Phase 2 — Item-manager + alias correction  ✅ DONE (2026-07-02)   (confidence: 95%)

The fuzzy-matching reliability lever (BACKLOG). Must exist before the physical backfill session.
Commits `8473425` (Data) + `cf01ae2` (UI). 305 tests green; Windows head builds 0/0.

| Port FROM | Port INTO |
|---|---|
| `data/repositories/items_admin_repo.py` | `GrocerySense.Data/Repositories/ItemsAdminRepo.cs` |
| (no Python UI reference — new UX) | `/items` route + Fix-mapping action on the receipt browser |

- [x] **`ItemsAdminRepo`** (`8473425`): `MergeItems` re-points **seven** item_id tables in the caller's
      transaction. **FK-enumeration correction:** the schema has 7, not the 5 this plan listed — prices,
      receipt_line_items, shopping_list, item_aliases, watchlist **plus flyer_deals and price_drop_alerts**
      (the plan and the Python `_ITEM_ID_TABLES` both omit those two). **No table has a UNIQUE(item_id)**, so
      the Python SAVEPOINT-per-table collision dance is unnecessary — reference moves are plain UPDATEs;
      `watchlist` is deduped explicitly (keep target's watch, drop source's) since nothing else stops two
      active watches. Merge also promotes tracked/default_unit and keeps the source name as an alias.
      `RenameItem` surfaces the `canonical_name` UNIQUE collision as a clear "merge instead" message.
- [x] **Alias-correction** (grill Q14 — fix line + learn): `CorrectLineMapping` re-points one receipt line
      **and the price row it produced** (keyed `receipt_id` + `raw_name` = description + old item_id) and
      `UpsertAlias(description → item)`, one transaction. No retro-sweep — historical mis-maps cleaned by
      merge. *Limitation:* an originally-unmapped line has no price row to move; the line + alias are fixed
      but the missing historical price isn't back-created (re-import covers it).
- [x] `/items` route: search, per-item alias chips, in-place rename, **Merge into…** (item picker excluding
      the source + a confirm). Receipt browser: per-line **Fix** (unmapped lines flagged) → shared
      `ItemPickerDialog` (search existing / create new). MudBlazor 9.6 confirm is `ShowMessageBoxAsync`.
- [x] Tests (6): full **7-table FK sweep** (zero orphans, source name → target alias), **atomic rollback**
      (tx revert restores every table), watchlist dedup, tracked/unit promotion, rename collision, and the
      correction (line + price + alias move together).

**Done:** merge leaves zero orphans (tested); a seeded mis-map is fixable via `CorrectLineMapping`;
`dotnet test` green (305/0); Windows head builds 0/0.

**→ Next: run the real 6-month backfill session** (the human task — scan via Phase 1's Backfill button,
confirm dates, correct mis-maps with Fix as they appear). Phase 3 tunes on its output.

*Residual 5% (unchanged):* the picker/merge/correction dialogs are net-new UX only provable on a real
device; the Data ops are fully tested.

---

## Phase 3 — Real-data tuning pass  ⏸ DEFERRED (blocked on the backfill)   (confidence: 95% — process confidence; outcomes are data-dependent by design)

> **Status 2026-07-03:** cannot start — there is no corpus. Phase 3 tunes against the real backfilled
> receipts, and the physical ~6-month scan-in (Phase 1/2 tooling is ready) hasn't been done. Fabricating a
> corpus or verdicts would violate the fail-loud/never-fake rule. Resume after the backfill; proceeding to
> Phase 4 (meal planning) in the meantime, which has no data dependency.

Evaluate-and-adjust against the backfilled corpus. "No change needed" is a valid, recorded outcome.

- [ ] **Fuzzy thresholds** (FuzzySharp 0.78 accept / 0.90 learn): measure mis-map + unmapped rates from
      the backfill (aliases written, corrections made, lines left unmapped). Adjust only if the data
      says so; document at the call site (v1 convention).
- [ ] **Optimizer defaults** (`maxStores`=3, `minItemSavingPct`=10%, `minStoreSaving`=$5): run real
      shopping lists against real history; adjust shipped defaults if plans look wrong. They remain
      user-settings either way.
- [ ] **Alert thresholds** (15% below-usual, 5% near-low, staple = ≥3 receipts/≥4 lines/90d, 30-day
      cooldown): check alert volume/quality on the first post-backfill scans — noisy vs silent.
- [ ] Record every decision (changed or confirmed) in `IMPLEMENTATION_NOTES.md`.

**Done when:** all three threshold families have a recorded verdict backed by corpus numbers.

*Residual 5%:* can't pre-commit to outcomes — the corpus decides; the process itself has no unknowns.

---

## Phase 4 — Meal planning (straight Phase-7 port)  ✅ DONE (2026-07-03)   (confidence: 96%)

The PORTING.md Phase-7 spec. Commits `2ddec89` (RecipeEngine) · `02221d6` (MealSuggestion) · `836a7d2`
(WeeklyPlanner) · `ce942f3` (route + wiring). 364 tests green; Windows head builds 0/0.

| Port FROM | Port INTO |
|---|---|
| `recipes/recipe_engine.py` | `Core/Services/RecipeEngine.cs` |
| `recipes/recipes.json` (62 recipes) | **EmbeddedResource in `GrocerySense.Core`** (assembly manifest stream) |
| `services/meal_suggestion_service.py` | `Core/Services/MealSuggestionService.cs` |
| `services/weekly_planner_service.py` | `Core/Services/WeeklyPlannerService.cs` |

- [x] **RecipeEngine** — load (embedded recipes.json, or a file path for tests) / filter-by-ingredients-and-
      profile / get-by-name; parsed via a source-gen `JsonSerializerContext`. Typed `Recipe`/`MealProfile`
      records replace the Python dict wrapper. Stable `OrderByDescending` sort (Python's sort is stable;
      `List.Sort` is not).
- [x] **MealSuggestionService** — score = 0.5·price + 0.3·preference + 0.2·variety; flyer deals
      (flyer_deals table) blended against receipt baselines (`PriceHistoryService.GetBaselinePrices`); cost
      estimate + explanation ported. **WeeklyPlannerService** — aggregate ingredients, best-effort map via
      `IngredientMappingService`, persist to `ShoppingListService` in one transaction.
- [x] **Meal-profile inputs returned to Preferences** — `PreferencesService.GetMealProfile` rebuilt (removed
      in v1) from the single household profile; Preferences gains a Meal-preferences section (preferred /
      avoided proteins, favorite cuisines → protein weights / excluded_proteins / favorite_cuisines).
- [x] `/meals` route: suggestions (score, per-serving cost with partial-estimate disclosure, reasons) +
      aggregated shopping list → add-to-list. DI registers the three services; `MealSuggestionService` takes
      an optional profile provider so the route personalizes while tests stay config-free.
- [x] All four Python suites ported: `test_recipe_engine`, `test_recipes_catalog`, `test_meal_suggestion`,
      `test_weekly_planner` (59 new tests).

**Done:** all four ported suites green; a weekly plan lands on the shopping list via the real mapping path
(tested); `dotnet test` green (364/0); Windows head builds 0/0.

**Deviations (noted):** the injected `RecipeEngine` is actually used (Python used a module singleton and
ignored the injected one — the "injected engine ignored" test is inverted to assert it drives results); the
str()-coercion-of-non-string-ingredients and module-singleton-delegation tests are dropped as Python-only
quirks; a null profile in the service means an empty profile (the route resolves the real one via
`GetMealProfile`, not a hidden config read inside the service).

*Residual 4%:* the `/meals` route UI is only provable on a real device; the scoring/aggregation is fully
tested against the same fixtures as Python.

---

## Phase 5 — Family members + meal-picks  ✅ DONE (2026-07-03)   (confidence: 95%)

Names-only members (grill Q5) + the Python family-picks flow verbatim. Commits `1b0c605` (plumbing) ·
`90a11b4` (service) · `f72df85` (UI). 377 tests green; Windows head builds 0/0.

| Port FROM | Port INTO |
|---|---|
| `config_store` member subset: list/add/rename/delete, active member, master role | `Core/Services/ConfigStore.cs` (extend) |
| `data/repositories/member_requests_repo.py` (all 7 fns) | `Data/Repositories/MemberRequestsRepo.cs` + migration 5 |
| `services/family_requests_service.py` | `Core/Services/FamilyRequestsService.cs` |

- [x] **ConfigStore members = id + name + role only.** `AddMember` (next id, secondary role, canonical
      default profile — unused for prefs), `RenameMember`, `DeleteMember` (refuses the master + last member;
      resets active to primary), `IsMaster`/`IsSecondary`. The single household profile (master member) is
      untouched — no per-member preferences.
- [x] **Flow semantics ported verbatim (no approval gate):** `PickMeal`/`PickItem` add to the shared list
      immediately via `added_by`/`added_by_member_id` (columns already existed — no shopping-list migration);
      a `member_requests` row is created **only for a secondary picker** (master never self-notifies);
      `RemoveRequest` soft-deletes exactly the rows the pick created then marks it reviewed;
      `PickableRecipes` hides household hard-excludes/allergens. Migration 5 adds the `member_requests` table
      (member_id is a config-JSON id, no DB FK; item_row_ids a JSON array).
- [x] UI: `/family` route (acting-as member selector, pickable recipes + quick-item, parent review queue
      with Keep/Remove); Household-members management in Preferences; a Family nav badge = unreviewed count
      (refreshed on navigation, best-effort, gated on DB-ready).
- [x] Tests (13): repo round-trip + junk-tolerant decode (3); member CRUD incl. delete guards (4);
      service flow — secondary-creates-request vs master-doesn't, item pick, allergen-not-pickable,
      remove-undoes-exactly-its-rows, unknown-recipe-throws (6).

**Done:** kid-picks-meal → 5 ingredients on the list attributed to "Kid" → unreviewed count 1 → Remove
undoes exactly those 5 rows, all on a real temp DB (tested); `dotnet test` green (377/0); Windows head 0/0.

*Residual 5%:* the `/family` route + nav badge are only provable on a real device; the badge refresh is
best-effort (on navigation, no live event) — a design choice, not a correctness gap. All behavior tested.

---

## Phase 6 — DB maintenance ✅ + v2 release ⛔ (blocked on Phase-0 hand-offs)   (confidence: 96%)

Code half DONE (2026-07-03); commits `8917339` (service) + `1c97dd0` (UI). 381 tests green; Windows 0/0.
The release half is blocked on the same Phase-0 items (JDK 17 + android-36 + keystore) — user action.

| Port FROM | Port INTO |
|---|---|
| `services/db_maintenance_service.py` (`backup_database`, `export_to_csv`, `export_to_json`) | `Core/Services/DbMaintenanceService.cs` |

- [x] **Backup:** `VACUUM INTO` the app cache → share sheet (grill Q15 — no new permissions, no folder
      picker). DB file only; the UI copy states receipt images aren't included. Uses `VACUUM INTO` rather
      than Python's online-backup API (a clean WAL-safe snapshot; the share-sheet flow needs no local
      backups dir, so the Python pruning is dropped).
- [x] **Export:** CSV + JSON of receipts/prices/items/shopping_list/stores → timestamped cache dir → multi-
      file share. Values stay as SQLite returns them, so **TEXT money keeps its exact string** (no
      decimal/double round-trip); CSV is RFC-4180 quoted; JSON via `Utf8JsonWriter` (AOT-safe). Missing/empty
      tables skipped.
- [x] Maintenance section on Preferences: backup, export CSV/JSON, last-backup timestamp (MAUI Preferences).
- [ ] **Release — BLOCKED (user action), same gate as Phase 0:** version bump;
      `dotnet publish -f net10.0-android -c Release` signed APK (needs JDK 17 + android-36 + the release
      keystore); regression gate = full `dotnet test` + on-device smoke of all routes (v1 six + Savings,
      Items, Meals, Family review); distribute to the ring (manual reinstall).

**Done (code):** backup opens as a valid DB, exports keep money exact, all tested; `dotnet test` green
(381/0); Windows head 0/0. **Remaining:** the signed-APK release + on-device smoke (blocked on the Android
toolchain + keystore hand-offs).

*Residual:* share-sheet file-provider wiring on Android + restore on a second device are only provable on
hardware; the backup/export logic itself is fully tested.

---

## Verification — every phase ends GREEN (unchanged from v1)

- `dotnet test Grocery_Sense/GrocerySense.Tests/GrocerySense.Tests.csproj` green at every phase boundary.
- No-partial-rows test for every new transactional write (batch commit, merge, correction, request rows).
- `dotnet build GrocerySense.Integrations` separately when integration files change (Tests doesn't build it).
- App head builds on Windows **and Android** (Phase 0 unblocks Android).
- No `NotImplementedException` in files claimed done; per-task discipline stays add-test → implement →
  green → commit.
