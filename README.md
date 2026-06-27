# Grocery Sense (C# / .NET MAUI Blazor Hybrid)

Port of the Python/Tkinter Grocery Sense prototype to a cross-platform iOS/Android app.
**Status: scaffold only.** The solution compiles and the test host runs, but every service/repo
method currently `throw new NotImplementedException(...)` with a pointer to the Python source to port
from. This is the skeleton you fill in — not a working app yet.

## Layout

```
Grocery_Sense_Main/                 # repo root
  README.md
  .gitignore
  reference-python/                 # the Python prototype, copied in as the port spec (READ-ONLY ref)
    src/  tests/  ARCHITECTURE.md  CLAUDE.md  requirements.txt
  Grocery_Sense/                    # the C# solution
    GrocerySense.sln
    GrocerySense.Data/              # SQLite connection, schema/migrations, repos, domain records
    GrocerySense.Integrations/      # Azure OCR + flyer layout clients, Flipp stub (pure API, no deps)
    GrocerySense.Core/              # services: price math, planning, preferences, ingest, config, recipes
    GrocerySense.App/               # MAUI Blazor Hybrid UI + DI composition root
    GrocerySense.Tests/             # xUnit golden tests
```

Project dependency graph (no cycles):
`Data → (none)`, `Integrations → (none)`, `Core → Data + Integrations`, `App`/`Tests → all three`.

Domain records live in **Data** (not Core) so Data stays dependency-free and Core can both call repos
and use their return types without a circular reference — no repo interfaces needed.

## Stack

- .NET 10 (SDK pinned via `global.json`), C#, nullable + implicit usings on.
- **SQLite** via `Microsoft.Data.Sqlite` (raw ADO.NET — no ORM, matches the Python design). Bundled
  `SQLitePCLRaw.bundle_e_sqlite3` ships one consistent SQLite for both iOS and Android.
- **Azure.AI.DocumentIntelligence** for receipt OCR (kept from the prototype).
- **FuzzySharp** as the rapidfuzz replacement for ingredient matching.
- xUnit for tests.

## Build / run

One-time (elevated, only needed to build the MAUI App head): install the per-platform MAUI workloads —
`dotnet workload restore Grocery_Sense/GrocerySense.sln` (or `dotnet workload install maui`). The class
libs + tests build without it.

```powershell
# class libs + tests (fast; net10.0 — no MAUI workload needed)
dotnet test Grocery_Sense/GrocerySense.Tests/GrocerySense.Tests.csproj

# the app — pick a platform head (needs MAUI workloads, see above):
dotnet build Grocery_Sense/GrocerySense.App/GrocerySense.App.csproj -f net10.0-android
dotnet build Grocery_Sense/GrocerySense.App/GrocerySense.App.csproj -f net10.0-windows10.0.19041.0   # local dev on Windows
# net10.0-ios / net10.0-maccatalyst require a Mac build host.
```

`dotnet build GrocerySense.sln` will try every App TFM and **fails on Windows** because iOS/macCatalyst
can't build without a Mac. Build per-TFM as above, or set up a Mac/CI for the Apple heads.

## How to port (the sequence)

Full module/function/schema inventory + rationale: **`reference-python/ARCHITECTURE.md`**. Order
(service-layer inward, not screen-by-screen — the money math is the product):

1. **Golden tests first.** Port `reference-python/tests/price_intelligence/test_unit_normalization.py`
   and `test_multibuy_parser.py` into `GrocerySense.Tests` and assert against the (still-stubbed)
   services. Freezes the math before you touch it.
2. **Data foundation.** `SqliteConnectionFactory` (pragmas) → `Database` as a **numbered migration
   ledger** (fold the Python feature-local DDL in) → the 9 repos with raw SQL + batch chunking.
3. **Price math.** `UnitNormalizationService`, `MultiBuyDealService`, `PriceHistoryService`,
   `IngredientMappingService` (FuzzySharp), `PreferencesService`.
4. **Planning.** `ShoppingListService`, `PlanningService`, `BasketOptimizerService`, `PriceDropAlertService`.
5. **Ingest.** `AzureReceiptOcrClient` (pure) + `ReceiptIngestionService` (dedupe/mapping/DB writes) —
   the Python `azure_docint_client` was one mixed file; it's deliberately split here.
6. **UI.** Start with **5 Blazor routes** (Receipts, Shopping List, Deals, Plan, Preferences), not all
   16 windows. Defer admin screens. Bind pages to services via DI (`ServiceCollectionExtensions.cs`).

## Azure credentials

OCR creds (`DOCUMENTINTELLIGENCE_ENDPOINT` / `_API_KEY`) are read inside `AzureReceiptOcrClient`. For
local dev use `dotnet user-secrets` or a non-committed `appsettings.Development.json`. **Do not commit
keys.**

> Known deferred issue (intentionally left for a later version): a shipped mobile app cannot safely
> hold a shared Azure key — anyone can extract it and run up the bill. Before public release, route OCR
> through a thin backend you control (app → your endpoint → Azure) and add per-user rate limiting. Fine
> to ignore while developing against your own key.
