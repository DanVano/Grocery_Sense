# Ponytail Audit — 2026-08-01 (round 2) · external review reconciled 2026-08-02

Second whole-repo over-engineering audit on `refactor/ponytail-audit` (rounds ponytail-1..7 landed
earlier and already took the obvious dead code). Method: 7 parallel area finders + 7 adversarial
verifiers, every claim re-checked with independent greps against the working tree at `21bc6c6`.
51 raw findings → 46 unique confirmed, 2 refuted.

**2026-08-02: a 17-item external review was reconciled item-by-item (13 independent verifications).
4 items duplicated findings below; 5 folded in with scope/estimate corrections (items #6, #12, #13,
#22 revised; #47 added); 2 became product decisions (trip-planning UI, recipe steps); 6 rejected
with evidence — see "Rejected external-review claims".**

**Net if fully applied: ~-800 lines + ~105 KiB assets, 0 dependency changes. No behavior changes
intended anywhere. Up to ~-190 further lines sit behind the trip-UI and recipe-steps decisions.**

## STATUS: EXECUTED 2026-08-02 — all seven phases landed on `refactor/ponytail-audit`

`main..refactor/ponytail-audit`: **119 files changed, +746 / -1452 (net -706 lines)** plus the 105 KiB
font. Every phase is its own commit, each gated on an unpiped `dotnet test` (**560 passing, 0 failed**,
up from 578 pre-audit — the drop is deleted test *cases*, not lost coverage: 17 SafeFloatMoney inline
cases and four NeedsSync tests replaced by one theory over the real RunSync path, plus one new
legacy-config-lift test). Windows head, Android Debug **and Android Release** all build clean.

| Commit | Phase | Items |
|---|---|---|
| `29bde0d` | ponytail-8 | dead production code (#2-#10) |
| `fee2318` | ponytail-9 | dead params/flags/config (#11, #13-#21) |
| `dc3cc3c` | ponytail-10 | consolidations + stdlib (#22-#24, #26-#29) |
| `d14d933` | ponytail-10 | FK cascade (#25) — own commit, landmine-gated |
| `8c1af3a` | ponytail-11 | App head (#30-#33, #47) |
| `7d0e618` | ponytail-12 | test consolidation (#34-#45) |
| `ad16278` | ponytail-13 | build files + MacCatalyst (#1, #46) |
| `4855d76` | ponytail-14 | typed household preferences (#12) |

Decisions taken while executing: **D1 MacCatalyst — done** (reversible; the decision log already called
it droppable). **D2 trip-UI — deferred**, per this doc's own recommendation: the workflow shell has
never run on a device. **D3 recipe steps — kept**, per recommendation (~4 real code lines, and the
editor collecting them implies a planned view).

One deviation worth flagging: #12 shipped **with** a one-time legacy-config lift rather than leaving it
open as a question. Allergies are safety-critical, and silently dropping them on first launch is the
exact failure mode the project's error-handling rules forbid. It is ~30 lines in `ConfigStore.Load`,
marked `ponytail:` for deletion once every device has saved through the new shape, and pinned by a test.

---

## Product decisions needed (Dan)

**D1 — MacCatalyst (item #1, ~-81).** IMPLEMENTATION_NOTES.md:382-384 already says "maccatalyst is
unused and can drop whenever convenient" but parks removal under v3 platform work — pulling it
forward is a schedule decision. Everything else in this audit is unblocked.

**D2 — Trip-planning UI consolidation (~-125, DEFERRED recommendation).** External review flagged
the ShoppingList/​/plan optimize→apply duplication (~50 lines of parallel markup; service layer
already fully shared — both pages call `Optimizer.Optimize(_mode)` +
`ApplyOptimizerPlanToActiveList`). But it picked the wrong survivor: commit 018a5a6 (2026-07-19)
deliberately moved the core trip flow ONTO the List page ("no hop to /plan, zero drawer trips");
/plan survives only as the power path (budget-defer view) and lives in the More drawer. If
consolidating, the defensible direction is the inverse — fold the budget-defer view (~45 lines)
into the List preview and delete Plan.razor (171 lines + nav link), net ~-125. **Hard gate: the
entire workflow shell has never run on a device (V2_FOLLOWUPS §2; "Plan-trip
preview→confirm→Shop-Mode" is explicitly on the on-device checklist). Recommend deferring this
decision until after first Android device verification — don't churn an unverified surface.**
Decision hinges on: does budget-defer deserve its own page, or does it become chips on the List
preview?

**D3 — Recipe.Steps (~-65, KEEP recommendation).** Steps is verifiably write-only app-wide:
RecipeEngine.cs:161 and UserRecipeService.cs:33 fill it, nothing reads it — and RecipeEditDialog
collects steps that nothing displays, which implies a recipe-detail/cook-mode view was intended.
Same shape as the verified ClassifyDeal keep (zero callers, planned UI hook). If cook mode is ever
coming: keep (costs ~10 KB cached once). If never: the coherent cut is bigger than the external
claim — also stop collecting steps in the editor and UserRecipeService (the user_recipes.steps
column stays, append-only ledger). Of the -65, ~62 lines are recipes.json data; leaving the JSON
untouched (the DTO skips unknown keys) makes a later revert ~3 lines, so real code savings are ~4
lines. **Recommend: keep — the lazy default.**

---

## Confirmed findings, ranked by size of cut

Format: `tag — what to cut → replacement [path] (~net lines)`

### Pending decision D1
1. `delete` — net10.0-maccatalyst TFM + entire `Platforms/MacCatalyst/` folder → nothing
   [GrocerySense.App/GrocerySense.App.csproj:11,36] (~-81, mostly plist).

### Production code — dead (zero callers, verified repo-wide incl. .razor and tests)
2. `delete` — FlyerIngestService.SafeFloatMoney + StrictMoney regex + its test block
   (FlyerIngestServiceTests.cs:57-82) + the csproj InternalsVisibleTo comment naming it → nothing
   [GrocerySense.Core/Services/FlyerIngestService.cs:253] (~-40)
3. `delete` — StoresRepo.UpdateStore (Stores.razor:127 comment already warns it clobbers columns;
   zero callers incl. tests) → nothing [GrocerySense.Data/Repositories/StoresRepo.cs:114] (~-22)
4. `delete` — ShoppingListItem record (everything uses ShoppingListRow) → nothing
   [GrocerySense.Data/Domain/Models.cs:76] (~-14) *(found independently by 2 agents)*
5. `delete` — Receipt record (Python-port leftover; readers map ReceiptSummary/ReceiptDetail) →
   nothing [GrocerySense.Data/Domain/Models.cs:34] (~-12)
6. `delete` — **(expanded 08-02)** PriceHistoryService's entire write/create surface:
   GetOrCreateItem (public + private), RecordPriceFromReceipt, RecordManualPrice, Today() — all
   test-only or fully dead; production price writes go through ReceiptsRepo's INSERT INTO prices
   inside the ingest transaction. Tests reseed directly via ItemsRepo.CreateItem +
   PricesRepo.AddPricePoint (~+5-10 lines of local seed helper). Surviving surface: ctor(factory,
   ConfigStore — ClassifyDeal reads the inflation table), GetItemPriceProfile (← Items.razor:162),
   GetBaselinePrices (← MealSuggestionService.cs:51), ClassifyDeal (deliberate keep).
   [GrocerySense.Core/Services/PriceHistoryService.cs:28] (~-30 net; was -10)
7. `delete` — FlyerSyncService.NeedsSync (production routes through RunSyncAsync, which re-implements
   the same freshness check at :92-95) → delete; retarget its ~7 test call sites to the public
   ReadMeta().Success [GrocerySense.Core/Services/FlyerSyncService.cs:66] (~-9)
8. `shrink` — SpendTrendPoint record (field-for-field twin of MonthSpend, 4 rename sites, none
   JSON-serialized) → MonthSpend [GrocerySense.Data/Domain/Models.cs:149] (~-1)
9. `delete` — index.html link to GrocerySense.App.styles.css (no .razor.css files exist,
   EnableDefaultCssItems=false; link 404s) → nothing [GrocerySense.App/wwwroot/index.html:10] (~-1)
10. `delete` — UserSecretsId property (no configuration builder exists anywhere to read it; creds go
    env vars → SecureStorage) → nothing [GrocerySense.Integrations/GrocerySense.Integrations.csproj:7] (~-1)

### Production code — dead flexibility (params/flags/config nobody sets)
11. `yagni` — ShoppingListRepo.BulkAddItems: pass-through foreach with one caller that ignores its
    return → inline the foreach at WeeklyPlannerService.PersistToShoppingList (caller already owns
    the tx) [GrocerySense.Data/Repositories/ShoppingListRepo.cs:108] (~-12)
12. `yagni` — **(superseded & expanded 08-02; was "trim 10 dead DefaultMemberProfile keys", -10)**
    Replace the per-member polymorphic profile dictionaries wholesale with one typed
    household-preferences record on UserConfig; members keep Id/Name/Role only. Verified: only the
    master profile is ever read (PreferencesService.cs:59, Preferences.razor:211/254 — no per-member
    editing UI exists; ConfigStore.cs:99 comment: "Preferences stay the single household profile on
    the master member"; brainstorm 2026-07-02 Q5 explicitly skipped per-member profiles). Deletes:
    ProfileDictionaryConverter (~55), DefaultMemberProfile (~19), member-profile clone/coerce
    branches (~18), PreferencesService Get/NormList/NormWeights/ToDouble/Split coercion (~72),
    Preferences.razor JsonElement branches (~20), optional PreferencesService._cache (~12). Record
    fields = the 6 live key groups (allergies, hard/soft excludes, excluded_proteins,
    preferred_protein_weights, favorite_cuisines). Keep lowercase/trim normalization (config is
    hand-editable). NO config migration pre-ship: STJ skips the legacy members[].profile key; Dan
    re-enters ~6 comma-list fields once on dev devices (a ~25-line legacy-lift shim only if he wants
    them preserved). AOT win: removes `object` polymorphism from the serialization path.
    [GrocerySense.Core/Models/Results.cs:235, UserConfigJsonContext.cs, ConfigStore.cs,
    PreferencesService.cs, Preferences.razor] (~-120)
13. `yagni` — **(expanded 08-02)** WeeklyPlannerService: remove ALL test-only planner switches —
    targetIngredients, recentlyUsedRecipeIds, plannedStoreId (never passed), mapIngredients
    (production always true; the false path is a 2-line if-wrapper), and the
    persistToShoppingList/addedBy build-and-persist mode (zero production callers; Meals.razor:229
    documents the deliberate two-step build → PersistToShoppingList). BuildWeeklyPlanUnderBudget's
    recentlyUsedRecipeIds/mapIngredients fall out in the same edit. Tests: delete
    Build_map_false_leaves_mapping_unset; rewrite 3 build-and-persist tests to the two-step
    production shape. **Scope guard: MealSuggestionService's targetIngredients/recentlyUsedRecipeIds
    params STAY** — direct tests + FamilyRequestsService.cs:107 is a production consumer; the
    external -80..-95 estimate only adds up by wrongly counting that service.
    [GrocerySense.Core/Services/WeeklyPlannerService.cs:24] (~-23 net; was -8)
14. `yagni` — RecipeEngine mtime-stamp cache invalidation (production always loads the embedded
    resource, stamp is constant 1; only its own test consumes the behavior) → cache once; forceReload
    already covers file-fixture tests [GrocerySense.Core/Services/RecipeEngine.cs:167] (~-8)
15. `yagni` — ShoppingListService dead optional params: AddItemsFromText(plannedStoreId, addedBy,
    memberId) + its unreachable SetPlannedStoreId branch, AddSingleItem(addedByMemberId),
    GetActiveItems(storeId), ApplyOptimizerPlanToActiveList(clearFirst) → remove
    [GrocerySense.Core/Services/ShoppingListService.cs:23] (~-7)
16. `yagni` — AzureDocIntEndpointGuard standalone class, one caller, tests exercise it only via the
    client's public API → fold Validate into AzureDocIntClient as a private static method, delete the
    file (trust-boundary logic kept verbatim) [GrocerySense.Integrations/AzureDocIntEndpointGuard.cs] (~-7)
17. `yagni` — UserConfig.City: user-editable Preferences field nothing reads (flyer sync uses
    PostalCode only; worse than dead — implies functionality) → delete field + Normalize line + UI
    round-trip lines; retarget the 2 ConfigStoreTests probes to PostalCode
    [GrocerySense.Core/Models/Results.cs:246] (~-6)
18. `yagni` — ClearPlannedStoreIdsForActiveItems' includeCheckedOff param: only value ever passed is
    false → bake in `AND is_checked_off = 0` [GrocerySense.Data/Repositories/ShoppingListRepo.cs:145] (~-3)
19. `yagni` — AddPricePoint's flyerSourceId param (the prices/flyer_sources path is documented
    retired at PricesRepo.cs:383) → drop param, bind, and column from the INSERT (column stays,
    nullable) [GrocerySense.Data/Repositories/PricesRepo.cs:36] (~-3)
20. `yagni` — IngredientMappingService's `private const bool AutoLearn = true` guarding one if →
    delete const; LearnThreshold's name carries the intent
    [GrocerySense.Core/Services/IngredientMappingService.cs:26] (~-2)
21. `yagni` — InflationRates.WeightedAdjustedAverage's halfLifeDays param (both callers + all tests
    use the default; the HalfLifeDays const tuning knob stays) → use the const directly
    [GrocerySense.Core/Services/InflationRates.cs:69] (~-1)

### Production code — consolidations / stdlib / native (behavior-identical)
22. `yagni` — **(replaced 08-02; was "FlyerSyncService `using static RawJson`", -18)** Type
    IFlyerProvider's return as a fixed 9-field DTO (public sealed record beside the interface in
    Core/Abstractions — dependency direction holds) instead of
    IReadOnlyList<Dictionary<string, object?>>. FlippClient already builds dicts with exactly these
    9 keys; FlyerSyncService.MapDeal immediately unpacks them via private GetVal/GetStr/ToInt/
    ToDouble (~24 lines) — all deleted. EnrichDeal's decimal? boxing becomes direct (double?) casts.
    Kills the phantom "deal_total" key (no producer ever emits it; the fallback always fired). NO
    JsonSerializerContext needed — FlippClient hand-constructs the DTO from JsonDocument navigation,
    nothing serializes it. **FlippClient's Str/Num/RootArray/IsoDateOnly STAY** (SEC-02 input bounds
    on the untrusted backflipp endpoint). RawJson stays the Azure-shape single home; FlyerIngestService
    untouched. Test fakes are compiler-caught signature swaps (~0 net).
    [GrocerySense.Core/Abstractions/IFlyerProvider.cs, FlyerSyncService.cs:214-316,
    GrocerySense.Integrations/FlippClient.cs:80-91] (~-20)
23. `stdlib` — MealSuggestionService.CollectAllIngredients hand-rolls order-preserving dedupe →
    `recipes.SelectMany(r => r.Ingredients).Select(i => i.ToLowerInvariant()).Distinct().ToList()`
    (Distinct preserves first-seen order) [GrocerySense.Core/Services/MealSuggestionService.cs:300] (~-9)
24. `shrink` — PriceDropAlertService.LoadRecentDismissedKeys/LoadOpenKeys duplicate the same
    reader→HashSet loop; LoadOpenKeys' source param has one caller always passing "receipt" → shared
    ReadKeys(cmd) helper, bake in the source filter
    [GrocerySense.Core/Services/PriceDropAlertService.cs:302] (~-8)
25. `native` — ReceiptsRepo.DeleteReceiptRows hand-deletes 5 child tables that all declare
    ON DELETE CASCADE (foreign_keys=ON on every factory connection; FlyersRepo.cs:42 states the house
    rule "do not add redundant child deletes") → single `DELETE FROM receipts WHERE id = $id`
    [GrocerySense.Data/Repositories/ReceiptsRepo.cs:539] (~-8)
    ⚠ Landmine-adjacent (V2_FOLLOWUPS §4 #16/#20/#21 — replace/restore paths). Gate on
    ReceiptReplacementTests + RestoreStagingTests + the no-partial-rows tests.
26. `yagni` — FlyerIngestService + FlyerSyncService each take UnitNormalizationService +
    MultiBuyDealService ctor params solely to `new DealEnricher(...)` → register DealEnricher as a
    singleton, inject it (strengthens the §4 #22 "DealEnricher has ONE home" invariant)
    [GrocerySense.Core/Services/FlyerIngestService.cs:50] (~-3)
27. `stdlib` — RecipeEditDialog Split+Select(Trim)+Where(len>0) chains →
    `Split(sep, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)`
    [GrocerySense.App/Components/Dialogs/RecipeEditDialog.razor:41] (~-2)
28. `shrink` — BudgetService.GetBudgetStatus re-inlines Grade()'s 0.85/1.0 thresholds three lines
    above calling Grade() → `var status = Grade(spend.Total, budget.Value);` (0 lines, kills the
    two-copy drift risk on the cutoffs) [GrocerySense.Core/Services/BudgetService.cs:41] (0)
29. `stdlib` — `Convert.ToHexString(...).ToLowerInvariant()` → `Convert.ToHexStringLower(...)`
    (.NET 9+, AOT-safe) at FlyerIngestService.cs:271 and ReceiptIngestionService.cs:405 (0)

### App head — BusyComponent adoption + shared markup + template residue
30. `shrink` — 12 copy-pasted 3-line loading-spinner blocks across pages/layout → one
    `<Loading />` component with optional label param (Plan.razor uses "Working")
    [GrocerySense.App/Components/Pages/*.razor, MainLayout.razor] (~-19)
31. `shrink` — Family.razor hand-rolls the busy/try/catch/finally guard in PickMealAsync +
    AddQuickItemAsync despite inheriting BusyComponent (its own KeepAsync/RemoveAsync already use
    GuardAsync; `after` runs inside the try so success-only Snackbar survives)
    [GrocerySense.App/Components/Pages/Family.razor:123] (~-16)
32. `shrink` — Savings.razor's private Guard<T> is GuardAsync<T> minus the `where T : class`
    constraint → drop the constraint on BusyComponent (return `default`), delete the copy, rename 10
    call sites [GrocerySense.App/Components/Pages/Savings.razor:231] (~-15)
33. `shrink` — Budget.razor re-declares _busy/_error and hand-rolls the guard instead of inheriting
    BusyComponent like the other 9 stateful pages → @inherits + GuardAsync
    [GrocerySense.App/Components/Pages/Budget.razor:152] (~-12)
47. `delete` — **(added 08-02)** MAUI template residue: OpenSans registration (MauiProgram.cs:13-16
    ConfigureFonts block) + Resources/Fonts/OpenSans-Regular.ttf (104.8 KiB — only source reference
    is the registration itself; the UI is a BlazorWebView, MainPage.xaml hosts no native controls);
    csproj MauiImage/MauiAsset globs (Resources/Images and Resources/Raw don't exist on disk) + the
    MauiFont glob once the ttf is gone; App.xaml (truly empty Application element) → delete XAML,
    remove InitializeComponent() from App.xaml.cs and drop `partial` — **App.xaml.cs itself is NOT
    residue** (keychain reset, share-cache purge, orphan sweep, CreateWindow live there).
    MauiIcon/MauiSplashScreen stay (real files). [GrocerySense.App/MauiProgram.cs:15,
    GrocerySense.App.csproj:52-59, App.xaml] (~-18 + 105 KiB)

### Test suite — consolidation into existing shared fixtures
34. `shrink` — 16 test classes hand-roll the identical temp-dir trio (Guid dir + ctor
    CreateDirectory + try/catch Dispose); 6 also carry a private 6-line WriteFile helper → one
    TempDirTestBase : IDisposable beside TempDb.cs (FlyerSyncTestBase is the in-repo precedent).
    RestoreStagingTests stays custom — its Dispose calls ClearAllPools (§4 #19)
    [GrocerySense.Tests/*] (~-60)
35. `delete` — `using Xunit;` in 55 test files (csproj:24 declares the global using; 3 files already
    omit it) → nothing [GrocerySense.Tests/*] (~-55)
36. `shrink` — SELECT COUNT(*) re-implemented as 4 private helpers + 8 inline CreateCommand blocks →
    one-line TestSeed.Count over the existing TestSeed.ExecScalar (2 sites are MAX(LENGTH()) scalars —
    route through ExecScalar directly) [GrocerySense.Tests/*] (~-28)
37. `shrink` — 4 private OCR/layout fakes re-implement OcrFixtures.FakeOcr's canned-return shape
    (CountingOcr/CountingLayout/FakeLayout/NullLayout) → shared fakes in OcrFixtures with a Calls
    counter (FlyerSyncTestBase.FuncProvider is the precedent) [GrocerySense.Tests/OcrSpendBoundsTests.cs:16] (~-22)
38. `shrink` — OcrSpendBoundsTests.ReceiptRaw rebuilds OcrFixtures.Raw minus the date/total that Raw
    parameterizes; the oversized-merchant test then hand-bolts those two fields back on → use
    OcrFixtures.Raw (its JSON round-trip is the more production-faithful shape per the file's own
    comment) [GrocerySense.Tests/OcrSpendBoundsTests.cs:61] (~-22)
39. `shrink` — 19-field FlyerDeal spelled out at 8 sites across 8 files (3 local builders + 5 inline)
    → one defaulted TestSeed.Deal builder; `with` clauses cover the rare extra field
    [GrocerySense.Tests/*] (~-15)
40. `shrink` — DiResolutionSmokeTests re-implements TestSeed.DaysAgo + AddReceipt verbatim and
    repeats the 4-fake AddSingleton block ×3 → `using static TestSeed` + one private
    CoreServices(dbPath) helper [GrocerySense.Tests/DiResolutionSmokeTests.cs:111] (~-15)
41. `shrink` — SeedActiveFlyerDeal (flyer_batches + flyer_deals INSERT pair) written twice → one
    parameterized TestSeed.SeedActiveFlyerDeal(conn, storeId, title, unitPrice, itemId = null,
    unit = null) [GrocerySense.Tests/TripReconciliationServiceTests.cs:31, FamilyRequestsServiceTests.cs:36] (~-13)
42. `shrink` — recipes_sample.json path constant redeclared in 6 files → one
    Fixtures.RecipesSamplePath [GrocerySense.Tests/*] (~-10)
43. `shrink` — BudgetServiceTests.AddReceipt duplicates AddReceiptOn's 10-line body → one-line
    delegation with ThisMonthDate [GrocerySense.Tests/BudgetServiceTests.cs:17] (~-8)
44. `shrink` — 3 ExtractDealsFromLayout tests build TempDb + the full 6-dependency service (whose
    fake is never invoked) to exercise a static-only parsing method → call
    FlyerIngestService.ExtractDealsFromLayout(layout) directly (already internal;
    InternalsVisibleTo already granted) [GrocerySense.Tests/FlyerIngestServiceTests.cs:107] (~-6)
45. `shrink` — 7 per-file date-string constants re-derive TestSeed.DaysAgo →
    `TestSeed.Today => DaysAgo(0)` + DaysAgo(±n) at the FlyersRepoTests sites (negative-arg DaysAgo
    is existing convention) [GrocerySense.Tests/FlyersRepoTests.cs:9 et al.] (~-5)

### Build files
46. `shrink` — ImplicitUsings/Nullable=enable repeated in all five csprojs → hoist to the existing
    Directory.Build.props PropertyGroup (imports before project body; behavior identical)
    [Directory.Build.props:5] (~-8)

**net: ~-800 lines + 105 KiB, -0 deps** (includes the D1-pending -81; excludes D2/D3).

---

## Refuted / deliberately kept (do not re-flag)

- **PriceHistoryService.ClassifyDeal** — zero production callers is true, but commit 8cf0aee
  (2026-07-11) deliberately invested the I1 inflation-adjusted bands into it per the accepted
  brainstorm, and the 2026-07-18 trim kept it while deleting five siblings. A feature awaiting its
  UI hook, not dead code. Keep. (The external review's -105 PriceHistory estimate only adds up by
  deleting ClassifyDeal's ~67 lines — that portion is rejected; see item #6 for what does go.)
- **OcrSpendBoundsTests.InterlockedMax CAS loop** — `Interlocked.Max` does NOT exist in .NET 9/10
  (unshipped API proposal); the CAS loop is the correct idiom. Keep.
- **FuzzySharp** — examined for replacement; load-bearing at the documented 0.78 ingest threshold
  (V2_FOLLOWUPS §4 #11). All other packages also earn their keep (the external review independently
  reached the same conclusion). No dependency changes.

## Rejected external-review claims (2026-08-02) — do not re-raise without new facts

1. **"Retire write-only receipt/flyer raw-JSON persistence" — REJECTED.** Receipt raw JSON is not
   write-only: ReceiptsRepo.Snapshot (:584) reads it for delete/undo backups, the replace path calls
   DeleteReceiptWithBackup inside the ingest transaction, and RestoreReceiptFromBackup re-inserts it
   (:413-423) — all landmine #16/#20/#21 territory. Flyer raw IS write-only, but SEC-04 deliberately
   made SQLite the sole raw copy of paid Azure output, and it's the only way to re-run parsing for
   the Phase-3 threshold tuning ("pending real data") without re-paying Azure per flyer.
2. **"Narrow FlyerDeal to production-read fields" — REJECTED.** 8 of 19 fields are read as record
   fields in production; norm_unit_price/norm_unit/deal_total are read as columns via raw SQL by six
   services and must flow through the record to be persisted. The only end-to-end-dead fields
   (DealQty, NormNote, MappingConfidence, Confidence) are tested enrichment provenance / the
   confidence data the Phase-3 tuning pass depends on. A read/write record split forks DealEnricher's
   single-home output (landmine #22). The -35..-50 estimate is arithmetically impossible.
3. **"Return JsonElement from Azure clients instead of Dictionary<string, object?>" — REJECTED.**
   The stated AOT motivation is false — the dict is built from JsonDocument with Clone() precisely
   to avoid reflection Deserialize (the B1 comment at AzureDocIntClient.cs:58-64); RawJson's dual
   shape is documented as deliberate so ~50 plain-dict test fixtures can drive the parser. Cost:
   ~16 files, a fixture-strategy migration across 9 test files, and a rewrite of the landmine-#22
   ReceiptDocument navigator — for zero behavior change.
4. **"Replace key=value sync metadata with source-gen JSON" — REJECTED.** The format is an in-code
   documented decision (FlyerSyncService.cs:224-227: "NOT JSON — trivially trim-safe for the AOT
   Android head, no serializer context needed"). Swap saves ~20 lines, adds AOT-trim surface to a
   zero-risk 4-field dev-device ledger, and rewrites 6+ format-coupled test sites. App unshipped ⇒
   no compat problem exists — and also no gain.
5. **"Inline the scan-alert notification wrapper; drop Notified" — REJECTED.** Not a pass-through:
   it owns receipt-scoped alert scanning (the documented §4 backfill-misattribution mitigation),
   notification-body formatting, and notifier fault isolation, with a 130-line isolation test suite
   that inlining would force through full OCR-ingest setup. Notified is the tested deny-path/fault
   observable (4 assertions).
6. **"INSERT…RETURNING instead of the shared last_insert_rowid helper" — REJECTED.** Feasible
   (bundled SQLite 3.50.4 ≥ 3.35) but zero correctness gain: per-operation connections mean no
   interleave risk and the schema has no triggers. Cost: ~26 edit points across 9 files (3 inside
   landmine #16/#20/#21 transactions) to delete one shared 5-line helper. Its cascade half is
   already item #25 (the estimate double-counted it).

Scope corrections applied while folding the accepted items: planner-modes' -80..-95 wrongly counted
MealSuggestionService's filtering (stays; separate production consumer); member-profiles' "migration
required" is moot pre-ship; the flipp DTO replaces (not stacks with) the old RawJson-consolidation
item #22.

## Stale-doc corrections to fold in alongside the code
- IMPLEMENTATION_NOTES.md:53 lists RecordManualPrice as "still live" — grep-contradicted; correct
  when #6 lands.
- ConfigStore.cs:214 "v1 reads oils" comment — stale; dies with #12.
- FlyerIngestService csproj InternalsVisibleTo comment naming SafeFloatMoney — dies with #2.

---

## Implementation plan

Rules for every phase: one commit per phase (`refactor(ponytail-8)`…), `dotnet test
GrocerySense.Tests` run unpiped and green before each commit, no behavior changes — any test that
has to change asserts the same invariant through a public surface. New .md files need `git add -f`
(blanket *.md ignore).

**Phase 0 — decisions (Dan).** D1 MacCatalyst (approve/defer), D2 trip-UI (recommend: defer until
after first on-device run), D3 recipe steps (recommend: keep). Everything below proceeds regardless.

**Phase 1 — `ponytail-8`: dead production code.** Items #2-#10, including the expanded
PriceHistoryService write-surface trim (#6): delete write/create methods, reseed
PriceHistoryServiceTests via ItemsRepo.CreateItem + PricesRepo.AddPricePoint. Retarget NeedsSync
tests to ReadMeta().Success; delete the SafeFloatMoney test block. Risk: near-zero (all zero-caller
or test-only, verified twice).

**Phase 2 — `ponytail-9`: dead params, flags, switches.** Items #11, #13-#21 (#12 moved to its own
phase). #13 now the full planner-switch trim: delete Build_map_false test, rewrite 3
build-and-persist tests to the production two-step shape; MealSuggestionService params untouched.
UserConfig.City (#17) touches Preferences.razor markup — verify the page in the running Windows
head. Risk: low; compiler catches missed callers.

**Phase 3 — `ponytail-10`: Core/Data/Integrations consolidations + stdlib swaps.** Items #22-#29.
#22 is now the IFlyerProvider typed DTO (replaces the RawJson consolidation — same helper lines die,
plus the stringly-typed seam that produced them; slightly wider diff: Core + Integrations + Tests,
all compiler-caught). ⚠ #25 (DeleteReceiptRows → FK cascade) stays the one landmine-adjacent change:
own commit inside this phase, gated explicitly on ReceiptReplacementTests, RestoreStagingTests, and
the ingest no-partial-rows tests. #26 (DealEnricher DI) composes with #22.

**Phase 4 — `ponytail-11`: App head.** Items #30-#33 + #47. Order: drop the GuardAsync constraint
(#32), migrate Savings, Family (#31), Budget (#33), shared Loading component (#30), then the MAUI
residue (#47). #47 touches csproj + the Application subclass → build BOTH the Windows head and the
Android TFM after it (§4 #23). Verify pages in the running Windows head (spinners, error banners).

**Phase 5 — `ponytail-12`: test-suite consolidation.** Items #34-#45. Biggest file count, zero
production risk. #35 (using Xunit) as one mechanical sweep. TempDirTestBase must NOT absorb
RestoreStagingTests (§4 #19). Green test run is the whole verification.

**Phase 6 — `ponytail-13`: build files (+ MacCatalyst if D1 approved).** Item #46, plus #1. After
csproj edits: build the Windows head AND the Android TFM (§4 #23):
`dotnet build GrocerySense.App -f net10.0-android -p:AndroidSdkDirectory=... -p:JavaSdkDirectory=...`
Fold in the stale-doc corrections above.

**Phase 7 — `ponytail-14`: household-preferences typed record (#12).** The one structural change —
its own phase, last, so everything else lands regardless of how review goes. New typed record +
UserConfigJsonContext registration; delete converter/defaults/clone/coercion; rewrite ConfigStore
round-trip + PreferencesService + BasketOptimizer/FamilyRequests test setups (roughly line-neutral);
keep normalization in Compute. Gates: full test suite, Windows head, AND the Android TFM build —
this touches the AOT-sensitive serialization path (net AOT win: `object` polymorphism leaves it).
Ask Dan first whether dev-device profiles need the ~25-line legacy-lift shim or hand re-entry is fine.

Order constraint: Phase 1 before Phase 5 (deletions shrink the test files the consolidation
touches); Phase 7 last. 2→3→4→6 can shuffle if UI verification time is scarce.
