using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GrocerySense.Core;

// The loosely-typed Azure raw-JSON shape, read and written in one place. Values arrive as JsonElement (the
// OCR/layout clients build the dictionaries from a JsonDocument); tests build the same shape from plain
// dict/list literals. Those are the only two representations produced — the navigators support both and
// nothing else. Receipt/flyer field extraction (PickField, ExtractDeals, …) stays in the ingest services;
// this owns only the primitive navigation both of them share.
internal static class RawJson
{
    // --- write half: AOT-safe. Reflection-based JsonSerializer.Serialize over Dictionary<string, object?>
    // breaks under iOS full AOT (B1), so write with Utf8JsonWriter. The result is round-trippable JSON —
    // exact escaping/formatting is not contractual (only stored + re-parsed). ---
    public static string ToJsonString(IReadOnlyDictionary<string, object?> map)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in map)
            {
                writer.WritePropertyName(key);
                if (value is JsonElement element) element.WriteTo(writer);
                else writer.WriteNullValue(); // values from the Azure clients are always JsonElement
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // --- read half: navigate the shape. Both proven representations only (JsonElement | plain dict/list). ---

    public static IReadOnlyDictionary<string, object?>? AsDict(object? o) => o switch
    {
        IReadOnlyDictionary<string, object?> d => d,
        JsonElement je when je.ValueKind == JsonValueKind.Object =>
            je.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value),
        _ => null,
    };

    public static IReadOnlyList<object?>? AsList(object? o) => o switch
    {
        IReadOnlyList<object?> l => l,
        JsonElement je when je.ValueKind == JsonValueKind.Array => je.EnumerateArray().Select(x => (object?)x).ToList(),
        _ => null,
    };

    public static object? GetProp(object? o, string key) => AsDict(o)?.GetValueOrDefault(key);

    public static string Str(object? o) => o switch
    {
        null => "",
        string s => s,
        JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : je.ToString(),
        _ => o.ToString() ?? "",
    };

    public static double? ToDouble(object? o) => o switch
    {
        null => null,
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetDouble(),
        JsonElement je when je.ValueKind == JsonValueKind.String =>
            double.TryParse(je.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null,
        _ => double.TryParse(o.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null,
    };
}
