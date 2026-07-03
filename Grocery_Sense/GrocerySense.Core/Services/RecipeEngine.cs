using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GrocerySense.Core;

// Port of reference-python/.../recipes/recipe_engine.py — pure, file/embedded-backed recipe catalog with
// ingredient+profile filtering. Default source is the recipes.json embedded in this assembly (so the engine
// stays MAUI-free and testable); tests point it at a file fixture instead.
//
// Deviation from Python: Python exposes module-level load/filter/get functions that delegate to a hidden
// singleton (and MealSuggestionService accidentally uses that singleton instead of its injected engine).
// Here the engine is a plain instance that callers inject and use directly — no global, no singleton bug.

// A recipe, normalized on load: name trimmed, blank ingredients dropped, tags trimmed + lowercased.
public sealed record Recipe(
    int? Id, string Name, int? Servings,
    IReadOnlyList<string> Ingredients, IReadOnlyList<string> Steps, IReadOnlyList<string> Tags);

public sealed class RecipeEngine
{
    private readonly string? _recipesPath; // null => embedded resource
    private IReadOnlyList<Recipe>? _cache;
    private long _cacheStamp;

    public RecipeEngine(string? recipesPath = null) => _recipesPath = recipesPath;

    public IReadOnlyList<Recipe> LoadAllRecipes(bool forceReload = false)
    {
        // Embedded source is immutable at runtime -> load once. A file source is mtime-invalidated so a
        // runtime edit is picked up without forceReload (mirrors Python).
        var stamp = _recipesPath is null ? 1 : FileStamp(_recipesPath);
        if (_cache is not null && !forceReload && stamp == _cacheStamp)
            return _cache;

        var json = ReadSource();
        if (json is null) { _cache = Array.Empty<Recipe>(); _cacheStamp = 0; return _cache; }

        _cache = Parse(json);
        _cacheStamp = stamp;
        return _cache;
    }

    // Recipes whose ingredients overlap `includeIngredients`, ranked by overlap count (+ small profile
    // bonuses), after hard-profile filtering. Empty include set or no overlap => that recipe is dropped.
    public IReadOnlyList<Recipe> FilterByIngredientsAndProfile(IEnumerable<string> includeIngredients,
        MealProfile? profile = null, int maxResults = 10)
    {
        var recipes = LoadAllRecipes();
        if (recipes.Count == 0) return Array.Empty<Recipe>();

        var include = NormalizeSet(includeIngredients);
        var scored = new List<(double Score, Recipe Recipe)>();

        foreach (var r in recipes)
        {
            if (profile is not null && !SatisfiesProfile(r, profile)) continue;

            var ingredients = NormalizeSet(r.Ingredients);
            var matchCount = include.Count(ingredients.Contains);
            if (matchCount <= 0) continue;

            scored.Add((matchCount + ProfileSmallBonus(r, profile), r));
        }

        // OrderByDescending is a stable sort, so equal-score recipes keep catalog order (matches Python's
        // stable sort); List.Sort would not.
        return scored.OrderByDescending(t => t.Score).Take(maxResults).Select(t => t.Recipe).ToList();
    }

    public Recipe? GetRecipeByName(string name)
    {
        var target = name.Trim().ToLowerInvariant();
        return LoadAllRecipes().FirstOrDefault(r => r.Name.Trim().ToLowerInvariant() == target);
    }

    // ---- profile hard filter / soft bonus (whole-word allergy/avoid match) ----

    // A recipe is rejected if any allergy/avoid term, or a no_<ingredient> restriction, appears as a whole
    // word in its ingredients (whole-word so "nut" doesn't hit "coconut"). no_meat / no_fish are umbrella
    // diet flags, not single-ingredient bans, and are skipped here.
    internal static bool SatisfiesProfile(Recipe recipe, MealProfile profile)
    {
        var text = string.Join(" ", recipe.Ingredients).ToLowerInvariant();

        foreach (var term in NormalizeSet(profile.Allergies).Concat(NormalizeSet(profile.AvoidIngredients)))
            if (WholeWord(term, text)) return false;

        foreach (var restriction in NormalizeSet(profile.Restrictions))
            if (restriction.StartsWith("no_"))
            {
                var term = restriction[3..].Trim();
                if (term.Length > 0 && term is not ("meat" or "fish") && WholeWord(term, text)) return false;
            }

        return true;
    }

    private static double ProfileSmallBonus(Recipe recipe, MealProfile? profile)
    {
        if (profile is null) return 0.0;
        var text = string.Join(" ", recipe.Ingredients).ToLowerInvariant();
        var tags = recipe.Tags.ToHashSet();

        var bonus = 0.0;
        foreach (var meat in NormalizeSet(profile.PreferMeats))
            if (text.Contains(meat)) bonus += 0.2;
        foreach (var tag in NormalizeSet(profile.FavoriteTags))
            if (tags.Contains(tag)) bonus += 0.1;
        return bonus;
    }

    // ---- loading / parsing ----

    private string? ReadSource()
    {
        if (_recipesPath is not null)
            return File.Exists(_recipesPath) ? File.ReadAllText(_recipesPath) : null;

        var asm = typeof(RecipeEngine).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("Recipes.recipes.json"))
            ?? throw new InvalidOperationException("Embedded recipes.json not found in GrocerySense.Core.");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Accepts a bare list or a {"recipes": [...]} wrapper; anything else is a clear error (a bare string must
    // NOT silently become one bogus recipe).
    private static IReadOnlyList<Recipe> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
            array = root;
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("recipes", out var inner)
                 && inner.ValueKind == JsonValueKind.Array)
            array = inner;
        else
            throw new InvalidDataException(
                "recipes.json must be a list of recipes or an object with a 'recipes' list.");

        var dtos = array.Deserialize(RecipeJsonContext.Default.ListRecipeJson) ?? new List<RecipeJson>();
        return dtos.Select(ToRecipe).ToList();
    }

    private static Recipe ToRecipe(RecipeJson j) => new(
        Id: j.Id,
        Name: (j.Name ?? "").Trim(),
        Servings: j.Servings,
        Ingredients: (j.Ingredients ?? new()).Select(i => (i ?? "").Trim()).Where(i => i.Length > 0).ToList(),
        Steps: (j.Steps ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList(),
        Tags: (j.Tags ?? new()).Select(t => (t ?? "").Trim().ToLowerInvariant()).Where(t => t.Length > 0).ToList());

    private static HashSet<string> NormalizeSet(IEnumerable<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim().ToLowerInvariant()).ToHashSet();

    private static bool WholeWord(string term, string text) =>
        term.Length > 0 && Regex.IsMatch(text, $@"\b{Regex.Escape(term)}\b", RegexOptions.IgnoreCase);

    private static long FileStamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; } catch { return 0; }
    }
}

internal sealed class RecipeJson
{
    public int? Id { get; set; }
    public string? Name { get; set; }
    public int? Servings { get; set; }
    public List<string?>? Ingredients { get; set; }
    public List<string?>? Steps { get; set; }
    public List<string?>? Tags { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<RecipeJson>))]
internal sealed partial class RecipeJsonContext : JsonSerializerContext;
