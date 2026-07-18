using GrocerySense.Core;
using GrocerySense.Core.Abstractions;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GrocerySense.Tests;

public sealed class FlyerIngestServiceTests : IDisposable
{
    private readonly string _rawDir = Path.Combine(Path.GetTempPath(), $"gs_flyer_{Guid.NewGuid():N}");
    public FlyerIngestServiceTests() => Directory.CreateDirectory(_rawDir);
    public void Dispose() { try { Directory.Delete(_rawDir, recursive: true); } catch { /* temp */ } }

    // Returns a fixed canned layout regardless of file path (mirrors the receipt FakeOcr).
    private sealed class FakeLayout(Dictionary<string, object?> raw, string op = "op-1") : IFlyerLayoutClient
    {
        public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeLayoutFileAsync(
            string filePath, CancellationToken ct = default) => Task.FromResult((op, raw));
    }

    private FlyerIngestService Build(TempDb db, Dictionary<string, object?> layout) =>
        new(new FakeLayout(layout), db.Factory, new IngredientMappingService(db.Factory),
            new UnitNormalizationService(), new MultiBuyDealService());

    private string WriteAsset(string content)
    {
        var path = Path.Combine(_rawDir, $"{Guid.NewGuid():N}.png");
        File.WriteAllText(path, content);
        return path;
    }

    private static Dictionary<string, object?> Line(string content, double conf) =>
        new() { ["content"] = content, ["confidence"] = conf };

