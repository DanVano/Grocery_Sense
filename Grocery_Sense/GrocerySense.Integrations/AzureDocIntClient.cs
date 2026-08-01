using System.Text.Json;
using Azure;
using Azure.AI.DocumentIntelligence;
using GrocerySense.Core.Abstractions;

namespace GrocerySense.Integrations;

// External Document Intelligence OCR only; DB writes live in Core's ingestion services. One client for
// both paid surfaces — receipt (prebuilt-receipt) and flyer layout (prebuilt-layout) differ only in
// model id and page cap. Network/cred-gated and not unit-testable offline (the endpoint guard is —
// see AzureDocIntClientSecurityTests).
public sealed class AzureDocIntClient : IReceiptOcrClient, IFlyerLayoutClient
{
    private const string Locale = "en-US";

    private readonly string? _endpoint;
    private readonly string? _apiKey;

    public AzureDocIntClient(string? endpoint = null, string? apiKey = null)
    {
        _endpoint = endpoint;
        _apiKey = apiKey;
    }

    // Pages = "1": a receipt is one page; a multipage TIFF must not bill every page (P0-3).
    public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeReceiptFileAsync(
        string filePath, CancellationToken ct = default) =>
        AnalyzeAsync("prebuilt-receipt", "1", filePath, ct);

    // Pages = "1-10": acknowledged ceiling — one flyer import (≤10 files) can bill up to 100 pages,
    // never more (P0-3). A longer PDF simply has pages 11+ ignored.
    public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeLayoutFileAsync(
        string filePath, CancellationToken ct = default) =>
        AnalyzeAsync("prebuilt-layout", "1-10", filePath, ct);

    private async Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeAsync(
        string modelId, string pages, string filePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_endpoint) || string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException(
                "Azure DocumentIntelligence endpoint/apiKey are not configured. Supply them from the App composition root.");
        AzureDocIntEndpointGuard.Validate(_endpoint);

        var client = new DocumentIntelligenceClient(new Uri(_endpoint), new AzureKeyCredential(_apiKey));

        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var options = new AnalyzeDocumentOptions(modelId, BinaryData.FromBytes(bytes))
        {
            Locale = Locale,
            Pages = pages,
        };
        var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, options, ct);

        // Use the raw response JSON (REST field shape) rather than the typed model, so the dict matches what
        // the ingestion services navigate. The operation result is { status, analyzeResult: {...} }.
        using var doc = JsonDocument.Parse(operation.GetRawResponse().Content);
        var analyzeResult = doc.RootElement.TryGetProperty("analyzeResult", out var ar) ? ar : doc.RootElement;
        // Build the loosely-typed dict directly from the JsonDocument (values = cloned JsonElement, detached
        // from the disposed doc). Avoids reflection-based Deserialize<Dictionary<string,object?>>, which the
        // downstream re-serialize + iOS full AOT can't rely on (B1).
        var rawJson = new Dictionary<string, object?>();
        if (analyzeResult.ValueKind == JsonValueKind.Object)
            foreach (var p in analyzeResult.EnumerateObject())
                rawJson[p.Name] = p.Value.Clone();

        string operationId;
        try { operationId = operation.Id; } catch { operationId = Guid.NewGuid().ToString("N"); }
        return (operationId, rawJson);
    }
}
