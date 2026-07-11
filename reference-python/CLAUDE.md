# Grocery Sense — CLAUDE.md

> **READ-ONLY PORT REFERENCE.** This file describes the retired Python/Tkinter prototype, not the active
> project. The product is the C# app in `../Grocery_Sense/` — its `CLAUDE.md` holds the live conventions.
> Do not run, fix, or extend the Python code; it exists only as the porting spec.

## Overview

Desktop grocery shopping optimizer for families. Tracks prices from receipts (Azure Document Intelligence OCR) and store flyers, manages a shared household shopping list, suggests meals from deals and member preferences, and optimizes multi-store trips. Single-user desktop app, local SQLite, no server.

A real flyer provider (Flipp) and a primary/secondary device-sync story are sketched in the schema but not yet implemented — see **Known gaps**.

## Stack

- Runtime: Python 3.x, pure stdlib for the app shell (`tkinter`, `sqlite3`, `pathlib`, `threading`, `json`)
- UI: Tkinter (`ttk`, `ScrolledText`, `messagebox`) — one window file per feature
- Storage: raw `sqlite3` — **no ORM**
- OCR: Azure AI Document Intelligence (`azure-ai-documentintelligence`, `azure-core`), prebuilt-receipt model
- Fuzzy matching: `rapidfuzz` (receipt/flyer text → canonical item)
- HTTP: `requests` (for the future flyer provider)
- Tests: `pytest`
- Deps live in `requirements.txt` only — **no `pyproject.toml`/`setup.py`/packaging**.

## Run

```bash
# GUI (the real app) — Grocery_Sense must be importable, so run with src/ on the path
python -m Grocery_Sense.ui.tk_main

# Backend smoke test (NO GUI) — exercises schema init + a couple of repos
python -m Grocery_Sense.main

# Tests
pytest tests/
```

SQLite DB auto-creates at `src/Grocery_Sense/data/db/grocery_sense.db` on first launch (`initialize_database()` runs from `GrocerySenseApp.__init__`).

## Architecture

Strict top-down layering — never call downward past the next layer, never call upward:

```
ui/  ──▶ services/ ──▶ data/repositories/ ──▶ data/ (connection, schema) ──▶ domain/models.py
                │
                └──▶ integrations/ (Azure OCR, Flipp stub)
                └──▶ config/config_store.py (JSON household profile)
                └──▶ recipes/ (recipe_engine + recipes.json)
```

- **UI calls services only** — never a repository, never the DB. Surface errors with `messagebox.showerror`.
- **Services hold all business logic** — they call repositories and integrations. Mix of plain-function modules and classes; match whichever the file uses.
- **Repositories are CRUD-only**, function-based modules of raw `sqlite3`, one per table-ish concept, named `*_repo.py`. A few (e.g. `FlyersRepo`) are classes — match the file.
- **Domain models are `@dataclass`** — plain data, no methods (`domain/models.py`).
- **Integrations are external-API clients** — called from services, never from UI or repos.

## Paths

```
src/Grocery_Sense/
  data/db/grocery_sense.db            # SQLite store (auto-created; WAL mode)
  config/user_config.json            # household + members + preferences (config_store.py)
  config/deals_cache.json            # file-backed deal cache (cache_get/cache_set, 7-day TTL)
  config/flyer_sync_meta.json        # last-sync timestamp for the throttle
  recipes/recipes.json               # recipe catalog for meal suggestions
<repo root>/.env                     # DOCUMENTINTELLIGENCE_ENDPOINT / DOCUMENTINTELLIGENCE_API_KEY
Notes/                               # personal, git-excluded — DO NOT touch unless explicitly named
```

Config/cache JSON files are written **atomically** (temp file → `fsync` → `replace`) so a crash mid-write never truncates them. Preserve that pattern for any new JSON state.

## Rules

**Layering**
- **UI → services → repositories → data → domain.** UI never touches repos or the DB; services never touch the DB directly; repos never call services (note: `prices_repo` keeps its own price-string parser rather than importing a service, precisely to avoid inverting the layering — keep it that way).
- **Business logic lives in services only.** Repos are CRUD; domain models are inert dataclasses.