    // The azure_layout_simple.json fixture, as plain dicts/lists (the navigator handles these + JsonElement).
    private static Dictionary<string, object?> CannedLayout() => new()
    {
        ["pages"] = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["lines"] = new List<object?>
                {
                    Line("Chicken Thighs", 0.95), Line("Family Pack", 0.91), Line("$5.99/kg", 0.88),
                    Line("Fresh Apples", 0.96), Line("2/$5", 0.92),
                    Line("Pork Loin Roast", 0.90), Line("3 for 10", 0.89),
                    Line("Header row — no price here", 0.80),
                    Line("Olive Oil 500 ml", 0.87), Line("Was $8.99 Now $4.99", 0.85),
                    Line("Yogurt 750 g", 0.90), Line("2 @ 4.00", 0.86),
                },
            },
        },
    };

    // ---------------- SafeFloatMoney — the "never fabricate a price" guard ----------------

    [Theory]
    [InlineData("$2.99", 2.99)]
    [InlineData("2.99", 2.99)]
    [InlineData(" $ 10.00 ", 10.00)]
    [InlineData("4.99/kg", 4.99)]
    [InlineData("$0.99", 0.99)]
    [InlineData("0.50", 0.50)]
    public void SafeFloatMoney_parses_clean_inputs(string raw, double expected) =>
        Assert.Equal(expected, FlyerIngestService.SafeFloatMoney(raw)!.Value, 2);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("free")]
    [InlineData("--")]
    [InlineData("O.99")]                 // OCR letter-O prefix
    [InlineData("1,25")]                 // EU decimal comma
    [InlineData("Was $4.99 Now $2.99")]  // multi-amount, ambiguous
    [InlineData("price: $3.49")]         // prose prefix
    [InlineData("-5.00")]                // negative
    [InlineData("$")]                    // lone dollar
    [InlineData("$ .99")]                // missing integer part
    public void SafeFloatMoney_rejects_dirty_or_ambiguous(string? raw) =>
        Assert.Null(FlyerIngestService.SafeFloatMoney(raw));

    // ---------------- ExtractPriceText — flyer price forms ----------------

    [Theory]
    [InlineData("Chicken Thighs $5.99", "$5.99")]
    [InlineData("Apples 2/$5", "2/$5")]
    [InlineData("Pork 3 for 10", "3 for 10")]
    [InlineData("Yogurt 2 @ 4.00", "2 @ 4.00")]
    [InlineData("Milk 3.99", "3.99")]
    public void ExtractPriceText_detects_patterns(string text, string expected) =>
        Assert.Equal(expected, FlyerIngestService.ExtractPriceText(text));

    [Theory]
    [InlineData("Header row")]
    [InlineData("No price here")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Sale!")]
    [InlineData("$O.99")]
    public void ExtractPriceText_returns_null_without_a_price(string text) =>
        Assert.Null(FlyerIngestService.ExtractPriceText(text));

    // ---------------- ExtractDealsFromLayout ----------------

    [Fact]
    public void ExtractDealsFromLayout_skips_header_and_finds_price_anchors()
    {
        using var db = new TempDb();
        var svc = Build(db, CannedLayout());
        var deals = svc.ExtractDealsFromLayout(CannedLayout());

        Assert.DoesNotContain("Header row — no price here", deals.Select(d => d.Title));
        var priceTexts = deals.Select(d => d.PriceText).ToList();
        Assert.Contains("$5.99", priceTexts);
        Assert.Contains("2/$5", priceTexts);
        Assert.Contains("3 for 10", priceTexts);
        Assert.Contains("2 @ 4.00", priceTexts);
        Assert.All(deals, d => Assert.False(string.IsNullOrEmpty(d.PriceText)));
    }

    [Fact]
    public void ExtractDealsFromLayout_title_uses_prior_line()
    {
        using var db = new TempDb();
        var svc = Build(db, CannedLayout());
        var layout = new Dictionary<string, object?>
        {
            ["pages"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["lines"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["content"] = "Chicken Thighs" },
                        new Dictionary<string, object?> { ["content"] = "Family Pack" },
                        new Dictionary<string, object?> { ["content"] = "$5.99/kg" },
                    },
                },
            },
        };

        var deal = Assert.Single(svc.ExtractDealsFromLayout(layout));
        Assert.Equal("Family Pack", deal.Title);
        Assert.Contains("Chicken Thighs", deal.Description);
    }

    [Fact]
    public void ExtractDealsFromLayout_handles_empty_and_malformed()
    {
        using var db = new TempDb();
        var svc = Build(db, CannedLayout());

        Assert.Empty(svc.ExtractDealsFromLayout(new()));
        Assert.Empty(svc.ExtractDealsFromLayout(new() { ["pages"] = new List<object?>() }));

        var malformed = new Dictionary<string, object?>
        {
            ["pages"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["lines"] = new List<object?>
                    {
                        null, new Dictionary<string, object?>(),
                        new Dictionary<string, object?> { ["content"] = "" },
                        new Dictionary<string, object?> { ["content"] = "$3.99" },
                    },
                },
            },
        };
        var deal = Assert.Single(svc.ExtractDealsFromLayout(malformed));
        Assert.Equal("$3.99", deal.PriceText);
    }

    // ---------------- IngestAssetsAsync — end-to-end ----------------

    [Fact]
    public async Task IngestAssets_persists_batch_assets_rawjson_and_deals()
    {
        using var db = new TempDb();
        var storeId = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var svc = Build(db, CannedLayout());

        var result = await svc.IngestAssetsAsync(storeId, "2026-06-20", "2026-06-27",
            new[] { WriteAsset("flyer-bytes") }, _rawDir);

        Assert.True(result.FlyerId > 0);
        Assert.Equal(1, result.AssetsCount);
        Assert.Equal(1, result.RawJsonCount);
        Assert.Equal(5, result.DealsCount); // five price anchors in the canned layout

        var repo = new FlyersRepo();
        Assert.Equal(5, repo.ListDealsForFlyer(db.Conn, result.FlyerId).Count);
        // raw JSON dropped to disk so a reprocess doesn't re-pay Azure.
        Assert.NotEmpty(Directory.GetFiles(_rawDir, "*.json"));
    }

    // Split-brain regression (flyer unification): a manually ingested, mapped deal must surface through
    // GetActiveFlyerPricesBatch — the query the optimizer/watchlist/alerts/badges read.
    [Fact]
    public async Task IngestAssets_mapped_deal_reaches_GetActiveFlyerPricesBatch()
    {
        using var db = new TempDb();
        var storeId = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "Apples").Id;
        // Alias keyed by the mapper's own normalization of the combined deal text BuildDeal maps
        // (title + extracted description, which here is "$5.99/kg Fresh Apples").
        var normalized = new IngredientMappingService(db.Factory)
            .MapToItem("Fresh Apples $5.99/kg Fresh Apples").NormalizedInput;
        new ItemAliasesRepo().UpsertAlias(db.Conn, normalized, item, 1.0);
        var svc = Build(db, CannedLayout());

        await svc.IngestAssetsAsync(storeId, null, null, new[] { WriteAsset("flyer-bytes") }, _rawDir);

        var quotes = PricesRepo.GetActiveFlyerPricesBatch(db.Conn, new[] { item }, new[] { storeId });
        var quote = quotes[(item, storeId)];
        Assert.Equal("flyer", quote.Source);
        Assert.Equal(2.50, quote.UnitPrice); // "2/$5" -> $2.50 effective unit price
    }

    [Fact]
    public async Task IngestAssets_requires_store_id()
    {
        using var db = new TempDb();
        var svc = Build(db, CannedLayout());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.IngestAssetsAsync(null, null, null, new[] { WriteAsset("x") }, _rawDir));
    }

    [Fact]
    public async Task IngestAssets_missing_files_yield_empty_batch_not_a_throw()
    {
        using var db = new TempDb();
        var storeId = StoresRepo.CreateStore(db.Conn, "Sobeys").Id;
        var svc = Build(db, CannedLayout());

        var result = await svc.IngestAssetsAsync(storeId, null, null,
            new[] { Path.Combine(_rawDir, "does-not-exist.png") }, _rawDir);

        Assert.True(result.FlyerId > 0);
        Assert.Equal(0, result.AssetsCount);
        Assert.Equal(0, result.DealsCount);
    }

    // The whole flyer write (batch + assets + raw-json + deals) is one transaction: a mid-write failure
    // leaves zero rows. Drive the same sequence the service uses and poison the deal insert with a bad FK.
    [Fact]
    public void Flyer_write_is_atomic_no_partial_rows_after_failure()
    {
        using var db = new TempDb();
        var repo = new FlyersRepo();
        var storeId = repo.UpsertStore(db.Conn, "Mart");

        using (var tx = db.Conn.BeginTransaction())
        {
            var flyerId = repo.CreateFlyerBatch(db.Conn, storeId, "2026-06-20", "2026-06-27", tx: tx);
            repo.AddAsset(db.Conn, flyerId, "image", "/tmp/f.png", "sha", tx);
            // deal pointing at a non-existent flyer_id -> FK violation on flyer_deals.flyer_id.
            var poisoned = new FlyerDeal(0, 999999, null, storeId, 0, "X", null, "$1", null, 1m, 1m, "each",
                null, null, null, null, null, null, null);
            Assert.ThrowsAny<SqliteException>(() => repo.AddDeals(db.Conn, new[] { poisoned }, tx));
            tx.Rollback();
        }

        Assert.Equal(0L, ScalarCount(db.Conn, "flyer_batches"));
        Assert.Equal(0L, ScalarCount(db.Conn, "flyer_assets"));
        Assert.Equal(0L, ScalarCount(db.Conn, "flyer_deals"));
    }

    private static long ScalarCount(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }
}
