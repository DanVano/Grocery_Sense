namespace GrocerySense.Integrations;

// External OCR only — the "split" half of reference-python/.../integrations/azure_docint_client.py.
// In Python that file ALSO wrote to the DB; ARCHITECTURE.md says keep this layer pure (API in,
// JSON out) and move dedupe/mapping/DB-writes into Core's ReceiptIngestionService.
// Creds come from config (DOCUMENTINTELLIGENCE_ENDPOINT / _API_KEY) — NOT hardcoded.
public sealed class AzureReceiptOcrClient
{
    public AzureReceiptOcrClient(string? endpoint = null, string? apiKey = null, string locale = "en-US")
    {
        // PORT: resolve creds from injected config; construct DocumentIntelligenceClient.
    }

    // Calls the prebuilt-receipt model. Returns (operationId, rawJson).
    public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeReceiptFileAsync(
        string filePath, int maxAttempts = 3, CancellationToken ct = default)
        => throw new NotImplementedException("Port from azure_docint_client.AzureReceiptClient.analyze_receipt_file");
}

public record AzureReceiptResult(string Status, double? Confidence, Dictionary<string, object?> RawJson);
