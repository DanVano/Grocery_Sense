using GrocerySense.Data;
using Microsoft.Extensions.DependencyInjection;

namespace GrocerySense.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGrocerySenseCore(this IServiceCollection services, string databasePath)
    {
        services.AddSingleton(_ => new SqliteConnectionFactory(databasePath));

        // ConfigStore writes user_config.json / deals_cache.json beside the DB in the app-data dir.
        var configDir = Path.GetDirectoryName(databasePath) is { Length: > 0 } d ? d : ".";
        services.AddSingleton(_ => new ConfigStore(configDir));
        services.AddSingleton<PreferencesService>();
        services.AddSingleton<UnitNormalizationService>();
        services.AddSingleton<MultiBuyDealService>();
        services.AddSingleton<IngredientMappingService>();
        // The ONE home for the multibuy -> unit-guess -> map -> normalize chain, shared by flyer sync and
        // manual flyer ingest (V2_FOLLOWUPS §4.22 — they must never drift back into two copies).
        services.AddSingleton<DealEnricher>();
        services.AddSingleton<PriceHistoryService>();
        services.AddSingleton<PriceDropAlertService>();
        services.AddSingleton<WatchlistService>();
        services.AddSingleton<ShoppingListService>();
        services.AddSingleton<ShoppingInsightsService>();
        services.AddSingleton<StapleRestockService>();
        services.AddSingleton<BasketOptimizerService>();
        // P0-3: the ONE gate every paid OCR call runs through. It must be this singleton — the App head
        // constructs a new Azure client per call, so a lock anywhere else serializes nothing.
        services.AddSingleton<OcrGate>();
        // P1-4: the ONE single-flight gate covering scheduler resume, manual sync, and manual flyer import.
        services.AddSingleton<FlyerMutationGate>();
        services.AddSingleton<ReceiptIngestionService>();
        services.AddSingleton<FlyerIngestService>();
        services.AddSingleton<FlyerSyncService>();
        services.AddSingleton(sp =>
        {
            var scheduler = new FlyerSyncScheduler(sp.GetRequiredService<FlyerSyncService>(),
                sp.GetRequiredService<FlyerMutationGate>());
            var alerts = sp.GetRequiredService<PriceDropAlertService>();
            // Phase 8 hook: a sync that actually ran refreshes engine price-drop alerts (the C# analog of
            // Python's on_sync_complete). Both call paths — App resume and the Deals sync button — route
            // through this singleton. A handler failure is disclosed in FlyerSyncResult.Errors, not thrown.
            // ponytail: synchronous handler — local SQLite on a small DB; Task.Run only if it janks the UI.
            scheduler.SyncCompleted += _ => alerts.RefreshEngineAlerts();
            return scheduler;
        });
        services.AddSingleton<BudgetService>();

        // Meal planning (Phase 4). RecipeEngine loads the embedded recipes.json (no path) and merges the
        // user's own recipes (they shadow same-name catalog entries). MealSuggestion resolves the household
        // meal profile when a caller doesn't pass one.
        services.AddSingleton<UserRecipeService>();
        services.AddSingleton(sp => new RecipeEngine(
            extraRecipes: () => sp.GetRequiredService<UserRecipeService>().ListAsRecipes()));
        services.AddSingleton(sp => new MealSuggestionService(
            sp.GetRequiredService<RecipeEngine>(),
            sp.GetRequiredService<PriceHistoryService>(),
            sp.GetRequiredService<SqliteConnectionFactory>(),
            () => sp.GetRequiredService<PreferencesService>().GetMealProfile()));
        services.AddSingleton<WeeklyPlannerService>();
        // V3 Phase 2: quantity-aware plan costing (Smart Week budget claims ride this, not the legacy
        // 1-unit-per-ingredient estimate).
        services.AddSingleton<PlanCostService>();

        // Family meal-picks (Phase 5): names-only members + parent review queue.
        services.AddSingleton<FamilyRequestsService>();

        // DB maintenance (Phase 6): backup + CSV/JSON export.
        services.AddSingleton<DbMaintenanceService>();

        // Post-trip check (food-savings follow-up): recent receipt vs current list/flyers.
        services.AddSingleton<TripReconciliationService>();

        // Single-scan price-alert notification (A7). ScanAlertNotificationService needs an ILocalNotifier —
        // the head binds it (App: #if ANDROID AndroidLocalNotifier #else NoOpLocalNotifier; tests: a fake).
        // PendingNavigationService carries a notification-tap route into Blazor (deep link).
        services.AddSingleton<ScanAlertNotificationService>();
        // Single-scan workflow (ingest + alert pass) shared by every scan entry point (FAB / page / share).
        services.AddSingleton<ScanIngestService>();
        services.AddSingleton<PendingNavigationService>();
        // PendingSharedReceiptsService carries receipt image(s) shared into the app (ACTION_SEND) into Blazor.
        services.AddSingleton<PendingSharedReceiptsService>();

        // Item Manager destructive mutations (merge / line-correction) with the transaction boundary owned in
        // Core rather than a Razor @code block.
        services.AddSingleton<ItemManagerService>();

        return services;
    }
}
