# Grocery Sense C# / Blazor MAUI Port: Brainstorm / Discovery Notes
Date: 2026-06-24 · Goal: Extract intent for porting the Python/Tkinter Grocery Sense prototype to a C# .NET 10 MAUI Blazor Hybrid app (iOS/Android) — scope, distribution, priorities, cuts — into a durable spec that survives context loss.

## Summary / key decisions
- **v1 = personal/household tool**, shared to a friends-&-family test ring to bug-fix and refine workflow. **Public app-store product (B) is a stated future goal** → build A now, architect so B is reachable (OCR/provider behind interfaces; don't bake single-user-only assumptions that block multi-user/accounts later).

- **Android-first.** User's own phone is Android (primary dogfood device); buildable + sideloadable from Windows with no Mac/Apple-account/cost. iOS deferred until Mac/CI access or the public-B push. Codebase stays cross-platform — iOS not blocked, just not a v1 target.
- **v1 builds BOTH halves**: (1) receipt → price-intel → smart list, AND (2) deal alerts. User has **~6 months of old receipts to bulk-import day 1**.
- **Critical finding (verified in code):** the receipt backfill powers the *receipt-driven* alert engine fully, but NOT forward-looking flyer deals (see Q3). "Both halves" therefore makes **flyer data source a v1-blocking decision** (Flipp = empty stub).
- **Flyer source = C (hybrid).** v1 = **manual flyer-photo import** via the Azure *layout* OCR (`FlyerDocIntClient`) + existing flyer-ingest pipeline + `flyer_deals` schema. Real provider/scraper deferred to public-B (fragile, ToS/legal). Swap seam = `IFlyerProvider` (already in PORTING 0.C).
- **Data on-ramp = clean start, scan via C# pipeline.** 6 months of receipts are **on paper** → scanned through the C# Azure-OCR ingest. **No Python→C# DB migration** (existing 0.22 MB prototype DB irrelevant to v1). Consequences: ingest pipeline is the day-1 on-ramp (prioritize); needs **bulk/multi-image scan UX** and **reliable printed-date extraction** (else the 6-mo history collapses).

- **Meal planning DEFERRED to v2** (RecipeEngine, MealSuggestionService, WeeklyPlannerService, ingredient→recipe mapping, `recipes.json`). v1 **keeps preference data + filtering** — allergies/excludes that the **Deal feed** filters on.
- **Single profile in v1 (no multi-member).** Household has no differing allergies/dietary needs → one preference set: allergies + hard/soft excludes + oils-allowed, consumed by deal filtering. **No** master/secondary, consensus/strong-soft merge, star annotations, member switching, or family meal-picks — all → v2. (Recommendation: store the single profile in a shape that can later become the "master member" so v2 multi-member needs no data migration.)
- **Alerts: v1 = in-app feed + local notifications (on-device compute); v2 = real push.** Alerts compute on-device after a scan / flyer import; new alerts fire a **local notification** + populate the in-app feed. No background polling, no server (consistent with no-backend v1). Proactive push while app is closed (server + FCM/APNs) → v2.
- **Distribution = raw APK sideload (A).** Email/Drive the APK; testers enable "install unknown apps." Zero infra/cost. Trade-off accepted: **no auto-update** (push a new APK + ping testers to reinstall each build). **Azure key embedded** in the app (no backend) with an **Azure budget cap + spend alert**; rotate if an APK leaks; OCR proxy is the gate before public-B.
- **UI = MudBlazor (A), 5 touch-first screens.** Greenfield UI (Tk windows hold no logic). Don't clone the 16 desktop windows — build Receipts, List, Deals, Plan, Preferences. MudBlazor (free/MIT) for fast, accessible, touch-sized components in BlazorWebView; spend effort on logic, not restyling controls.
- **Optimizer REDESIGN (not a port).** Drop the Python trip-penalty (distance_km × gas_cost_per_km) entirely — **the user decides travel, not the app.** New goal: **minimize number of stores** while capturing meaningful savings. Don't route the user to 5 stores for 5 marginally-cheaper items; prefer e.g. 2 stores splitting the list. An item only "pulls" a new store onto the plan if its savings exceed a **marginal-savings threshold** (exact % TBD — make it a tunable constant with a sensible default). **Time spent shopping is a first-class value.** → `distance_km`/`gas_cost_per_km` cut from v1 setup + optimizer math; `BasketOptimizerService` is partly new logic (store-count minimization + threshold), not a translation.
- **Budget tracking IS in v1** (user moved it out of defer). `BudgetService` (month spend vs budget + trend) ships in v1.
- **Unit normalization is core/non-negotiable in v1.**
- **v1 boundaries (confirmed Q11):** no accounts/login (local-only per device); **offline** = scan + flyer import need network (Azure OCR), everything else works offline on local SQLite; **single locale = CAD / en-CA / Canadian postal codes** (no i18n/multi-currency); **stores + postal code defined in v1 setup** (store management + shop-here in v1; flyers + the redesigned store-count optimizer key off your defined stores). *(distance_km / gas_cost cut per Q12 — see Optimizer REDESIGN.)*

Established before this session (facts, not decisions):
- Scaffold exists at `Grocery_Sense_Main/Grocery_Sense/` — 5 projects (App/Core/Data/Integrations/Tests), .NET 10, compiles, tests green. App head builds on net10-windows after elevated workload restore.
- DB: SQLite via `Microsoft.Data.Sqlite` (raw SQL, no ORM). `SQLitePCLRaw.bundle_e_sqlite3 3.0.3`.
- OCR: keep Azure Document Intelligence (.NET SDK wired).
- Fuzzy: FuzzySharp (rapidfuzz replacement) — known score-drift risk at thresholds.
- Docs in repo: `README.md`, `PORTING.md` (10-phase playbook), `reference-python/ARCHITECTURE.md` (full inventory).
- Python source copied to `reference-python/` as the port spec.

## Deferred to v2 (future development backlog)
- **Meal planning** — RecipeEngine, MealSuggestionService, WeeklyPlannerService, ingredient→recipe mapping, `recipes.json` embed. (Decided Q6.) Preference *inputs* that only meal-suggestion uses (protein weights, cuisines) likely move here too.
- **iOS** — needs Mac/CI + Apple Developer acct (Q2).
- **Real flyer provider** (Flipp/scrape) replacing manual import — fragility + ToS/legal review (Q4).
- **OCR backend proxy** + per-user rate limiting — required before public-B so the Azure key doesn't ship on devices (Q1).
- **Real/proactive push notifications** (server + FCM/APNs) — fire deal alerts while the app is closed. v1 is local-notification-on-compute only (Q8). Arrives with the backend.
- **Multi-member households** (decided Q7) — master/secondary members, household-wide allergy merge, soft-exclude + strong-soft consensus, star annotations, member switching, and the family meal-pick → parent-review flow. v1 is single-profile; build the profile forward-compatible (could become the "master member") to avoid a v2 migration.
- **Multi-device household sync** — each member on own phone; needs sync infra (ties to multi-member above).
- Deferred admin/utility screens: item-manager (catalog merge/rename), demo seed, DB maintenance, family requests, list audit. **NOT deferred (now v1):** Budget (Q12), store management + postal/shop-here (Q11). `PORTING.md` reconciled to match (session end).
- (A minimal "fix this item mapping" / alias-correction UX may be pulled forward into v1 to de-risk fuzzy matching — see confidence notes; distinct from the full item-manager.)

## Q&A log

### Q1 — v1 purpose / definition of done
- Asked: Is v1 a personal tool (A), public app-store product (B), or portfolio/demo (C)?
- Captured: **A now, B later.** Start personal/household; share to friends & family to bug-fix + improve workflow. Future: move to public app-store product. → Build A, keep the path to B open (interfaces for OCR/flyer provider; allow for accounts/multi-user later without a rewrite).
- Flags: Azure OCR key will ship on friends'/family devices — small trusted circle, but the key is extractable (abuse/cost exposure). Decide tolerance vs. standing up the backend proxy earlier -> user (revisit at Distribution + before B).

### Q2 — Platform priority
- Asked: Android-first, iOS-first, or both? (Constraint: Windows-only, no Mac.)
- Captured: **Android-first** — user's phone is Android. iOS deferred (needs Mac + Apple Developer acct + TestFlight). Distribute to testers via APK sideload / Play internal track.
- Flags: full tester iPhone/Android split not captured — could pull iOS earlier if testers are iPhone-heavy -> user (minor).

### Q3 — v1 core loop / can 6mo receipts power "alerts"?
- Asked: Is v1 the receipt→price→list loop (deals deferred), or are deal-alerts non-negotiable for v1?
- Captured: **Build BOTH in v1.** User will bulk-import ~6 months of old receipts on day 1 and asked if those power the alerts.
- Answer (verified against `price_drop_alert_service.py`):
  - **YES — receipt-driven alerts.** The engine learns "usual price" = receipt median, "staples" = receipt frequency (≥3 receipts / ≥4 lines in 90d), "6-month low" from the price DB. Two alert types: **below-usual** (current ≥15% under usual) and **stock-up** (within 5% of 6-mo low + not seen that low in ≥30d). All receipt-powered. The 6-mo backfill's real win: it **kills the cold-start** — baselines are accurate on day 1 instead of after months of scanning, so the FIRST newly-scanned receipt yields meaningful alerts.
  - **Caveat:** bulk-importing history computes baselines correctly but the "current price" per item = the latest *historical* receipt → expect **stale, non-actionable alerts at import** ("milk was 20% under usual" — back in March). Implementation detail: **suppress alert firing during the historical backfill**; let alerts fire on receipts scanned *after* import.
  - **NO — flyer/local-store deals.** "On sale near me this week" is forward-looking advertised prices (`get_active_flyer_prices_batch` reads flyer_sources/flyer_deals with validity windows). Receipts are past purchases; they cannot supply this. Flipp = empty stub → **a flyer data source must be chosen for v1** (next question).
- Flags: flyer data source unresolved (v1-blocking) -> Q4. Bulk-import alert-noise suppression -> ingestion implementation detail.

### Q4 — Flyer/deal data source for v1
- Asked: Manual import (A), real provider (B), or hybrid (C)?
- Captured: **C — hybrid.** v1 ships **manual flyer-photo import** (buildable now: Azure layout OCR + flyer-ingest pipeline + `flyer_deals` table already scaffolded). Real provider integration (Flipp/scrape) deferred to the public-B push due to fragility + ToS/legal risk. Keep `IFlyerProvider` as the seam so the provider drops in without touching the deal-matching/UX.
- Flags: real-provider integration carries fragility + legal review -> revisit before B.

### Q5 — Form of the 6-month backfill / migrate vs rescan
- Asked: Already in Python DB (A), still paper (B), or fresh start (C)?
- Captured: **B — still on paper; will scan through the C# pipeline into the new DB.** No DB migration; clean start. (Python prototype DB exists — 0.22 MB, last write 2026-06-20 — but is NOT carried over.)
- Consequences:
  - **Drop the Python→C# migration task** — Data phase (PORTING 2) simplifies.
  - **Ingest pipeline (PORTING 5) is the day-1 on-ramp** — prioritize it; dedupe (file-hash + signature) matters so re-scans don't double-count.
  - **Bulk-scan UX**: ~6 months of paper one-photo-at-a-time is brutal → support **multi-image pick from gallery / a batch-scan session** (Python had folder-scan; mobile needs multi-photo selection). Net-new mobile requirement.
  - **Printed purchase-date extraction is load-bearing**: OCR must capture each receipt's real date (+ manual fallback when missed). If old receipts default to "today," the 6-month price history is worthless — the whole backfill premise breaks.
  - Mute alerts during the bulk backfill session (ties to Q3 flag).
  - Minor Azure $ on the user's key for ~6 months of scans (verify per-scan pricing).
- Flags: bulk multi-image scan UX; printed-date extraction + manual fallback -> ingest design (PORTING 5/8).

### Q6 — Meal planning in v1?
- Asked: Meal planning/recipes in v1, or defer to v2?
- Captured: **Defer to v2** (logged in "Deferred to v2" section above for future dev, per user request). **v1 keeps preference data + filtering** — the allergy/exclude data that the Deal feed consumes.
- Scope nuance (recommendation, confirm): v1 Preferences = allergies (hard, household-wide) + hard/soft excludes + oils-allowed — i.e., exactly what `FlyersRepo` deal-filtering reads. Protein-weight + cuisine preference inputs are consumed ONLY by meal-suggestion → defer those input fields to v2. Keeps the v1 Preferences screen lean.
- Flags: confirm whether v1 Preferences trims protein-weight/cuisine inputs -> Q7-area.

### Q7 — Household members in v1?
- Asked: Single profile (A), multi-member one-device (B), or multi-member + sync (C)?
- Captured: **A — single profile.** No differing allergies/dietary needs in the household. Multi-member (master/secondary, allergy merge, consensus, family picks) + cross-device sync → **v2** (noted in Deferred section, per user request).
- Effect on the port: `PreferencesService.compute_effective_preferences` collapses to "read one profile"; drop the member-merge/consensus/star/annotation machinery and `family_requests`/`member_requests` from v1. v1 Preferences screen = allergies + hard/soft excludes + oils → deal filter. Protein-weight/cuisine inputs stay deferred (only meal-suggestion uses them).
- Recommendation: persist the single profile in a shape that can become the v2 "master member" (forward-compatible) so multi-member needs no data migration later.

### Q8 — Alert delivery mechanism
- Asked: In-app feed only (A), feed + local notifications (B), or hold for push (C)?
- Captured: **B for v1, C for v2.** v1: alerts compute on-device after a scan / flyer import → fire a **local notification** + show in the in-app alerts feed. No background polling, no server. v2: real proactive push (server + FCM/APNs) for "deal dropped while app closed."
- Tech notes / flags: MAUI has no built-in local notifications → add a package (e.g. `Plugin.LocalNotification`). Android 13+ needs the `POST_NOTIFICATIONS` runtime permission. On-device alert compute triggers on scan/import events (not a background timer) — reinforces replacing the Python `FlyerSyncScheduler` timer with event/resume-driven compute.

### Q9 — Distribution + Azure key handling
- Asked: APK sideload (A), Firebase App Distribution (B), or Play Internal (C)? Plus: embed key + budget cap?
- Captured: **A — raw APK sideload.** Zero infra/cost. **Key embedded** with **Azure budget cap + spend alert** (taken as decided given no-backend + sideload; user to veto if not).
- Consequences / flags:
  - **No auto-update** — each build = new APK emailed/Drive'd; testers manually reinstall. Fine for a tiny ring; gets painful past ~5 testers → revisit Firebase (B) then.
  - **Stable signing keystore required** — in-place updates need the same signature. Create one and **safeguard it** (lose it → testers must uninstall/reinstall, losing local data).
  - Build = `dotnet publish -f net10.0-android -c Release` (APK, not AAB, for sideload). **Bump version each build** so builds are distinguishable.
  - Tell testers: enable "install unknown apps"; Play Protect may warn on sideloaded APKs.
  - Azure key: set the budget cap before sharing; rotate if an APK escapes the circle. Proxy before public-B (in Deferred list).

### Q10 — UI build approach
- Asked: MudBlazor (A), hand-rolled CSS (B), or commercial kit (C)?
- Captured: **A — MudBlazor.** 5 touch-first screens (Receipts, List, Deals, Plan, Preferences); no cloning of the 16 desktop windows. UI is greenfield — Tk layouts carry no logic.
- Tech notes: add `MudBlazor` package to the App, `AddMudServices()`, include MudBlazor CSS/JS in `wwwroot/index.html`, wrap root in `MudThemeProvider`. Confirmed-compatible with MAUI BlazorWebView.

### Q11 — v1 boundary confirmations
- Asked: Confirm no-accounts, offline behavior, single CAD/en-CA locale, stores+postal in v1 setup.
- Captured: **All four confirmed.** (1) No accounts/login, local-only per device. (2) Offline: scan + flyer import need network; price intel/list/alerts/deal feed/preferences work offline on local SQLite. (3) Locale fixed CAD / en-CA / Canadian postal; no i18n or multi-currency in v1. (4) User defines stores + postal in setup; store management + shop-here are v1. (NOTE: superseded by Q12 — distance/gas dropped from optimizer.)

### Q12 — Completeness backstop (loose ends)
- Asked: Plan optimizer in v1? Budget in/out? Units core? Anything missed?
- Captured:
  1. **Optimizer = redesign.** No distance/gas in the math (user decides travel). Goal: **fewest stores** that still capture meaningful savings; skip splitting a trip for an item that's only marginally cheaper elsewhere (marginal-savings **threshold %, value TBD/tunable**). Example: 5 items each cheapest at a different store → prefer 2 stores splitting them, not 5 stops. **Shopping time is a key value.** (See Summary "Optimizer REDESIGN".)
  2. **Budget tracking → v1** (not v2).
  3. **Unit normalization = core/non-negotiable.**
  4. **End grill session;** reconcile decisions into the port files (PORTING.md). User also asked for refreshed port-confidence ratings (in chat, not this file).
- Flags: optimizer marginal-savings threshold % unset → tunable constant + sensible default; revisit with real use -> user/future.

## Open flags (pending input)
- ~~Azure OCR key shipping on F&F devices~~ RESOLVED (Q9): embed + Azure budget cap + rotate-if-leaked for v1; OCR proxy before public-B.
- Android signing keystore — create + safeguard for in-place sideload updates -> build setup.
- No auto-update with sideload — manual reinstall per build; revisit Firebase if the ring grows -> distribution.
- Optimizer marginal-savings threshold % is unset — implement as a tunable constant w/ sensible default; set the real value after real use -> user/future.
- Fuzzy-match fidelity (FuzzySharp ≠ rapidfuzz): de-risk via real-receipt-corpus tuning + a manual alias-correction UX -> see confidence notes / ingest UX.
- Bulk-import alert-noise: suppress alert firing during 6-mo historical backfill; fire only on post-import scans -> ingestion implementation detail.
- Real flyer-provider integration (v2/public-B): fragility + ToS/legal review -> revisit before B.
- Bulk multi-image scan UX (gallery multi-pick / batch session) for the paper backfill -> ingest UX design.
- Printed purchase-date extraction + manual fallback (backfill integrity) -> ingest design.
