# Grocery Sense v2 — Follow-ups, Known Gaps & Bug-fixing Landmines

Consolidated as of **2026-07-03**, after Phases 1/2/4/5/6 (code) landed on `V2_Features_Implementation`.
The per-phase detail lives in `V2_PLAN.md` (status) and `IMPLEMENTATION_NOTES.md` (decisions); this file is
the single "what's left + what will bite you" view. State at write time: **381 tests green, 0 skipped;
Windows head builds 0/0; nothing pushed.**

---

## 1. Blocked — must happen before a v2 release (all user/environment action)

| Item | Why blocked | What it needs |
|---|---|---|
| **Android build** | This machine has only **JDK 8** | `winget install Microsoft.OpenJDK.17`, then elevated `sdkmanager … "platforms;android-36" "build-tools;36.0.0"` (SDK is under `C:\Program Files (x86)\Android\android-sdk`), then `dotnet build … -f net10.0-android`. Commands in `V2_PLAN.md` Phase 0. |
| **Release keystore** | Signing key = a secret the user must own | `keytool -genkeypair -v -keystore grocerysense-release.keystore -alias grocerysense -keyalg RSA -keysize 2048 -validity 10000`. **Back it up off-machine.** Lose it → testers uninstall/reinstall and lose local data. |
| **Signed v2 APK + on-device smoke** | Needs the two above | `dotnet publish -f net10.0-android -c Release`; smoke every route; sideload to the ring (manual reinstall). |
| **Azure OCR budget cap** | External portal | Set the cap + check per-page cost **before** the ~50–150-receipt backfill scan. |
| **Phase 3 tuning** | No corpus exists | Requires the physical backfill first (below). Then: measure + adjust fuzzy (0.78/0.90), optimizer (3 / 10% / $5), alert (15% / 5% / staple) thresholds; record verdicts. |

**The linchpin is the physical 6-month paper backfill.** It unblocks Phase 3 and turns on every
intelligence feature (alerts, optimizer, savings, meal-cost estimates are all data-starved until it runs).
Tooling has been ready since Phases 1–2: Receipts → **Backfill (multiple)** → confirm each date → fix
mis-maps with the per-line **Fix** action / the `/items` merge.

---

## 2. Nothing in the v2 UI has been exercised on a device

