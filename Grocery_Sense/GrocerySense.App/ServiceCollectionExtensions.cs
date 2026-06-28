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

        services.AddSingleton<IReceiptOcrClient, AppReceiptOcrClient>();
        services.AddSingleton<IFlyerProvider, FlippClient>();
        services.AddSingleton<IFlyerLayoutClient, AppFlyerLayoutClient>();

        return services;
    }

    private sealed class AppReceiptOcrClient : IReceiptOcrClient
    {
        public async Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeReceiptFileAsync(
            string filePath, CancellationToken ct = default)
        {
            var (endpoint, apiKey) = await ReadAzureDocIntCredsAsync();
            return await new AzureReceiptOcrClient(endpoint, apiKey).AnalyzeReceiptFileAsync(filePath, ct);
        }
    }

    // Flyer layout OCR uses the same Azure DocumentIntelligence resource as receipts (same creds).
    private sealed class AppFlyerLayoutClient : IFlyerLayoutClient
    {
        public async Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeLayoutFileAsync(
            string filePath, CancellationToken ct = default)
        {
            var (endpoint, apiKey) = await ReadAzureDocIntCredsAsync();
            return await new FlyerDocIntClient(endpoint, apiKey).AnalyzeLayoutFileAsync(filePath, ct);
        }
    }

    private static async Task<(string? Endpoint, string? ApiKey)> ReadAzureDocIntCredsAsync()
    {
        var endpoint = await ReadSettingAsync("azure_docint_endpoint",
            "GROCERY_SENSE_AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT", "AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT");
        var apiKey = await ReadSettingAsync("azure_docint_api_key",
            "GROCERY_SENSE_AZURE_DOCUMENT_INTELLIGENCE_API_KEY", "AZURE_DOCUMENT_INTELLIGENCE_API_KEY",
            "AZURE_DOCUMENT_INTELLIGENCE_KEY");
        return (endpoint, apiKey);
    }

    private static async Task<string?> ReadSettingAsync(string secureKey, params string[] envKeys)
    {
        foreach (var key in envKeys)
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value)
                return value;

        try { return await Microsoft.Maui.Storage.SecureStorage.Default.GetAsync(secureKey); }
        catch { return null; }
    }
}
