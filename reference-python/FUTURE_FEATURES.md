# Future Features

Backlog of planned features not yet built. Each entry records the intent, the decisions
already made, and the staged approach so it can be picked up later.

---

## Starter dataset + shared central price database

**Status:** Planned — not started.
**Captured:** 2026-06-23.

### The idea
1. Ship the app pre-populated with a starter database (curated stores + prices) so new
   users don't begin with an empty app. Users build on top of it.
2. Grow a **central, crowdsourced** price database that all users contribute to and read
   from, so coverage improves over time.

### Decisions made
- **This Python/Tkinter app is a prototype.** The real product is a **C# .NET MAUI Blazor
  Hybrid** port (same stack as WinRxUpdate). That stack is what can publish to the Apple
  App Store / Google Play — Tkinter cannot. So "shipping to the stores" is a property of
  the port, not this prototype.
- **Seed contents:** catalog **plus** prices, with prices **labeled as sample** (not
  authoritative).
- **Scope for the prototype:** build the **local seed only**. The central backend is
  roadmap, not built here.

### Key answers
- **Does crowdsourcing need a server?** Yes — a shared DB many clients write to and read
  from needs an always-on backend; no peer-to-peer trick avoids it. But use a **managed
  backend** (Supabase = hosted Postgres + auth + REST, free tier; or Pocketbase = single
  binary over SQLite). Do **not** hand-roll or self-host hardware.
- **Best sequencing?** Seed now (local, zero infra). Backend later, in the C# app, where
  it's also the thing that reaches the stores. The central API is client-agnostic, so
  building it later doesn't lock anything in.
- **The durable artifact** is the seed dataset file (`starter_data.sql`) — plain
  SQLite-compatible SQL that loads unchanged in the C# app. The Python loader is throwaway.

### Staged approach

**Stage 0 — local seed (prototype, build first)**
- Add an `is_sample` flag to the `prices` table (numbered migration; dedicated flag, not a
  reused `source` value — preserves provenance and allows a one-query purge).
- Produce `src/Grocery_Sense/data/seed/starter_data.sql` from the real dev DB: scrub
  receipt/flyer FK links, set `is_sample=1`, dump four tables in FK order — `stores`,
  `items`, `item_aliases` (required for fuzzy matching), `prices`.
- Add a small `load_starter_data_if_needed()`: marker-gated (`config/seed_meta.json`),
  loads only into an empty DB (won't clobber existing users), ensures the normalized price
  columns exist before insert, runs once at startup.
- Minimal UI: `(sample)` badge on sample prices, a "Clear sample data" action, and exclude
  sample prices from price-drop alerts.

**Stage 1 — one-way central sharing (in the C# app, when there are users)**
- Managed backend, anonymous device-token auth + rate limiting.
- Upload unit = a **price observation**:
  `(canonical_item, store_identity, region, date, unit_price, unit, norm_unit_price, confidence)`.
- **Privacy:** upload observations only — never raw receipts or who bought what. App stores
  require a privacy policy for this. `is_sample` rows are not uploaded.
- Geo + recency are load-bearing: every price carries store-location + region + date;
  clients filter to their area and weight by recency.
- Reuse the existing Flipp HTTP-client pattern (`requests`, `raise_for_status`, 429).

**Stage 2 — data quality (only if it grows)**
- Dedup, recency-weighting, flag/vote moderation. The hard 80% — defer until scale demands it.

### Notes / gotchas
- Keep this separate from `demo_seed_service.py`, which generates **random fake** demo data
  behind a dev button. Don't ship that as if it were real (violates "never fake data").
- Normalized price columns (`norm_unit_price` etc.) are added lazily by
  `UnitNormalizationService.ensure_schema()`, not at startup — the seed loader must trigger
  that before inserting prices with `norm_*` values.

### Detailed implementation plan
`~/.claude/plans/i-m-thinking-that-we-atomic-sunbeam.md` (exact files, migration code,
seed-production steps, and verification tests).
