# Grocery Sense

Grocery Sense is a receipt-scanning grocery app for Android and iOS. You scan your receipts, it
reads the prices, and over time it learns what you normally pay for things. From there it helps you
build a smarter shopping list, catch real flyer deals, plan a trip across a few stores, track a
budget, and plan meals around what's actually on sale.

It's a C# rewrite of an earlier Python/Tkinter version I built. Everything runs on your phone — no
accounts, no sign-in, no server. Your data stays local. Prices are in Canadian dollars (en-CA).

It's built with .NET 10, C#, and a MAUI Blazor Hybrid front end (MudBlazor). Android and iOS are the
only real targets. There is a Windows build, but I only use it as a dev harness to run the tests and
check things quickly. It isn't something I ship.

## What it does

- **Scan receipts.** Snap one receipt, or a whole batch of old ones, and it pulls out the store,
  date, and line items using Azure Document Intelligence.
- **Learn your prices.** It tracks what you usually pay for each item over the last six months and
  flags when something is unusually cheap.
- **Build a shopping list.** Items group by store, tagged as buy now, stock up, or wait, with hints
  for multi-buy deals.
- **Find deals.** It pulls current flyer deals and matches them against your list.
- **Plan a trip.** It works out which few stores are worth visiting, and only splits your trip when
  the savings actually justify the extra stop.
- **Track a budget.** See your spending against a monthly budget, year over year.
- **Plan meals.** Suggestions based on what's cheap right now and what your household likes, with a
  per-serving cost. You can add your own recipes too.
- **Family picks.** Household members can pick meals and items; a parent reviews what makes the list.
- **Back up and export.** Share-sheet backup plus CSV and JSON export. No cloud lock-in.

When the data is thin — a store with no flyer, or a short receipt history — the app says so instead
of inventing numbers. Errors work the same way. If a flyer sync fails, it tells you; it won't quietly
show an empty list as if that were the real answer. The flyer feed itself is an unofficial one, so it
can break without warning. When it does, you can still add a flyer photo by hand.

## Status

All the core features are built and merged to `main`: receipt OCR, price intelligence, the shopping
list, deals, the trip optimizer, budget, meals, family picks, price-drop alerts with local
notifications, and backup/export. 488 tests pass.

The Android head now builds with no errors, using JDK 17 and Android SDK 36. What's left is mostly
on-device and hardware work, not code:

- Generate a release signing keystore, and cap the Azure OCR budget, before the first real scan run.
- Build a signed APK and smoke-test every screen on a real Android phone. Nothing has been driven on
  a device yet. The tests cover the services and the database, not the UI.
- Backfill about 50 paper receipts from the last year. This is the linchpin: most of the smart
  features have nothing to work with until there is real price history behind them.
- iOS needs a Mac with Xcode to build. I want to get to it, but it's parked until I have the hardware.

The full list of what's left lives in `OPEN_ITEMS_0721.md`.

## Stack

- .NET 10 (SDK version pinned in `global.json`), C#, nullable reference types on.
- SQLite through `Microsoft.Data.Sqlite` — raw ADO.NET with hand-written repositories, no ORM.
  `SQLitePCLRaw.bundle_e_sqlite3` ships one consistent SQLite build for both platforms.
- Azure Document Intelligence for receipt and flyer OCR.
- FuzzySharp for matching messy receipt text to known items.
- xUnit for the tests.

One detail worth calling out: money is stored as decimal text, never as a float. Floats drop cents,
and this whole app is about cents adding up, so decimals go end to end.

## Layout

The repo root holds the docs and planning. The C# solution lives in `Grocery_Sense/`.

```
Grocery_Sense_Main/            # repo root (docs and planning live here)
  README.md
  reference-python/            # the old Python version, kept read-only as the port spec
  Grocery_Sense/               # the C# solution
    GrocerySense.Data/         # SQLite, schema/migrations, repos, domain records
    GrocerySense.Integrations/ # Azure OCR + flyer clients, Flipp provider
    GrocerySense.Core/         # services: price math, planning, ingest, config, recipes
    GrocerySense.App/          # MAUI Blazor UI + dependency-injection setup
    GrocerySense.Tests/        # xUnit tests
```

Dependencies only point one way. `Data` depends on nothing, `Core` depends on `Data`, `Integrations`
depends on `Core`, and `App` and `Tests` sit on top of all of it. Core never references Integrations —
the OCR and flyer clients bind through interfaces in the App at startup. Domain records live in
`Data` so that Data stays dependency-free while Core can still use those types.

## Build and run

The class libraries and tests build without any MAUI workloads:

```powershell
dotnet test Grocery_Sense/GrocerySense.Tests/GrocerySense.Tests.csproj
```

To build the app itself you first need the MAUI workloads installed
(`dotnet workload restore Grocery_Sense/GrocerySense.sln`, run elevated). Then build one platform
head at a time:

```powershell
dotnet build Grocery_Sense/GrocerySense.App/GrocerySense.App.csproj -f net10.0-android
dotnet build Grocery_Sense/GrocerySense.App/GrocerySense.App.csproj -f net10.0-windows10.0.19041.0   # dev harness only, not shipped
```

Don't run `dotnet build GrocerySense.sln` on Windows. It tries every platform head, and the iOS and
Mac Catalyst heads can't build without a Mac. Build one head at a time, as above.

## Docs

- `OPEN_ITEMS_0721.md` — the live list of everything still unfinished. Start here.
- `V2_FOLLOWUPS.md` — known limitations and the bug-fixing landmines. §4 is required reading before
  you touch the merge, backfill, export, or alert code.
- `V2_PLAN.md` and `IMPLEMENTATION_NOTES.md` — the feature plan and the running decision log.
- `SECURITY_REVIEW_FUTURE_WORK.md` — standing security notes for a future public release.
- `PORTING.md` and `CONTRACT_AUDIT.md` — the original port playbook and ledger. The status boxes are
  frozen mid-port, but the conventions in them still hold.
- `reference-python/` — the retired Python original, kept as a read-only reference.
- `Grocery_Sense/CLAUDE.md` — conventions for working inside the C# solution.

## Azure credentials

OCR needs an Azure Document Intelligence endpoint and key. The app reads them from environment
variables (`GROCERY_SENSE_AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT` and `_API_KEY`) or from MAUI
SecureStorage, which you set in the app under Preferences → Cloud OCR. Don't commit or hardcode keys.

One limitation I've left on purpose for a later version: a shipped mobile app can't safely carry a
shared Azure key, because anyone can pull it out of the app and run up the bill. Before any public
release, OCR needs to run through a small backend I control (app → my endpoint → Azure), with
per-user rate limiting. It's fine while I'm developing against my own key.
