namespace GrocerySense.Core;

// Port of reference-python/.../services/ingredient_mapping_service.py — noisy text -> canonical item_id.
// Strategy: normalize -> alias cache (exact) -> fuzzy (FuzzySharp, port of rapidfuzz token_sort_ratio).
// accept_threshold 0.78, learn_threshold 0.90. FlushLearnedAliases batches writes after an ingest loop.
public sealed class IngredientMappingService
{
    public MappingResult MapToItem(string rawText) => throw new NotImplementedException();

    public void FlushLearnedAliases() => throw new NotImplementedException();

    public void InvalidateChoices() => throw new NotImplementedException();
}
