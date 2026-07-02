namespace GrocerySense.Integrations;

internal static class AzureDocIntEndpointGuard
{
    public static void Validate(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Azure DocumentIntelligence endpoint must be an https:// URL.");

        var host = uri.Host;
        var ok = host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase)
                 || host.EndsWith(".api.cognitive.microsoft.com", StringComparison.OrdinalIgnoreCase);
        if (!ok)
            throw new InvalidOperationException(
                "Azure DocumentIntelligence endpoint host must be an Azure Cognitive Services domain.");
    }
}
