using GrocerySense.Core.Abstractions;

namespace GrocerySense.Integrations;

// External OCR only; DB writes live in Core's ReceiptIngestionService.
public sealed class AzureReceiptOcrClient : IReceiptOcrClient
{
    public AzureReceiptOcrClient(string? endpoint = null, string? apiKey = null, string locale = "en-US")
    {
        // PORT: resolve creds from injected config; construct DocumentIntelligenceClient.
    }

    public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeReceiptFileAsync(
        string filePath, int maxAttempts = 3, CancellationToken ct = default)
        => throw new NotImplementedException("Port from azure_docint_client.AzureReceiptClient.analyze_receipt_file");
}

public record AzureReceiptResult(string Status, double? Confidence, Dictionary<string, object?> RawJson);