All v2 service/data logic is unit-tested on real temp DBs, but **no MAUI UI flow was driven** (headless
Windows host can't click dialogs / share sheets / pickers). Verify these on-device before trusting them:

- **Backfill batch**: multi-pick from gallery, the per-receipt date-confirm dialog, missing-date entry,
  cancel mid-batch, the summary counts, retention (only imported images kept).
- **Items**: `/items` search/rename/merge-confirm; receipt-line **Fix** dialog (pick existing / create new).
- **Meals** (`/meals`): Suggest, per-serving cost display, Add-to-list.
- **Family** (`/family`): acting-as selector, pick a meal/item, parent review Keep/Remove; the nav **badge**;
  member add/rename/delete in Preferences.
- **Data & backup**: share-sheet backup (the file-provider wiring is the fiddly Android bit) + CSV/JSON
  export; open a backup on a **second device/emulator** to confirm it restores.
- **Savings** (from `feat/family-savings`, pre-v2): still never smoke-tested on device (Phase-0 leftover).

---

## 3. Known limitations (they work, but have a documented ceiling)

- **Alias-correction on an *unmapped* line does not back-create its price.** `ItemsAdminRepo.CorrectLineMapping`:
  a line that OCR left unmapped (`item_id` NULL) has no price row to re-point, so fixing it re-points the
  line + learns the alias but the historical price isn't recovered. Recovery path = re-import that receipt
  with `replaceExisting`. (Wrong→right mappings, the common case, fix fully.)
- **No scan-on-ingest alerts and no local notifications.** Alerts are computed **on-demand** on the Savings
  page only; nothing fires on a scan, and there's **no `Plugin.LocalNotification` package**. The v1 grill
  (Q8) wanted "compute on scan → local notification + feed"; that was never built. Backfill "suppression"
  therefore relies on correct dates keeping old rows outside `ScanRecentReceipts`' 21-day window, not on a
  hook toggle. **If you wire scan-on-ingest later, make the backfill batch path skip it** (see the
  `ImportBatchAsync` comment).
- **Family nav badge is best-effort** — refreshed on navigation (`LocationChanged`), gated on DB-ready,
  errors swallowed. No live event from the service; the `/family` page always shows the accurate count.
- **Meal profile is a lossy single-profile projection.** `PreferencesService.GetMealProfile` maps
  `favorite_tags` from the profile's *favorite cuisines* and `prefer_meats` from protein weights > 1.0. It's
  good enough for ranking; it is not the full Python multi-field meal profile.
- **Backfill Prepare writes items/aliases before the date confirm.** If the user cancels at the confirm
  dialog, any items/aliases the mapper created during Prepare persist (harmless, matches v1 single-scan
  behavior) — only the receipt/price rows are gated on Commit.

---

## 4. Bug-fixing landmines (read before touching these areas)

1. **The item_id FK-table list is a load-bearing invariant.** `ItemsAdminRepo.ItemIdTables` (7 tables:
   prices, receipt_line_items, shopping_list, flyer_deals, price_drop_alerts, item_aliases, + watchlist
   handled separately) **and** the FK-sweep test's `ItemIdTables` array must BOTH gain any new table that
   adds an `item_id` column — or `MergeItems` silently leaves orphans. **Do not trust the Python
   `_ITEM_ID_TABLES` list; it omits two.** The FK-sweep test is the guard; keep it honest.
2. **Merge assumes no `UNIQUE(item_id)` on any table.** Today none exists, so reference moves are plain
   UPDATEs and only `watchlist` is deduped by hand. A future table with `UNIQUE(item_id)` (or a unique index
   involving it) needs collision handling (the Python SAVEPOINT-per-table pattern) reconsidered.
3. **Money is `decimal` stored as TEXT — never REAL.** SQL `SUM`/`AVG` over money columns is banned
   (aggregate in C#). Exports read cells raw (`GetValue`) specifically to keep the TEXT string exact; a
   `double` round-trip loses cents.
4. **`dotnet test` piped into `tail`/`grep` in an `&&` chain masks the exit code** (the pipeline returns
   `tail`'s status), so a failing test can slip past a `&& git commit`. This bit once (committed red, amended
   clean). **Run `dotnet test` as its own command before committing.**
5. **`dotnet test` does NOT build `GrocerySense.Integrations`** (Tests refs Core+Data only). Azure/Flipp
   client compile errors are invisible to the test run — after touching `AzureReceiptOcrClient`,
   `FlyerDocIntClient`, or `FlippClient`, run `dotnet build GrocerySense.Integrations` separately. Those
   clients are **compile-verified only** (no offline test; behavior confirmed on-device).
6. **Adding a table breaks a migration test on purpose.** `DatabaseMigrationTests.Fresh_database_reaches_latest_version_with_all_tables`
   has an `expected` present-list and a `DoesNotContain` absent-list. A new table must be added to `expected`
   (this bit me: `member_requests` was in the old "deferred, must-not-exist" list).
7. **Android is AOT — all serialized JSON must use a source-gen `JsonSerializerContext`** (`RecipeJsonContext`,
   `ReceiptSnapshotContext`, the client agent/file contexts). Reflection-based `JsonSerializer<T>` will
   trim-break on the Android head even though it "works" on Windows/tests.
8. **RecipeEngine deviations from Python are intentional — don't "fix" them back.** (a) the injected engine
   IS used (Python used a hidden module singleton and ignored the injected one — a real Python bug); (b) the
   str()-coercion-of-non-string-ingredients test and the module-singleton-delegation tests are dropped as
   C#-irrelevant; (c) a null profile in `MealSuggestionService` = empty profile (the `/meals` route resolves
   the real one via an injected provider, not a config read inside the service).
9. **Backfill "never default to today" is the rule that protects 6 months of history.** `ImportBatchAsync`
   only commits with an explicit confirmed date; a null resolver result **skips** the receipt (no write).
   Never add a today-fallback to the batch path (the single-scan path keeps its mtime/today fallback — that's
   fine for a fresh scan).
10. **Stable sort matters for recipe ranking parity.** Use `OrderByDescending` (stable), not `List.Sort`
    (unstable) — equal-score recipes must keep catalog order to match Python.
11. **Fuzzy threshold is FuzzySharp, not rapidfuzz.** Ingest accepts at 0.78 (Python receipt-ingest used
    0.75 — a documented 3-point divergence). This is the known reliability risk; the de-risk levers are the
    alias-correction UX (built) and the Phase-3 tuning pass (pending real data).
12. **`VACUUM INTO` takes a SQL string literal, not a parameter.** The path is app-controlled (cache dir) and
    single-quotes are escaped; never route user input into it.
13. **Two Razor name-clashes to remember** (`CS0542`): `Family.razor` injects `FamilyRequestsService` as
    `Picks` (not `Family`), and Preferences must fully-qualify `Microsoft.Maui.Storage.Preferences` (the
    component's own type is named `Preferences`).
14. **`member_requests.member_id` has no DB FK** (members live in `user_config.json`, not a table). Its
    `item_row_ids` JSON is decoded defensively (`JsonDocument`, junk → `[]`).

---

## 5. Deferred features (explicitly out of v2 → future / v3)

Out of v2 by decision (grill 2026-07-02), roughly in likely-value order:

- **Scan-on-ingest alerts + local notifications** (`Plugin.LocalNotification`; Android 13+ `POST_NOTIFICATIONS`) —
  the "deal alert fires after a scan" experience. Half-specced in v1, never built.
- **Phase 3 real-data tuning** (blocked on backfill — see §1).
- **v3 platform/public-store gate:** accounts/auth · multi-device sync · OCR backend proxy + per-user rate
  limiting · real flyer provider (Flipp) + ToS/legal · proactive push (FCM/APNs) · iOS + Apple heads ·
  auto-update channel (Firebase App Distribution or Play) · starter dataset + central crowdsourced price DB
  (see `reference-python/FUTURE_FEATURES.md`).
- **Per-member preference profiles** (merge/consensus/star machinery) — v2 is names-only; the Python
  multi-member `preferences_service` surface stays deferred.
- **Custom user-entered recipes** — v2 ships the fixed 62-recipe catalog only.
- **Cut entirely:** `list_audit_service`, `demo_seed_service`, `deals_service` provider-search path,
  `planning_service` cost-view (superseded by the redesigned optimizer), `distance_km`/`gas_cost_per_km`.

Reference for any deferred port: `reference-python/` + `CONTRACT_AUDIT.md` (Port/Replace/Defer ledger).
