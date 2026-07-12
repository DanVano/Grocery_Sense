using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrocerySense.Core;

// Source-gen metadata for user_config.json (B1). The config Load/Save path runs on every start and holds the
// rate table + allergies, so it must not depend on reflection STJ (breaks under iOS full AOT / Android trim).
// The polymorphic HouseholdMember.Profile (Dictionary<string, object?>) is handled by ProfileDictionaryConverter
// so the generator never has to introspect `object`. Naming + indentation match the old JsonOpts so existing
// files round-trip unchanged.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(UserConfig))]
internal partial class UserConfigJsonContext : JsonSerializerContext
{
}

// Reads/writes the member profile dictionary with only Utf8JsonReader/Writer + JsonDocument — no reflection.
// Read yields JsonElement values (the shape the rest of the code already expects); Write handles both those
// JsonElements (after a round-trip) and the concrete default/edited types (List<string>, Dictionary<string,double>,
// bool, string) that PreferencesService / DefaultMemberProfile produce before the first save.
internal sealed class ProfileDictionaryConverter : JsonConverter<Dictionary<string, object?>>
{
    public override Dictionary<string, object?> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for the member profile.");

        var result = new Dictionary<string, object?>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return result;
            var name = reader.GetString()!;
            reader.Read();
            using var doc = JsonDocument.ParseValue(ref reader);
            result[name] = doc.RootElement.Clone();
        }
        throw new JsonException("Unexpected end of the member profile object.");
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, object?> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, item) in value)
        {
            writer.WritePropertyName(key); // profile keys are literal snake_case tokens — written verbatim
            WriteValue(writer, item);
        }
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? item)
    {
        switch (item)
        {
            case null: writer.WriteNullValue(); break;
            case JsonElement je: je.WriteTo(writer); break;
            case string s: writer.WriteStringValue(s); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case double d: writer.WriteNumberValue(d); break;
            case int i: writer.WriteNumberValue(i); break;
            case IDictionary<string, double> map:
                writer.WriteStartObject();
                foreach (var (k, dv) in map) { writer.WritePropertyName(k); writer.WriteNumberValue(dv); }
                writer.WriteEndObject();
                break;
            case IEnumerable<string> list:
                writer.WriteStartArray();
                foreach (var s in list) writer.WriteStringValue(s);
                writer.WriteEndArray();
                break;
            default:
                throw new JsonException($"Unsupported profile value type: {item.GetType()}");
        }
    }
}
