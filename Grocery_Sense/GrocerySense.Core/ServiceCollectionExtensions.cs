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
        services.AddSingleton<PriceHistoryService>();
        services.AddSingleton<PriceDropAlertService>();
        services.AddSingleton<WatchlistService>();
        services.AddSingleton<ShoppingListService>();
        services.AddSingleton<ShoppingInsightsService>();
        services.AddSingleton<PlanningService>();
        services.AddSingleton<BasketOptimizerService>();
        services.AddSingleton<ReceiptIngestionService>();
        services.AddSingleton<FlyerIngestService>();
        services.AddSingleton<FlyerSyncService>();
        services.AddSingleton(sp =>
        {
            var scheduler = new FlyerSyncScheduler(sp.GetRequiredService<FlyerSyncService>());
            var alerts = sp.GetRequiredService<PriceDropAlertService>();
            // Phase 8 hook: a sync that actually ran refreshes engine price-drop alerts (the C# analog of
            // Python's on_sync_complete). Both call paths — App resume and the Deals sync button — route
            // through this singleton. A handler failure is disclosed in FlyerSyncResult.Errors, not thrown.
            // ponytail: synchronous handler — local SQLite on a small DB; Task.Run only if it janks the UI.
            scheduler.SyncCompleted += _ => alerts.RefreshEngineAlerts();
            return scheduler;
        });
        services.AddSingleton<BudgetService>();

        // Meal planning (Phase 4). RecipeEngine loads the embedded recipes.json (no path). MealSuggestion
        // resolves the household meal profile when a caller doesn't pass one.
        services.AddSingleton(_ => new RecipeEngine());
        services.AddSingleton(sp => new MealSuggestionService(
            sp.GetRequiredService<RecipeEngine>(),
            sp.GetRequiredService<PriceHistoryService>(),
            sp.GetRequiredService<SqliteConnectionFactory>(),
            () => sp.GetRequiredService<PreferencesService>().GetMealProfile()));
        services.AddSingleton<WeeklyPlannerService>();

        // Family meal-picks (Phase 5): names-only members + parent review queue.
        services.AddSingleton<FamilyRequestsService>();

        // DB maintenance (Phase 6): backup + CSV/JSON export.
        services.AddSingleton<DbMaintenanceService>();

        return services;
    }
}
