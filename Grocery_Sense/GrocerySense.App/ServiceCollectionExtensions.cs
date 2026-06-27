using GrocerySense.Core;
using GrocerySense.Core.Abstractions;
using GrocerySense.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace GrocerySense.App;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGrocerySenseServices(this IServiceCollection services)
    {
        var dbPath = Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "grocery_sense.db");
        services.AddGrocerySenseCore(dbPath);

        services.AddSingleton<IReceiptOcrClient>(_ => new AzureReceiptOcrClient());
        services.AddSingleton<IFlyerProvider, FlippClient>();
        services.AddSingleton<FlyerDocIntClient>();

        return services;
    }
}
