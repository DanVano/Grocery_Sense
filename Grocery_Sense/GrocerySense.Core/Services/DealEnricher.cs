using Microsoft.Data.Sqlite;

namespace GrocerySense.Core;

// The ONE deal-enrichment pipeline (multibuy adjust → observed-unit guess → item mapping → unit
// normalization) shared by flyer sync and manual flyer ingest. Before this module the chain lived as
// two hand-mirrored copies (FlyerSyncService.EnrichDeal / FlyerIngestService.BuildDeal) whose comments
// promised to match — drift between them was a silent pricing bug. Flyers never auto-create items: an
// unmapped title keeps ItemId null, and enrichment never fabricates a price the text doesn't carry.
public sealed class DealEnricher
{
    private readonly IngredientMappingService _mapper;
    private readonly UnitNormalizationService _unitNorm;
    private readonly MultiBuyDealService _multibuy;

    public DealEnricher(IngredientMappingService mapper, UnitNormalizationService unitNorm,
        MultiBuyDealService multibuy)
    {
        _mapper = mapper;
        _unitNorm = unitNorm;
        _multibuy = multibuy;
    }

    public sealed record EnrichedDeal(
        double DealQty, decimal? DealTotal, decimal? UnitPrice, string Unit,
        decimal? NormUnitPrice, string? NormUnit, string? NormNote,
        int? ItemId, double? MappingConfidence);

    // unitPrice/dealTotal are provider-supplied when available (sync) and null for layout extraction
    // (the promo phrase in priceText is then the only price source). Returns null when there is no text
    // to enrich at all — the caller keeps its row untouched rather than stamping fabricated fields.
    public EnrichedDeal? Enrich(SqliteConnection conn, string? title, string? description, string? priceText,
        double? unitPrice, double? dealTotal, SqliteTransaction? tx = null)
    {
        var t = title ?? "";
        var desc = string.IsNullOrEmpty(description) ? t : description!;
        var combined = $"{t} {desc}".Trim();
        if (combined.Length == 0) return null;

        var adj = _multibuy.Adjust($"{t} {priceText ?? ""}".Trim(), quantity: 1.0,
            unitPrice: unitPrice, lineTotal: dealTotal, discount: null);

        var observedUnit = _unitNorm.GuessUnitFromText(combined);
        if (observedUnit == "unknown") observedUnit = "each";

        int? itemId = null;
        double? mapConf = null;
        var normUnitPrice = adj.UnitPrice;
        var normUnit = (string?)observedUnit;
        var normNote = (string?)$"flyer;{adj.DealNote}";

        var m = _mapper.MapToItem(conn, combined, tx);
        if (m.ItemId is not null)
        {
            itemId = m.ItemId;
            mapConf = m.Confidence;
            if (adj.UnitPrice is not null)
            {
                var norm = _unitNorm.Normalize(conn, m.ItemId.Value, adj.UnitPrice.Value, observedUnit, combined, tx);
                normUnitPrice = norm.NormUnitPrice;
                normUnit = norm.NormUnit;
                normNote = $"{norm.Note};{adj.DealNote};flyer";
            }
        }

        return new EnrichedDeal(adj.Quantity, Dec(adj.LineTotal), Dec(adj.UnitPrice), observedUnit,
            Dec(normUnitPrice), normUnit, normNote, itemId, mapConf);
    }

    private static decimal? Dec(double? v) => v is { } x ? (decimal)x : null;
}
