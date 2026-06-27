# Backlog — flagged for later (Grocery Sense C# port)

Consolidated "revisit later" list so nothing is buried in PORTING/CONTRACT_AUDIT/brainstorms. Three buckets:
deferred features (v2), implementation flags to handle at the relevant port phase, and tune-after-real-data.

## v2 — deferred features
(Full classification in `CONTRACT_AUDIT.md`; scope rationale in `PORTING.md` "Deferred to v2".)
- Meal planning: RecipeEngine, MealSuggestion, WeeklyPlanner, ingredient→recipe mapping, `recipes.json`.
- Multi-member households: master/secondary merge, consensus, star annotations, member switching, family meal-picks → parent review; + cross-device sync.
- Real flyer provider (replace manual import); item-manager (catalog merge/rename); demo-seed; DB-maintenance; list-audit.
- `planning_service` (superseded by redesigned optimizer — revisit only if its cost-summary view is wanted).
- `deals_service` external-search/recipe path (v1 feed reads `FlyersRepo.list_active_deals`).
- iOS build/distribution; proactive push (FCM/APNs).

## Implementation flags — handle at the relevant phase
- **Fuzzy de-risk** (FuzzySharp ≠ rapidfuzz): tune against the real receipt corpus + add a **manual alias-correction UX** (mis-map → pick correct item → writes an alias). → Phase 3/5. The system reliability lever, not raw score parity.
- **Printed purchase-date extraction + manual fallback** — load-bearing for the backfill; if old receipts default to "today" the 6-month history is worthless. → Phase 5/8.
- **Suppress alert firing during the 6-month bulk backfill**; fire only on post-import scans. → Phase 5.
- **Bulk / multi-image receipt scan** (gallery multi-pick / batch session) for the paper backfill. → Phase 8.
- **Mobile glue:** receipt input as streams (copy temp file, no persistent picker paths); startup DB state machine (loading/ready/error); cancellation + progress; retention/delete policy for receipt images + raw OCR JSON; **never log secrets or full receipt data**. → Phase 8/9.
- **Android signing keystore** — stable + safeguarded, for in-place sideload updates (lose it → testers reinstall). → release.
- **`FlyersRepo` preference filtering** must move OUT of the repo into a Deal-feed service (layering). → Phase 4/6.

## Tune after real data
- Optimizer settings: `maxStores`=3, `minItemSavingPct`=10%, `minStoreSaving`=$5 (starting defaults).
- Alert thresholds: 15% drop-below-usual, 5% near-6mo-low, staple = ≥3 receipts/≥4 lines/90d, usual = receipt median (min 4 samples), 30-day cooldown.

## Before public-B (the v2 gate)
- **OCR backend proxy** + per-user rate limiting (Azure key can't ship in a public app); rotate/secure key.
- Accounts/auth; multi-device sync; real flyer provider + ToS/legal review; iOS + Apple Developer acct; auto-update channel (Firebase App Distribution or Play).

## Source docs
`PORTING.md` (phases + locked v1 decisions) · `CONTRACT_AUDIT.md` (Port/Replace/Defer) · `brainstorms/2026-06-24-…md` (scope grill) · `~/.claude/plans/please-do-a-full-greedy-graham.md` (business-logic/optimizer spec).
