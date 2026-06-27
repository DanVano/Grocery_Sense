using GrocerySense.Core;
using GrocerySense.Core.Abstractions;
using GrocerySense.Data;
using GrocerySense.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace GrocerySense.App;

// Composition root. Registers the connection factory, integration clients, and every service.
// Repos are static (port of Python module-functions) so they are NOT registered — services open a
// connection via SqliteConnectionFactory and pass it to repo methods.
// NOTE: services with constructor dependencies (e.g. ReceiptIngestionService) resolve automatically
// because their dependencies are registered below. As the port adds ctor injection to other services,
// just keep the dependency registered here — no other change needed.
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGrocerySenseServices(this IServiceCollection services)
    {
        // --- Data ---
        services.AddSingleton(_ => new SqliteConnectionFactory(
            Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "grocery_sense.db")));

        // --- Integrations (credential reads wait until clients stop being stubs) ---
        // Bound to Core interfaces so services depend on the seam, not the concrete Azure/Flipp class.
        services.AddSingleton<IReceiptOcrClient>(_ => new AzureReceiptOcrClient());
        services.AddSingleton<IFlyerProvider, FlippClient>();
        services.AddSingleton<FlyerDocIntClient>();

        // --- Core services ---
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
