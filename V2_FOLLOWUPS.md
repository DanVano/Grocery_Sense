# Grocery Sense v2 — Follow-ups, Known Gaps & Bug-fixing Landmines

Refreshed **2026-07-11**. v2 feature code (Phases 1/2/4/5/6), the July code-review/security fix pass,
**and all nine food-savings recommendations** (`Grocery_Sense/brainstorms/2026-07-09-family-food-savings.md`)
are done on `V2_Features_Implementation_Phase2` (**19 commits ahead of origin, not pushed**).
State at write time: **416 tests green, 0 skipped; Windows head builds 0/0; Integrations builds 0/0.**

**Product targets (decided 2026-07-11): Android + iOS ONLY.** All new features, updates, and bug
fixes target the mobile apps. The Windows head stays only as a dev harness (build checks + fixture
verification) until the Android head runs on this machine, then retires as a verification target.

The food-savings commits: `673d2bd` Shop Mode store groups + Buy/Stock-up/Wait badges + persisted
stock-up qty (migration 6) + multi-buy verdict chips · `688c403` **real Flipp provider** (unofficial
backflipp) + deal enrichment · `e676b4f` pantry likely-have hints + budget trip check · `d0ac9f0`
unit-price comparator page + cheaper same-category swaps (with coverage guard).

**The active execution plan is `V3_Phase0_plan.md`** (master: Android bring-up → backfill →
inflation feature → iOS; merges the platform plan, the backfill grill protocol, and
`INFLATION_ADJUSTMENT_PLAN.md`). `V3_PRE_PHASE0_BACKEND_CLOSEOUT.md` = the git-closeout detail it
starts with (push/merge/tag baseline).

Per-phase detail: `V2_PLAN.md` (plan + status) · `IMPLEMENTATION_NOTES.md` (decisions). Resolved
review records (July code-review findings, the executed SyncCompleted plan) were verified implemented
and deleted 2026-07-11; `SECURITY_REVIEW_FUTURE_WORK.md` stays live (standing v3 notes).

---

## 1. Next up: platform Phase 0 — Android build & release plumbing (all user/environment action)

| Item | Why blocked | What it needs |
|---|---|---|
| **Android build** | ~~JDK / API 36~~ **RESOLVED 2026-07-21 — the Android head now builds** | JDK 17 (`C:\Program Files\Microsoft\jdk-17.0.19.10-hotspot`) + API 36 platform are installed; `InstallAndroidDependencies` was run from an admin shell (the download works unelevated but the `Program Files` write needs admin). Build the head with: `JAVA_HOME` set to that JDK, `dotnet build GrocerySense.App -f net10.0-android -p:AndroidSdkDirectory="C:\Program Files (x86)\Android\android-sdk" -p:JavaSdkDirectory="C:\Program Files\Microsoft\jdk-17.0.19.10-hotspot"` → **Build succeeded, 0 errors** (7 pre-existing CS8602 nullable warnings in `AndroidLocalNotifier.cs`). Compiling is now a real verification target; **on-device run still is not** (needs a signed APK on a device/emulator — keystore row below). |
| **Release keystore** | Signing key = a secret the user must own | `keytool -genkeypair -v -keystore grocerysense-release.keystore -alias grocerysense -keyalg RSA -keysize 2048 -validity 10000`. **Back it up off-machine.** Lose it → testers uninstall/reinstall and lose local data. |
| **Signed v2 APK + on-device smoke** | Needs the two above | `dotnet publish -f net10.0-android -c Release`; smoke every route (§2 list); sideload to the ring (manual reinstall). |
| **Azure OCR budget cap** | External portal | Set the cap + check per-page cost **before** the ~50-receipt backfill scan. |
| **iOS head** | **Needs a Mac build host (Xcode) + Apple Developer account — impossible on this PC alone** | Was parked in the v3 gate (§5). Pulling it forward means Mac hardware (or a cloud Mac / CI) *first*; the shared C#/Blazor code is platform-neutral, so the work is toolchain + head config + device smoke, not a rewrite. |
| **Phase 3 tuning** | No corpus exists | Requires the physical backfill first (below). Then: measure + adjust fuzzy (0.78/0.90), optimizer (3 / 10% / $5), alert (15% / 5% / staple) thresholds; record verdicts in `IMPLEMENTATION_NOTES.md`. |

Also outstanding: **push the 19 local commits**, then merge `V2_Features_Implementation_Phase2` → `main`
and tag the v2 code baseline (steps in `V3_PRE_PHASE0_BACKEND_CLOSEOUT.md`).

