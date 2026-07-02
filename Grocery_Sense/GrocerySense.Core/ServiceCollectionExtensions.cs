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
        services.AddSingleton<PlanningService>();
        services.AddSingleton<BasketOptimizerService>();
        services.AddSingleton<ReceiptIngestionService>();
        services.AddSingleton<FlyerIngestService>();
        services.AddSingleton<FlyerSyncService>();
        services.AddSingleton<FlyerSyncScheduler>();
        services.AddSingleton<BudgetService>();

        return services;
    }
}
