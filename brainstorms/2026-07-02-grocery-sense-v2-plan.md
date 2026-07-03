# Grocery Sense v2: Implementation-Plan Grill / Discovery Notes
Date: 2026-07-02 · Goal: Lock v2 scope + per-phase decisions to ≥95% implementation confidence, producing a V2 plan doc (PORTING.md analog).

**Deliverable produced: `V2_PLAN.md`** (repo root, beside PORTING.md). This file is the raw capture.

## Summary / key decisions
- **v2 = FAMILY RELEASE** to the same F&F Android ring. Public-store infra (OCR proxy, accounts, sync, auto-update, iOS, real-flyer legal) → **v3** wholesale.
- **Final phase list (all ≥95% confidence):**
  0. Baseline hygiene: verify savings feature → commit stub deletions → merge to main + tag v1 → Android SDK 36 → keystore (97%)
  1. Backfill on-ramp: Prepare/Commit ingest split, multi-pick batch, date-confirm-only stops, alert suppression (95%)
  2. Item-manager + alias correction (fix-line-and-learn; merge FK sweep incl. new `watchlist` table) (95%)
  → *physical 6-month backfill session runs here*
  3. Real-data tuning: fuzzy 0.78/0.90, optimizer 3/10%/$5, alert thresholds — verdicts recorded (95% process)
  4. Meal planning straight port: recipes.json embed, RecipeEngine/MealSuggestion/WeeklyPlanner, meal-profile UI inputs return (96%)
  5. Members (names-only) + meal-picks: no approval gate (Python-verbatim flow), request rows for secondaries only (95%)
  6. DB maintenance (VACUUM INTO + share sheet) + signed v2 APK release (96%)
- **Members = names only** — single household profile untouched; no per-member prefs/consensus/star machinery.
- **Baseline:** verify + commit `feat/family-savings`, merge into `main`, tag v1; v2 branches cut from main.
- **Stub deletions committed** — v2 built fresh from the Python reference (CONTRACT_AUDIT enumerates the APIs).
- `v3-expanding-core-features-*` branches stale — delete.
- **Cut from v2:** list-audit, demo-seed, custom recipes, starter dataset, deals_service search, planning_service cost-view.

## Established facts (from repo/docs inspection, pre-grill)
- v1 port COMPLETE: Phases 0–9 done (276 tests green, Windows head builds; Android blocked only on SDK Platform 36 install; Apple heads need a Mac).
- All work lives on `feat/family-savings` (currently checked out): full v1 history + NEW post-v1 savings work (WatchlistRepo/Service, wait-for-sale planning, budget forecast, Savings page, list priority selector).
- `origin/main` = initial commit only. Per-phase branches exist for v1.
- `v3-expanding-core-features-phase1/2` branches exist but point at EARLY scaffold commits (stale bases, no unique work).
- Working tree (uncommitted): deletes ALL v2-deferred stubs (RecipeEngine, MealSuggestion, WeeklyPlanner, FamilyRequests, MemberRequestsRepo, ItemsAdminRepo, DealsService, DemoSeed, DbMaintenance, ListAudit) + Tizen head. Pure deletions — v2 features would be built fresh from the Python reference, not by filling stubs.
- BACKLOG.md v2 bucket: meal planning; multi-member households (+cross-device sync); real flyer provider; item-manager; demo-seed; db-maintenance; list-audit; planning_service cost-view (superseded); deals_service search path; iOS; proactive push.
- BACKLOG.md "Before public-B (the v2 gate)": OCR backend proxy + rate limiting; accounts/auth; multi-device sync; real flyer provider + ToS/legal; iOS + Apple acct; auto-update channel.
- FUTURE_FEATURES.md (Python-era, still relevant): starter dataset (local seed) + central crowdsourced price DB (managed backend — Supabase/Pocketbase recommended; observations-only privacy model).
- Python reference (`reference-python/`) remains the spec for all deferred features; CONTRACT_AUDIT.md enumerates every Defer API.

## Q&A log

### Q1 — v2 definition of done
- Asked: Is v2 the deferred-features bucket (family release) or the public-release infra gate?
- Captured: **Family release.** v2 = household/family feature release to the existing friends-&-family Android ring: multi-member, meal planning, savings polish, utilities. The whole public gate (OCR proxy, accounts/auth, multi-device sync infra, auto-update channel, iOS, real flyer provider + ToS/legal) moves wholesale to v3.

### Q2 — v2 code baseline
- Asked: Where does v2 branch from, given all work sits on `feat/family-savings` and origin/main is empty?
- Captured: **Merge to main first.** Finish/commit family-savings, merge into main (bringing all v1 history), tag as v1. v2 phase branches cut from main. Main becomes meaningful.

### Q3 — uncommitted stub deletions
- Asked: Intent of the working-tree deletions of all v2-deferred stub files (+ Tizen head)?
- Captured: **Commit the deletions.** Dead stubs go; v2 features get built fresh from the Python reference. No compat shims (house style).

### Q4 — v3-expanding-core-features branches
- Asked: Do the stale v3-named branches carry roadmap meaning?
- Captured: **Stale — ignore/delete.** No bearing on version naming; the thing planned here is v2.

