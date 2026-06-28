namespace GrocerySense.Core.Abstractions;

// Layout OCR seam for flyer photos/PDFs (mirrors IReceiptOcrClient). Implemented in Integrations by
// FlyerDocIntClient (Azure prebuilt-layout). Keeps Core ↛ Integrations: FlyerIngestService depends on
// this, never the concrete Azure client. Returns the raw layout JSON as a navigable dictionary.
public interface IFlyerLayoutClient
{
    Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeLayoutFileAsync(
        string filePath, CancellationToken ct = default);
}
