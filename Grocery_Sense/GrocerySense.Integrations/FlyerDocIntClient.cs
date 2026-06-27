namespace GrocerySense.Integrations;

// Port of reference-python/.../integrations/flyer_docint_client.py — Azure layout model for flyers.
public sealed class FlyerDocIntClient
{
    public Task<AzureLayoutResult> AnalyzeLayoutFileAsync(string filePath, CancellationToken ct = default)
        => throw new NotImplementedException("Port from flyer_docint_client.analyze_layout_file");
}

public record AzureLayoutResult(string Text, IReadOnlyList<Dictionary<string, object?>> Tables);