### Q5 — multi-member shape
- Asked: Given the v1 finding "household has no differing allergies/dietary needs," what must multi-member do in v2?
- Captured: **Members = names only.** Prefs stay uniform (single household profile unchanged). Members are lightweight identities (id + name) so the family meal-picks → parent-review flow can attribute requests, on the one shared phone. **Skip entirely:** per-member profiles, master/secondary merge, allergy merge, soft-exclude consensus, star annotations, per-member preference editing. This kills most of the Python `preferences_service` Defer surface and the config member-profile CRUD; keeps a small member list + active-member picker.

### Q6 — meal planning scope
- Asked: How much of RecipeEngine → MealSuggestion → WeeklyPlanner ships in v2?
- Captured: **Straight port** of the Phase-7 spec: embed the 62-recipe `recipes.json` in Core (EmbeddedResource), RecipeEngine filter → MealSuggestion scoring → WeeklyPlanner aggregation + ingredient→list mapping, plus a Meals route. Port the 4 Python test files. No custom-recipe entry in v2. MealSuggestion's meal-profile inputs (protein weights/cuisines) read from the single household profile.

### Q7 — utilities in v2
- Asked: Which of item-manager / db-maintenance / list-audit / demo-seed ship?
- Captured: **Item-manager + DB maintenance.** Item-manager = ItemsAdminRepo merge/rename + the manual alias-correction UX (never built in v1; the fuzzy-matching reliability lever). DB maintenance = backup + CSV/JSON export (protects real data, no sync exists). **Skip list-audit and demo-seed.**

### Q8 — 6-month paper backfill
- Asked: v1 shipped without the backfill tooling (verified: no multi-image pick, no manual-date-fix UI, no backfill alert suppression in the C# code). Has the backfill happened?
- Captured: **Not done — build the tooling in v2.** Gallery multi-pick batch import, per-receipt date confirm/fix step, alert suppression for backfill-flagged imports, fuzzy-threshold tuning + alias corrections against the real corpus. Positioned as v2's data on-ramp — alerts/optimizer/savings/budget history are starved until it lands.

### Q9 — savings feature state
- Asked: Is feat/family-savings complete?
- Captured: **Needs a verify pass first.** Feature landed (schema+service+UI+tests) but hasn't been exercised on-device. Phase 0 gets an explicit verification task (drive watchlist/wait-for-sale/budget-forecast on the Windows head or Android) BEFORE the merge to main.

### Q10 — phase ordering
- Asked: Backfill-first ordering vs meal-planning-first vs members-first?
- Captured: **Backfill first.** (0) baseline hygiene + Android SDK 36 + keystore → (1) backfill on-ramp (+ item-manager/alias-correction paired, since corrections happen DURING the scan-in) → (2) real-data tuning (fuzzy thresholds, optimizer defaults, alert thresholds) → (3) meal planning → (4) members + meal-picks → (5) db-maintenance + release.

### Q11 — distribution
- Asked: Stay on raw APK sideload for v2?
- Captured: **Keep sideload.** Same F&F ring/trust model. Stable+safeguarded keystore becomes a Phase-0 checklist item. Auto-update channel stays v3.

### Q12 — completeness backstop
- Asked: Anything missing from scope?
- Captured: **Nothing further.** Locked OUT of v2: accounts, sync, OCR proxy, real flyer provider, proactive push, iOS, starter dataset, custom recipes, list-audit, demo-seed.

### Q13 — backfill per-receipt interaction
- Asked: Date-confirm only vs full line review vs unattended, for the 50–150-receipt paper stack?
- Captured: **Date-confirm only.** Each receipt pauses on the extracted purchase date (merchant/total shown for context); confirm or fix → commit → next. Missing date = mandatory manual entry (never default to today in backfill mode — the poison case). Line-item mis-maps get fixed opportunistically via alias-correction, not inline.

### Q14 — alias-correction semantics
- Asked: What happens when the user fixes a mis-mapped receipt line?
- Captured: **Fix line + learn.** One transaction: re-point the line item + its price row to the correct item AND upsert the alias for future scans. No automatic retro-sweep — historical damage is cleaned via item-manager merge.

### Q15 — export/backup destination
- Asked: Share sheet vs SAF folder picker for DB backup + CSV/JSON export?
- Captured: **Share sheet.** Export/backup writes to app cache → Android share sheet. No new permissions. Backup = VACUUM INTO → share.

### Facts resolved from code (not asked)
- Python `family_requests_service` fully specifies the meal-picks flow: **no approval gate** — picks add to the shared list immediately, attributed via `added_by`/`added_by_member_id`; request rows created only for SECONDARY members; parent gets unreviewed badge + review queue; remove = soft-delete the created rows + mark reviewed; `pickable_recipes()` filters by household hard excludes/allergies.
- `added_by`/`added_by_member_id` columns survived the v1 port (shopping_list schema + repo) — members phase needs no shopping-list migration.
- `recipes.json` = 62 recipes.
- Receipt ingest already parses `TransactionDate` (two call sites in ReceiptIngestionService) — backfill needs the confirm/override UX, not new extraction.

## Open flags (pending input)
- Azure OCR per-page cost of the ~6-month backfill — user should sanity-check the Azure budget cap before the bulk session. -> user, before the backfill session runs.
- Item-merge FK sweep must include the NEW `watchlist` table (added by feat/family-savings, post-dating the Python reference) — verify all FK-bearing tables enumerated at implementation time. -> Phase 2 implementer.