**The linchpin is the physical paper backfill** — corpus reality (counted 2026-07-11): **50 receipts
spanning the last 12 months**, most crumpled, oldest fading (do it soon). Session protocol grilled
2026-07-11: `Grocery_Sense/brainstorms/2026-07-11-receipt-backfill-session-grill.md` (one photo per
receipt · chunks of 10 oldest-first · date confirmed against paper, no-date = skip, no rescue ·
fix pass after, repeat items only). It unblocks Phase 3 and turns on every
intelligence feature (alerts, optimizer, savings, meal-cost estimates, the new badges are all
data-starved until it runs). Tooling has been ready since Phases 1–2: Receipts → **Backfill (multiple)**
→ confirm each date → fix mis-maps with the per-line **Fix** action / the `/items` merge.

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
- **Food-savings recs** (all nine, committed): Shop Mode store groups + Buy/Stock-up/Wait badges on the
  shopping list, multi-buy verdict chips on Deals, **live Flipp sync** (first real network path in the
  app — test on device data/Wi-Fi), pantry hints + budget check on Plan, the comparator page, swap chips.
- **Android workflow shell (`V3_Mobile_Development`, 2026-07-19→21) — compiles on BOTH heads, never seen
  running.** Ten commits: `8236ca5` deal→list snackbar action · `018a5a6` List-centred "Plan trip"
  preview/confirm → Shop Mode · `4b04008` bottom nav + drawer demoted to More · `041d070` Shop Mode
  persisted across navigation · `91e8141` global Scan FAB · `5313894` actionable Home cards · `b04cd6a`
  `ReceiptFilePolicy` (one owner for the 20 MiB ceiling + allowlist, both capture paths funnel through
  it) · `e21d160` hardware Back walks WebView history · `9b2a804` Android receipt share target. Windows
  **and Android** heads build 0-error; 488 tests pass — but **no test exercises Razor or the Android
  platform layer**, so all UI + intent behaviour is still unproven at runtime. Verify on device:
  bottom-bar **safe-area / gesture-bar inset** + keyboard overlap; FAB position above the bar;
  active-route tinting; Plan-trip preview→confirm→Shop-Mode; Shop Mode surviving a tab-away; Home's
  resume card against a real half-finished shop; the Scan FAB camera→ingest path; **hardware Back**
  (walks in-app history, exits at root — and the known gap: a MudBlazor dialog/drawer creates no history
  entry, so Back navigates the page behind it instead of closing the overlay first — closing overlays on
  Back needs a managed open-overlay bridge, deferred); and the **share target** (does the intent filter
  appear in the sheet, cold vs warm delivery, content-URI read grants, the confirm banner, oversize/bad
  shares landing as disclosed rejects).

---

## 3. Known limitations (they work, but have a documented ceiling)

- **The Flipp provider rides UNOFFICIAL backflipp endpoints — no key, no contract.** Flipp can change or
  block them without notice. Failures throw loud (disclosed per store in `FlyerSyncResult.Errors`); the
  manual flyer-photo path (`FlyerIngestService`, reachable via Deals → "Import flyer") is the standing
  fallback. Sync covers only `ShopHere && IsActive` stores (2026-07-18 — fewer unofficial calls, matches
  every downstream consumer). ToS/legal review stays a v3 public-release gate (§5) — fine for the
  friends-&-family ring, not cleared for a store listing.
- **Alias-correction on an *unmapped* line does not back-create its price.** `ItemsAdminRepo.CorrectLineMapping`:
  a line that OCR left unmapped (`item_id` NULL) has no price row to re-point, so fixing it re-points the
  line + learns the alias but the historical price isn't recovered. Recovery path = re-import that receipt
  with `replaceExisting`. (Wrong→right mappings, the common case, fix fully.)
- **Alerts refresh after flyer sync AND single receipt scans, with native local notifications (2026-07-18:
  both halves built).** Post-sync hook: `FlyerSyncScheduler.SyncCompleted` → `PriceDropAlertService.RefreshEngineAlerts`
  (a hook-handler failure is disclosed in `FlyerSyncResult.Errors`, not faked as a sync failure) — and since
  the flyer-price unification (`GetActiveFlyerPricesBatch` reads `flyer_deals`) the refresh actually sees
  synced deals; before that fix it scanned never-populated legacy tables. Receipt half:
  `ScanAlertNotificationService.AfterSingleScanAsync` (receipt-scoped `ScanReceipt`, not the date-window
  scan) + native notifiers (`AndroidLocalNotifier`/`IosLocalNotifier` — platform APIs, not
  `Plugin.LocalNotification`) deep-linking to `/savings`, which now shows the persisted open alerts +
  dismiss. **The backfill batch path deliberately skips scan-on-ingest** (see the `ImportBatchAsync`
  comment) — keep it that way; backfill suppression still relies on correct dates.
