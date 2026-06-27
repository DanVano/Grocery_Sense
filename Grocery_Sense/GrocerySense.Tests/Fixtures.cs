using System.Text.Json;

namespace GrocerySense.Tests;

internal static class Fixtures
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<T> Load<T>(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<T>>(stream, Opts)
            ?? throw new InvalidOperationException($"Fixture '{fileName}' deserialized to null.");
    }

    public static IEnumerable<object[]> Rows<T>(string fileName) =>
        Load<T>(fileName).Select(row => new object[] { row! });
}

public sealed record ConvertCase(string Case, double PriceFrom, string From, string To, double? Expected);

public sealed record AliasCase(string? Raw, string Expected);

public sealed record GuessCase(string Text, string Expected);

public sealed record NormalizeCase(string Raw, string Expected);

public sealed record MultiBuyCase(
    string Case, string Desc, double? Quantity, double? UnitPrice, double? LineTotal,
    double? Discount, double? ExpectedUnitPrice, string DealNoteContains, bool Supported, string? Note);

public sealed record AmbiguityCase(
    string Case, string Raw, string[] Canonicals, string ExpectedMethod, string? ExpectedCanonical, string? Note);