**Database & SQL**
- **Raw `sqlite3` only — never add an ORM, never add a web framework.**
- **All access goes through `data/connection.py::get_connection()`.** It sets `row_factory = Row`, `PRAGMA foreign_keys = ON`, enables WAL + `synchronous = NORMAL` on first open, and runs `PRAGMA integrity_check` once per DB path. Don't open `sqlite3.connect` directly.
- **Never format user input into SQL — always `?` placeholders.** No f-string/`%` interpolation of values.
- **Tests redirect the DB via `connection._TEST_DB_PATH`** (set by `conftest.py`). Never hardcode the DB path; never assume the default file in code that tests touch.
- **Chunk large `IN (...)` lists.** `prices_repo` uses `_SQL_PARAM_CHUNK = 900` (under SQLite's `SQLITE_MAX_VARIABLE_NUMBER` floor of 999). Reuse the chunking/`executemany` helpers; don't build unbounded parameter lists.
- **FK cascades are load-bearing** (`ON DELETE CASCADE` for receipts/prices, `SET NULL` for shopping-list links). Don't drop them; foreign keys are enforced at runtime.

**Schema & migrations**
- **Core tables live in `data/schema.py::create_tables`**, all `CREATE TABLE IF NOT EXISTS` (idempotent, safe to re-run). `initialize_database()` calls it then `_migrate()` on every startup.
- **Schema is intentionally split** — not everything is in `schema.py`. Feature-owned tables/columns self-create on first use via an `ensure_schema()` guarded by a module-level "ready" cache **keyed by DB path**:
  - `UnitNormalizationService.ensure_schema()` → `items.default_unit`, `prices.norm_unit_price/norm_unit/norm_note`
  - `FlyersRepo` → `flyer_batches`, `flyer_assets`, `flyer_raw_json`, `flyer_deals`
  - `azure_docint_client` → `receipt_file_hashes`, `receipt_signatures`, `receipt_raw_json` (dedupe tables)
- **Migrations must be additive and idempotent.** Column adds key off `PRAGMA table_info`; structural rebuilds (`_migrate_receipt_support_tables`, `_migrate_flyer_deals_item_id_to_integer`) copy into a `__new` table with FKs off, then swap. **The DB is the user's real data — never drop-and-recreate without preserving rows.**
- **New table → decide deliberately:** broadly-shared/core → `schema.py`; feature-local → `ensure_schema()` with a path-keyed ready-cache **and** a test reset hook (see `_reset_schema_cache_for_tests`, `reset_integrity_cache`).

**Units & prices**
- **`unit_normalization_service` is the only place for unit math.** Never write ad-hoc kg/lb/g/mL conversions. It normalizes to a base per dimension (weight→kg, volume→L, count→each, dozen↔each) and records `norm_unit_price`/`norm_unit`/`norm_note`.
- **Prices are stored both raw and normalized.** When comparing across stores/units, compare the normalized columns, not `unit_price`.
- **Multi-buy parsing belongs in `multibuy_deal_service`** ("2/$5", "3 for 10", "BOGO" → effective unit price). Don't re-parse deal strings inline.

**Config & preferences**
- **`config/config_store.py` owns `user_config.json`.** Read via `load_config()`, write via `save_config()` — both are `RLock`-guarded, mtime/size-cached, and write atomically. `save_config` invalidates the `preferences_service` effective-profile cache; don't bypass it by editing the JSON directly.
- **Household baseline == MASTER member profile.** Secondary members store only overrides + allergies.
- **Allergies are ALWAYS hard exclusions, household-wide**, regardless of member role.
- **Secondary members' `hard_excludes` auto-downgrade to `soft_excludes`** in `ensure_member_profile_defaults` — only the master can hard-exclude by preference. Don't re-introduce hard excludes for secondaries.
- **`PROFILE_VERSION` (currently 3) is bumped on load**, with legacy-shape migration in `_from_raw_config`. Add migrations there, not in callers.

**Integrations & secrets**
- **No hardcoded secrets.** Azure creds come from `.env` (auto-loaded once by `azure_docint_client._load_dotenv_once`, walking up from the source file) or env vars `DOCUMENTINTELLIGENCE_ENDPOINT` / `DOCUMENTINTELLIGENCE_API_KEY`. Missing creds must raise a clear error, never silently no-op.
- **Receipt ingest dedupes before spending an Azure call:** (1) file-hash dedupe (no API call), (2) signature dedupe (merchant+date+total, catches rescans). Default is "don't re-insert"; `replace_existing=True` is the only path that deletes + re-ingests.
- **`FlippClient` is a stub returning `[]`.** Flyer sync is wired end-to-end but produces no real deals until a provider is implemented — don't fake deal data to make output look populated.

**Concurrency & UI threading**
- **Tkinter runs on the main thread.** Long work (flyer sync, price-drop alert check, receipt import) runs on `daemon` threads; marshal results back with `widget.after(...)`. Defer background starts until widgets are realized (see the `after(500, ...)` / `after(1500, ...)` in `tk_main`).
- **`FlyerSyncScheduler` uses a `threading.Timer`** polling hourly; `start()` on launch, `request_sync()` for the manual button, `stop()` wired to `WM_DELETE_WINDOW`. Cancel timers on close — don't leave daemon timers dangling.

**Code hygiene**
- **Match the file's existing style** — function-module vs class service, type-hint density, `StringVar` usage, and whether a repo function takes a `conn` or calls `get_connection()` itself.
- **Don't touch unrelated files.** No docstrings/comments/refactors on code you didn't change; no drive-by cleanups in the same PR.
- **No one-off helper utilities** for a single call site.
- **Validate only at boundaries** (user input, Azure responses, file uploads). Don't add defensive checks for conditions an internal guarantee already covers.
- **Surface errors to the user via `messagebox.showerror`, not `print`.**
- **Never touch `Notes/`** unless the user names it explicitly — personal, git-excluded.

**Timings & limits** (grep the constants before changing)
- `SYNC_INTERVAL_DAYS = 3.5` (≈ twice a week); scheduler poll `_POLL_SECONDS = 3600`
- Deals cache TTL: `max_age_days = 7` (`config_store.cache_get`)
- Protein-preference weights clamp to `0.25 .. 3.0`
- SQL parameter chunk: `900`
- `PROFILE_VERSION = 3`
- Don't change these without a note in the PR.

## Testing / verification

- **`pytest` with real SQLite — never mock the DB.** `conftest.py` has an `autouse` `isolated_db` fixture that points `connection._TEST_DB_PATH` at a per-test `tmp_path` DB, runs `create_tables` + `_migrate`, and clears the integrity + Azure schema caches before and after each test. Use `:memory:` or a tmp file; do not stub `sqlite3`.
- **Tests are grouped by concern** under `tests/`: `persistence/`, `ingestion/`, `planning/`, `price_intelligence/`, `integrations/`, `preferences/`. Put new tests in the matching folder.
- **Don't hit real external services.** Azure/flyer tests `monkeypatch.setenv`/`delenv` the `DOCUMENTINTELLIGENCE_*` vars and feed canned payloads; the `FlippClient` stub already returns `[]`.
- **When changing schema or migrations:** run the full suite (the autouse fixture re-runs `create_tables` + `_migrate` per test), and add a `persistence/` test that opens an old-shape DB and asserts the rebuild preserves rows.
- **When changing unit/price logic:** add cases to `price_intelligence/test_unit_normalization.py` / `test_multibuy_parser.py` — these are the regression guards for the math.

## Known gaps (acknowledge in PR, don't fix on-sight)

- **Flipp provider is a stub.** `FlippClient.fetch_flyers_for_store` returns `[]`; the sync pipeline runs but persists nothing real until a provider is wired (env creds → HTTP → the documented deal-dict schema).
- **Two parallel last-sync stores.** The throttle reads `config/flyer_sync_meta.json`, while the `sync_meta` table + `SyncMeta` dataclass (for the planned primary/secondary device sync) are defined but unused. Don't assume one reflects the other.
- **No single migration ledger.** "Schema version" is implicit — core DDL in `schema.py`, feature DDL self-created by `FlyersRepo`/`azure_docint_client`/`unit_normalization_service`. A fresh table can come from any of those paths.
- **No packaging.** There's no installable package; `src/` must be on `sys.path` (the test `conftest.py` does this; running the GUI requires `src/` importable). `python -m Grocery_Sense.main` is a backend smoke test, **not** the GUI — the app entry is `ui/tk_main.py`.
- **Receipt undo is a JSON snapshot** in `deleted_receipt_backups`, not a transactional restore — deep FK-derived rows are re-ingested, not bit-for-bit restored.

## Error Handling: Fail Loud, Never Fake

- Surface errors. Never swallow them to keep things "working."
- Never substitute placeholder, mock, or fabricated data to make output look successful. If real data is unavailable, fail or clearly mark the output as degraded.
- Fallbacks are allowed only when disclosed (banner, warning log, annotated output).
- Optimize for debuggability over cosmetic stability.

Priority order (highest to lowest):
1. Works correctly with real data
2. Fails with a clear, actionable error
3. Visibly degrades — explicitly signals fallback/partial mode
4. Silently degrades to look "fine" — never do this
