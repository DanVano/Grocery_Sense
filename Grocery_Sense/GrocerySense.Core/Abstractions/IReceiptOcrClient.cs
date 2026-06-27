namespace GrocerySense.Core.Abstractions;

public interface IReceiptOcrClient
{
    Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeReceiptFileAsync(
        string filePath, int maxAttempts = 3, CancellationToken ct = default);
}
