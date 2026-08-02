using System.Text.Json.Serialization;

namespace GrocerySense.Core;

// Source-gen metadata for user_config.json (B1). The config Load/Save path runs on every start and holds the
// rate table + allergies, so it must not depend on reflection STJ (breaks under iOS full AOT / Android trim).
// Every type in the graph is now concretely typed — no `object` for the generator to introspect and no custom
// converter — since preferences became HouseholdPreferences. Naming + indentation match the old JsonOpts so
// existing files round-trip unchanged.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(UserConfig))]
internal partial class UserConfigJsonContext : JsonSerializerContext
{
}