- **Family nav badge is best-effort** — refreshed on navigation (`LocationChanged`), gated on DB-ready,
  errors swallowed. No live event from the service; the `/family` page always shows the accurate count.
- **Meal profile is a lossy single-profile projection.** `PreferencesService.GetMealProfile` maps
  `favorite_tags` from the profile's *favorite cuisines* and `prefer_meats` from protein weights > 1.0. It's
  good enough for ranking; it is not the full Python multi-field meal profile.
- **Backfill Prepare writes items/aliases before the date confirm.** If the user cancels at the confirm
  dialog, any items/aliases the mapper created during Prepare persist (harmless, matches v1 single-scan
  behavior) — only the receipt/price rows are gated on Commit.
- **Sync-on-resume is ALREADY BUILT — do not re-propose or rebuild it.** `App.CreateWindow` wires
  `window.Resumed → FlyerSyncScheduler.CheckOnResumeAsync`, which is single-flighted (semaphore, second
  caller gets `"busy"`) and throttled by `FlyerSyncService.NeedsSync()` to **at most twice a week
  (every 3.5 days)**, with the last-sync timestamp persisted atomically beside the DB and clock-skew
  treated as overdue. The documented ceiling: **deals can be up to 3.5 days stale** between manual
  syncs. This caught out a 2026-07-19 mobile-workflow analysis that proposed building resume-sync from
  scratch with a 12–24h TTL — the feature existed, and the shipped interval is deliberately *more*
  conservative (locked 2026-06-24, mobile background-execution limits). Tightening it is a product
  decision, not a tuning knob. A metered/cellular guard was considered and **rejected as YAGNI**: at
  twice a week the data cost is negligible.

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
5. **(Obsolete since Tests gained an Integrations reference — kept for history.)** `GrocerySense.Tests`
   now references `GrocerySense.Integrations`, so `dotnet test` builds it and `FlippClientTests` /
   `AzureDocIntClientSecurityTests` run offline. Live endpoint behavior is still confirmed on-device only.
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
9. **Backfill "never default to today" is the rule that protects the price history.** `ImportBatchAsync`
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
15. **`CorrectLineMapping` moves exactly ONE price row on purpose.** Two identical-description lines on one
    receipt produce identical price rows; an unbounded UPDATE would move both when fixing the first line.
    There is deliberately no `line_item_id` column on `prices` — add one only if that pairing ever has to be
    exact (see the ponytail comment at the call site).

---

## 5. Deferred features (explicitly out of v2 → future / v3)

Out of v2 by decision (grill 2026-07-02), roughly in likely-value order:

- **~~Receipt-scan-on-ingest alerts + local notifications~~ — DONE (2026-07-18, no longer deferred).**
  Both halves built: see §3. Native platform notifiers were used instead of the `Plugin.LocalNotification`
  package this entry originally named.
- **Phase 3 real-data tuning** (blocked on backfill — see §1).
- **v3 platform/public-store gate:** accounts/auth · multi-device sync · OCR backend proxy + per-user rate
  limiting · Flipp ToS/legal review (provider itself is **built**, §3 — the legal clearance is what's
  gated) · proactive push (FCM/APNs) · **iOS + Apple heads
  (Dan wants to revisit — see the §1 Mac-hardware prerequisite)** · auto-update channel (Firebase App
  Distribution or Play) · starter dataset + central crowdsourced price DB
  (see `reference-python/FUTURE_FEATURES.md`).
- **Per-member preference profiles** (merge/consensus/star machinery) — v2 is names-only; the Python
  multi-member `preferences_service` surface stays deferred.
- **Custom user-entered recipes** — v2 ships the fixed 62-recipe catalog only.
- **Cut entirely:** `list_audit_service`, `demo_seed_service`, `deals_service` provider-search path,
  `planning_service` cost-view (superseded by the redesigned optimizer), `distance_km`/`gas_cost_per_km`.

Reference for any deferred port: `reference-python/` + `CONTRACT_AUDIT.md` (Port/Replace/Defer ledger).
