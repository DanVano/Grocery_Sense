using System.Buffers;
using System.Text;
using System.Text.Json;

namespace GrocerySense.Core;

// AOT-safe serialization of the loosely-typed Azure raw-JSON dictionaries. Their values are always JsonElement
// (the OCR/layout clients build them from a JsonDocument), and reflection-based JsonSerializer.Serialize over
// Dictionary<string, object?> breaks under iOS full AOT (B1). This writes with Utf8JsonWriter instead. The
// result is round-trippable JSON — exact escaping/formatting is not contractual (only stored + re-parsed).
internal static class RawJson
{
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
}
