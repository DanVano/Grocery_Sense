using System.Text.RegularExpressions;

namespace GrocerySense.Core;

// Shared hard-constraint filter for recipes: allergies, avoid-ingredients, and no_<x> restrictions.
// Single source of truth — RecipeEngine.SatisfiesProfile and MealSuggestionService.HasDisallowedIngredients
// (and FamilyRequestsService.PickMeal's safety re-check) all delegate here so a fix lands in one place.
internal static class ProfileFilter
{
    // A recipe violates the profile if any allergy/avoid term — or a no_<ingredient> restriction — appears in
    // its ingredients. Matching is token-based with naive singular/plural folding so a plural allergy like
    // "peanuts" still blocks a compound ingredient like "peanut butter", while whole-word tokenization keeps
    // "nut" from hitting "coconut". Multi-word terms fall back to a whole-word phrase match.
    public static bool Violates(IReadOnlyList<string> ingredients, MealProfile profile)
    {
        var text = string.Join(" ", ingredients).ToLowerInvariant();
        var tokens = Tokenize(text);

        foreach (var term in Norm(profile.Allergies).Concat(Norm(profile.AvoidIngredients)))
            if (Hits(term, text, tokens)) return true;

        foreach (var r in Norm(profile.Restrictions))
            if (r.StartsWith("no_"))
            {
                var term = r[3..].Trim();
                if (term.Length > 0 && term is not ("meat" or "fish") && Hits(term, text, tokens)) return true;
            }
        return false;
    }

    private static bool Hits(string term, string text, HashSet<string> tokens)
    {
        if (term.Length == 0) return false;
        // A multi-word term ("peanut butter") is matched as a whole-word phrase; a single word is matched
        // token-wise with singular/plural folding.
        return term.Contains(' ') ? WholeWord(term, text) : tokens.Contains(Singular(term));
    }

    private static readonly Regex WordChars = new(@"[a-z0-9]+", RegexOptions.Compiled);

    private static HashSet<string> Tokenize(string text)
    {
        var set = new HashSet<string>();
        foreach (Match m in WordChars.Matches(text)) set.Add(Singular(m.Value));
        return set;
    }

    // Naive English singularization — folds a trailing "s"/"es" so plural allergies match singular ingredient
    // words. Applied to BOTH sides, so the folding only needs to be self-consistent, not linguistically right.
    private static string Singular(string w) =>
        w.Length > 4 && w.EndsWith("es") ? w[..^2]
        : w.Length > 3 && w.EndsWith('s') ? w[..^1]
        : w;

    private static IEnumerable<string> Norm(IEnumerable<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim().ToLowerInvariant());

    private static bool WholeWord(string term, string text) =>
        term.Length > 0 && Regex.IsMatch(text, $@"\b{Regex.Escape(term)}\b", RegexOptions.IgnoreCase);
}
