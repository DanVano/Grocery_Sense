# Grocery Sense — project conventions

.NET 10 / C# / MAUI Blazor Hybrid (MudBlazor) grocery-savings app: receipt OCR → price
intelligence → smart shopping list / deals / trip plan / budget. C# port of a Python original.
**Client-only with no first-party backend** — not "local-only": selected receipt/flyer images go to
Azure Document Intelligence and the postal code goes to Flipp (the two disclosed egress points). No
accounts. CAD/en-CA. **Product targets: Android + iOS ONLY (2026-07-11 decision) —
the Windows head is a dev-only harness, not a shipping target.** v1 + v2 feature code done; the v2
release is blocked on user-side hand-offs (Android toolchain, keystore) — current status: `../V2_FOLLOWUPS.md`.

## Docs (one level up — code repo is `Grocery_Sense/`, planning lives in `Grocery_Sense_Main/`)
- `../V2_FOLLOWUPS.md` — what's left + known gaps; **§4 bug-fixing landmines — read before touching merge/backfill/export/alert code.**
- `../V2_PLAN.md` (v2 phases + decisions) · `../IMPLEMENTATION_NOTES.md` (decision log) · `../brainstorms/` (scope rationale).
- `../PORTING.md`, `../CONTRACT_AUDIT.md` — historical v1 playbook/ledger; status boxes stale, conventions still binding.
- `../reference-python/` — the retired Python original (+ `ARCHITECTURE.md`), read-only port reference. When porting behavior, match it via fixtures; when redesigning (e.g. BasketOptimizer), the plan doc wins, not the Python.

## Layout
- Five flat projects: `GrocerySense.App` (MAUI Blazor UI), `Core` (services), `Data` (SQLite + repos), `Integrations` (Azure clients), `Tests`.
- **Dependency direction: App → {Core, Data, Integrations}; Core → Data; Integrations → Core.** Interfaces (`IReceiptOcrClient`, `IFlyerProvider`, `IFlyerLayoutClient`) live in `Core/Abstractions/` — Core never references Integrations.
- One service per file in `Core/Services/`; one repo per file in `Data/Repositories/`; UI under `App/Components/{Pages,Dialogs,Layout}`.
- DI: Core registrations in `Core/ServiceCollectionExtensions.AddGrocerySenseCore(dbPath)`; app-level bindings (OCR clients, FlippClient, `AppStartup`) in `App/ServiceCollectionExtensions.AddGrocerySenseServices()`. Everything is a singleton.
- **The Android head is AOT/trimmed: every serialized JSON type goes through a source-gen `JsonSerializerContext`** (pattern: `ReceiptSnapshot.cs`, `RecipeJsonContext`). Reflection-based `JsonSerializer` passes on Windows/tests and breaks on device.
- File-scoped namespaces, nullable enabled, intent comments on non-obvious decisions — match surrounding style.

## Persistence
- **Raw SQLite (`Microsoft.Data.Sqlite`) + hand-written repos. No EF/ORM** — don't introduce one without an explicit conversation.
- Schema = numbered, **append-only migration ledger** in `Data/Database.cs`. Never edit a shipped migration; schema change = new entry. `DatabaseMigrationTests` guard this.
- **Money columns are TEXT round-tripping `decimal` — never REAL.** Floats drop cents; keep `decimal` end-to-end. **SQL `SUM`/`AVG` over money is banned — aggregate in C#.**
- Repos: static classes, signature `(SqliteConnection conn, …, SqliteTransaction? tx = null)` — the caller owns the transaction. Multi-table writes (ingest, merge, plan write-back, migrations) run in ONE transaction and get a no-partial-rows test.
- A new table with an `item_id` column must be added to BOTH `ItemsAdminRepo.ItemIdTables` and the FK-sweep test's list, or `MergeItems` silently orphans its rows.
- DB at `FileSystem.AppDataDirectory/grocery_sense.db`; `ConfigStore` JSON (`user_config.json`) beside it. Preferences live in config JSON, not the DB.
- Migrations run off the UI thread via `Data/AppStartup.cs` (Loading/Ready/Error state machine; lives in GrocerySense.Data so it's unit-testable — the App head only registers it). A broken DB must be visible — surface the error, never retry silently; the MainLayout error shell owns retry/restore.

## Integrations
- Azure Document Intelligence for receipt OCR + flyer layout. Creds resolve env vars → MAUI SecureStorage (set on Preferences page). Never hardcode or log keys; a broken SecureStorage read fails loud, not "not configured".
- `FlippClient` is a real provider on the UNOFFICIAL backflipp endpoints — no key, no contract, Flipp can break it without notice. Failures throw loud (disclosed per store in `FlyerSyncResult.Errors`); manual flyer-photo ingest is the fallback. **Never fabricate deal data** — empty flyer = empty list, error = error.

## Tests
- xUnit, flat `<Thing>Tests.cs` files in `GrocerySense.Tests`. Python-parity fixtures in `Fixtures.cs` / `Fixtures/`.
- **`TempDb` = real temp-file SQLite with the migration ledger applied. Use it; never mock repos or the DB** — pragmas, migrations, and decimal/TEXT round-trip are part of what's under test.
- **Never commit red.** Red exists only mid-task; every commit and phase boundary is `dotnet test` green.

## Build / run
```powershell
dotnet test GrocerySense.Tests                                 # class libs + tests (SDK pinned by ../global.json)
dotnet build GrocerySense.Integrations                         # optional since Tests refs Integrations (dotnet test builds it); still handy for a fast compile check
dotnet build GrocerySense.App -f net10.0-windows10.0.19041.0   # Windows head — DEV-ONLY harness, not a product target (retire as a verification target once the Android head builds); exe under bin\Debug\...\win-x64\
```
- `dotnet build GrocerySense.sln` FAILS on Windows (iOS/macCatalyst heads need a Mac) — build per project/TFM as above.
- Run `dotnet test` unpiped before committing — `dotnet test | tail` in an `&&` chain returns tail's exit code and can commit red.
- Verify features in the running app against seeded receipt/flyer fixtures, not just unit tests.

## Product rules
- Optimizer has **no distance/gas math — cut deliberately, don't reintroduce.** Hybrid gate: item moves store only if ≥10% cheaper; store joins only if it saves ≥$5 combined; greedy up to maxStores (default 3). All three thresholds are user settings.
- Deal/price features degrade honestly when data is thin (no flyer data for a store, short receipt history) — label the gap, never pad with fake numbers.
- **Backfill imports never default a receipt date to today** — no confirmed date = skip the receipt, no write. Wrong dates poison the 6-month price history every intelligence feature depends on. (The single-scan path keeps its mtime/today fallback — that one is fine.)

## Error handling — fail loud, never fake
1. Works correctly with real data. 2. Fails with a clear, actionable error. 3. Visibly degrades (banner/log/annotation). 4. Silent degradation to look "fine" — never.
