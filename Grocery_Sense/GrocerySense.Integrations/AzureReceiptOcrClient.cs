using System.Text.Json;
using Azure;
using Azure.AI.DocumentIntelligence;
using GrocerySense.Core.Abstractions;

namespace GrocerySense.Integrations;

// External OCR only; DB writes live in Core's ReceiptIngestionService.
public sealed class AzureReceiptOcrClient : IReceiptOcrClient
{
    private readonly string? _endpoint;
    private readonly string? _apiKey;
    private readonly string _locale;

    public AzureReceiptOcrClient(string? endpoint = null, string? apiKey = null, string locale = "en-US")
    {
        _endpoint = endpoint;
        _apiKey = apiKey;
        _locale = locale;
    }

    public async Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeReceiptFileAsync(
        string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_endpoint) || string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException(
                "Azure DocumentIntelligence endpoint/apiKey are not configured. Supply them from the App composition root.");
        AzureDocIntEndpointGuard.Validate(_endpoint);

        var client = new DocumentIntelligenceClient(new Uri(_endpoint), new AzureKeyCredential(_apiKey));

        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var options = new AnalyzeDocumentOptions("prebuilt-receipt", BinaryData.FromBytes(bytes)) { Locale = _locale };
        var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, options, ct);

        // Use the raw response JSON (REST field shape) rather than the typed model, so the dict matches what
        // ReceiptIngestionService navigates. The operation result is { status, analyzeResult: {...} }.
        using var doc = JsonDocument.Parse(operation.GetRawResponse().Content);
        var analyzeResult = doc.RootElement.TryGetProperty("analyzeResult", out var ar) ? ar : doc.RootElement;
        // Build the loosely-typed dict directly from the JsonDocument (values = cloned JsonElement, detached
        // from the disposed doc). Avoids reflection-based Deserialize<Dictionary<string,object?>>, which the
        // downstream re-serialize + iOS full AOT can't rely on (B1). Same shape ReceiptIngestionService navigates.
        var rawJson = new Dictionary<string, object?>();
        if (analyzeResult.ValueKind == JsonValueKind.Object)
            foreach (var p in analyzeResult.EnumerateObject())
                rawJson[p.Name] = p.Value.Clone();

        string operationId;
        try { operationId = operation.Id; } catch { operationId = Guid.NewGuid().ToString("N"); }
        return (operationId, rawJson);
    }
}
