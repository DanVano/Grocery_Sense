# Grocery Sense (C# / .NET MAUI Blazor Hybrid)

Port of the Python/Tkinter Grocery Sense prototype to a **mobile-only (Android + iOS)** MAUI Blazor
app (MudBlazor). The Windows head exists solely as a local dev harness — it is not a product target.
**Status: v1 + v2 feature code complete** — receipt OCR, price intelligence, shopping list, deals,
trip optimizer, budget, meal planning, family picks, backup/export; 380+ tests green. The v2 release
is blocked on user-side hand-offs (JDK 17 + Android SDK 36, signing keystore, Azure budget cap) and
the physical receipt backfill (50 receipts / last 12 months). **Current status + known gaps: `V2_FOLLOWUPS.md`.**

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
`Data → (none)`, `Core → Data`, `Integrations → Core` (implements the `Core/Abstractions` interfaces),
`App`/`Tests → all`. Core never references Integrations — the OCR/flyer clients bind via
`IReceiptOcrClient` / `IFlyerLayoutClient` / `IFlyerProvider` in the App composition root.

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
dotnet build Grocery_Sense/GrocerySense.App/GrocerySense.App.csproj -f net10.0-windows10.0.19041.0   # dev-only harness — NOT a product target
# net10.0-ios / net10.0-maccatalyst require a Mac build host.
```

`dotnet build GrocerySense.sln` will try every App TFM and **fails on Windows** because iOS/macCatalyst
can't build without a Mac. Build per-TFM as above, or set up a Mac/CI for the Apple heads.

## Docs

- **`V2_FOLLOWUPS.md`** — current status, known gaps, and §4 bug-fixing landmines (read before bug work).
- `V2_PLAN.md` / `IMPLEMENTATION_NOTES.md` — v2 phase plan + running decision log.
- `PORTING.md` / `CONTRACT_AUDIT.md` — historical v1 playbook + Port/Replace/Defer ledger
  (status boxes frozen mid-port; the conventions are still binding).
- `Grocery_Sense/CLAUDE.md` — agent/contributor conventions for the C# solution.
- `reference-python/` (+ its `ARCHITECTURE.md`) — the retired Python prototype, read-only port reference.
- `archive/` — superseded docs (old handoffs), kept for history.

## Azure credentials

OCR creds resolve at App composition: env vars (`GROCERY_SENSE_AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT` /
`_API_KEY`) or MAUI SecureStorage keys set in-app via Preferences → Cloud OCR (Azure). **Do not commit
or hardcode keys.**

> Known deferred issue (intentionally left for a later version): a shipped mobile app cannot safely
> hold a shared Azure key — anyone can extract it and run up the bill. Before public release, route OCR
> through a thin backend you control (app → your endpoint → Azure) and add per-user rate limiting. Fine
> to ignore while developing against your own key.
