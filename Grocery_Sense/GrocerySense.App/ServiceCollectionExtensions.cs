using GrocerySense.App.Services;
using GrocerySense.Core;
using GrocerySense.Core.Abstractions;
using GrocerySense.Data;
using GrocerySense.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace GrocerySense.App;

public static class ServiceCollectionExtensions
{
    internal const string AzureDocIntEndpointKey = "azure_docint_endpoint";
    internal const string AzureDocIntApiKeyKey = "azure_docint_api_key";

    public static IServiceCollection AddGrocerySenseServices(this IServiceCollection services)
    {
        var dbPath = Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "grocery_sense.db");
        services.AddGrocerySenseCore(dbPath);

        services.AddSingleton<IReceiptOcrClient, AppReceiptOcrClient>();
        services.AddSingleton<IFlyerProvider, FlippClient>();
        services.AddSingleton<IFlyerLayoutClient, AppFlyerLayoutClient>();
        services.AddSingleton<AppStartup>();
        services.AddSingleton<Services.ShopModeState>();
        services.AddSingleton<Services.QuickScanService>();

        // ILocalNotifier binding for ScanAlertNotificationService (A7) — the repo's first platform conditional.
        // Android posts a real notification; other heads no-op to false (in-app line still shows).
#if ANDROID
        services.AddSingleton<ILocalNotifier, AndroidLocalNotifier>();
#elif IOS
        services.AddSingleton<ILocalNotifier, IosLocalNotifier>();
#else
        services.AddSingleton<ILocalNotifier, NoOpLocalNotifier>();
#endif

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
        var endpoint = await ReadSettingAsync(AzureDocIntEndpointKey,
            "GROCERY_SENSE_AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT", "AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT");
        var apiKey = await ReadSettingAsync(AzureDocIntApiKeyKey,
            "GROCERY_SENSE_AZURE_DOCUMENT_INTELLIGENCE_API_KEY", "AZURE_DOCUMENT_INTELLIGENCE_API_KEY",
            "AZURE_DOCUMENT_INTELLIGENCE_KEY");
        return (endpoint, apiKey);
    }

    private static async Task<string?> ReadSettingAsync(string secureKey, params string[] envKeys)
    {
        foreach (var key in envKeys)
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value)
                return value;

        // GetAsync returns null when the key is simply absent; it throws only when the store itself is broken
        // (e.g. Keystore-wrapped value undecryptable after a cross-device restore). Fail loud on the latter —
        // masking it as null makes the OCR client report a misleading "not configured".
        try { return await Microsoft.Maui.Storage.SecureStorage.Default.GetAsync(secureKey); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Secure credential store could not be read for '{secureKey}'. If you restored this app onto a new " +
                $"device, re-enter your Azure OCR credentials in Preferences. Underlying error: {ex.Message}", ex);
        }
    }
}
