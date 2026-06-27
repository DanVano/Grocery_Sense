using GrocerySense.Data;
using Microsoft.Extensions.DependencyInjection;

namespace GrocerySense.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGrocerySenseCore(this IServiceCollection services, string databasePath)
    {
        services.AddSingleton(_ => new SqliteConnectionFactory(databasePath));

        services.AddSingleton<ConfigStore>();
        services.AddSingleton<PreferencesService>();
        services.AddSingleton<UnitNormalizationService>();
        services.AddSingleton<MultiBuyDealService>();
        services.AddSingleton<IngredientMappingService>();
        services.AddSingleton<PriceHistoryService>();
        services.AddSingleton<PriceDropAlertService>();
        services.AddSingleton<DealsService>();
        services.AddSingleton<ShoppingListService>();
        services.AddSingleton<PlanningService>();
        services.AddSingleton<BasketOptimizerService>();
        services.AddSingleton<RecipeEngine>();
        services.AddSingleton<MealSuggestionService>();
        services.AddSingleton<WeeklyPlannerService>();
        services.AddSingleton<ReceiptIngestionService>();
        services.AddSingleton<FlyerIngestService>();
        services.AddSingleton<FlyerSyncService>();
        services.AddSingleton<FlyerSyncScheduler>();
        services.AddSingleton<FamilyRequestsService>();
        services.AddSingleton<ListAuditService>();
        services.AddSingleton<BudgetService>();
        services.AddSingleton<DbMaintenanceService>();
        services.AddSingleton<DemoSeedService>();

        return services;
    }
}
