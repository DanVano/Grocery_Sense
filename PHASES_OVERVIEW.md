# Grocery Sense — Phase Overview

High-level map of the C# / .NET 10 / MAUI Blazor port. Full detail (checklists,
port-from→port-into tables, verify gates) lives in [PORTING.md](PORTING.md).
v1 scope + rationale: [brainstorms/2026-06-24-grocery-sense-csharp-maui-port.md](brainstorms/2026-06-24-grocery-sense-csharp-maui-port.md).

Port **service-layer inward** (money math is the product, UI is thin). Every
phase ends `dotnet test` green — red exists only mid-task, never at a boundary.

| Phase | Title | One-line scope |
|---|---|---|
| 0 | Setup, runtime upgrade, contract audit, structural fixes | .NET 10/MAUI 10, fix dep direction (interfaces in Core), creds, contract audit ledger. |
| 1 | Test harness & fixtures | Python↔C# fixture parity loaders + DI smoke test. Stays GREEN (math asserts skipped until Phase 3). |
| 2 | Data foundation + ConfigStore | SQLite factory + pragmas, numbered migration ledger, 7 v1 repos (+ batch readers), JSON ConfigStore. |
| 3 | Price math + preferences | UnitNormalization, MultiBuy, IngredientMapping (FuzzySharp), PriceHistory, single-profile Preferences. |
| 4 | Planning | ShoppingList, Planning, **BasketOptimizer (REDESIGN — hybrid gate, no trip penalty)**, PriceDropAlert, Deals, Budget. |
| 5 | Receipt ingest | Azure OCR client (`IReceiptOcrClient`) + ReceiptIngestionService; dedupe + single-transaction ingest. |
| 6 | Flyer pipeline | Flyer DocInt client, FlippClient stub (`IFlyerProvider`), FlyerIngest/Sync (sync-on-resume, no bg timer). |
| 7 | Recipes & meal planning | **DEFERRED to v2.** Spec kept; do NOT build in v1. |
| 8 | UI: 6 Blazor routes | Receipts, List, Deals, Plan, Preferences, Budget (+ Stores setup). MudBlazor, touch-first, async. |
| 9 | Platform glue & release readiness | MediaPicker/FilePicker streams, SecureStorage, (pre-release) OCR backend proxy, Apple heads. |

## v1 scope
**In:** Receipts · List · Deals · Plan · Preferences · Budget + deps + store
management/postal + unit normalization. Android-only, MudBlazor, local-only (no
accounts), CAD/en-CA.

**Deferred to v2:** meal planning (Phase 7), multi-member households, real flyer
provider, OCR backend proxy, push notifications, iOS. **Cut entirely:**
`distance_km` / `gas_cost_per_km` (optimizer redesign — user decides travel).

## Key redesign — BasketOptimizer (Phase 4)
Not a port. Drop the distance×gas trip penalty. **Fewest stores that still
capture meaningful savings** via a hybrid gate: item wants another store only if
**≥10% cheaper** there; a store joins only if its qualifying items save **≥$5**
combined; greedy up to **maxStores (default 3)**. Three thresholds are user
settings; "Fewest stops" vs "Best savings" toggle. Owns 8 golden tests.
