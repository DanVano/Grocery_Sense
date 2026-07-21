# Android performance baseline — 2026-07-18

Baseline + evidence for the performance-review implementation (`performance_review_codex_0718.md`).

## Status of the measurement tasks

| Task | Status |
|---|---|
| 1 — Android Release device baseline | **PARTIAL / BLOCKED** — see below |
| 9 — Re-profile then stop | **BLOCKED** — needs Task 1 device traces |

**Why blocked:** the Android head does not build/publish in the current environment (Windows dev box,
no Android SDK/keystore; `dotnet build GrocerySense.sln` needs a Mac for the iOS/macCatalyst heads, and
Android Release is gated on the toolchain + signing hand-offs tracked in `../V2_FOLLOWUPS.md`, plus the
XA5207 blocker noted in the SEC remediation commit). So there is **no on-device cold-start, warm-nav,
frame-time, memory, or connection-open trace** here. The perf gates in the plan (p95 cold launch ≤ 2.0 s,
warm nav ≤ 250 ms, etc.) remain **unverified on device** and must be captured once the Android toolchain
hand-off lands. Do not mark the gates "passed" — they are untested.

What **was** verifiable on Windows is recorded below: the automated suite, and the SQLite query plans
(plan choice is platform-independent, so this is valid evidence for the SQL-shape tasks 2–4).

## Automated baseline (Task 1, Step 1)

```
dotnet test GrocerySense.Tests -c Release
Passed! Failed: 0, Passed: 484, Skipped: 0
```

Count grew from the plan's stated 469 → **484**: +15 focused regression tests added by tasks 2–8
(index plan guard, bounded item-search equivalence, receipt paging + month boundaries, batch price
history, category-scoped swaps, caller-owned mapping connection). 0 failed.

## Query-plan evidence (Task 1, Step 4 — captured on Windows via `EXPLAIN QUERY PLAN`)

Seeded a fresh migrated DB (300 items, matching prices + receipts). Plan selection depends on the
available indexes and query shape, not row counts (no `ANALYZE` is run), so these plans are what the
device will use too.

**Exact item lookup (Task 2)** — now seeks the new case-insensitive index instead of scanning:
```
SEARCH items USING COVERING INDEX idx_items_name_nocase (canonical_name=?)
```

**Month spend (Task 4)** — now a range seek on the purchase-date index instead of a `STRFTIME` full scan:
```
SEARCH receipts USING INDEX idx_receipts_purchase_date (purchase_date>? AND purchase_date<?)
```

**Recent receipts (Task 4)** — the page is materialised first (`CO-ROUTINE page`), then line counts are
joined only for that page's receipts via the covering receipt-id index (no whole-table line GROUP BY):
```
CO-ROUTINE page
SCAN r
SCAN p
SEARCH s USING INTEGER PRIMARY KEY (rowid=?) LEFT-JOIN
SEARCH li USING COVERING INDEX idx_receipt_line_items_receipt_id (receipt_id=?) LEFT-JOIN
```

**Item search (Task 3)** — the `selected` items are materialised (bounded by LIMIT), then `price_stats`
joins prices to that bounded set (bloom-filtered) instead of grouping the entire prices table into a
discarded subquery:
```
MATERIALIZE selected
SCAN i
USE TEMP B-TREE FOR ORDER BY
MATERIALIZE price_stats
SCAN p USING INDEX idx_prices_item_coalesced
BLOOM FILTER ON s (id=?)
SEARCH s USING AUTOMATIC COVERING INDEX (id=?)
```
Honest caveat: the planner still drives `price_stats` by scanning `prices` and probing the bounded
`selected` set (bloom filter), rather than seeking per-selected-item. That is already a strict win over
the old unconditional full-table GROUP BY (stats are only built for the ≤ limit matched items). On the
representative 100k-price dataset, confirm with `EXPLAIN QUERY PLAN` whether the planner prefers a
per-item seek (`idx_prices_item_date`); if it keeps the full `prices` scan and the 100 ms search gate is
missed, run `ANALYZE` (see Task 9 conditional) — do not add a cache. The outer `SCAN i` for the substring
`LIKE` is inherent (a leading-`%` LIKE cannot use an index) and bounded by item count, not price history.

## What still needs a device (do when the Android hand-off lands)

Run the full Task 1 protocol on an Android Release build against the representative dataset (2,000 items,
100k prices, 5,000 receipts, 75k lines, 5,000 deals, 100 list rows):

1. `dotnet publish GrocerySense.App -f net10.0-android -c Release` (expect clean trim/AOT).
2. 20 cold starts + 30 warm runs per gated page; 40-line receipt map (novel vs warm-alias), OCR excluded.
   Record device/OS/commit/row-counts, median/p95/max, memory, connection-open count, visible jank.
3. `EXPLAIN QUERY PLAN` on the representative DB for the four queries above (confirm the Task 3 caveat).
4. Then Task 9: re-run every trace, compare before/after, and take only the conditional micro-opts whose
   gate actually fails (WAL-pragma removal, integrity-check deferral, `ANALYZE`, Savings snapshot). Skip
   the rest. Keep flyer sync sequential.

## Code tasks completed (2–8, verified green on Windows)

All landed test-first, each its own commit, full suite green before each:
2 case-insensitive item index · 3 bounded item search · 4 bounded receipt summary/month spend ·
5 batched optimizer history · 6 category-scoped swaps · 7 paged deals · 8 caller-owned mapping connections.
