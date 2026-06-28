namespace GrocerySense.Core.Abstractions;

public interface IReceiptOcrClient
{
    Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeReceiptFileAsync(
        string filePath, CancellationToken ct = default);
}
