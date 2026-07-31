using GrocerySense.Core;
using Xunit;

namespace GrocerySense.Tests;

// The typed receipt-document module: parsing is testable on raw dicts, no OCR fakes, no DB.
// End-to-end ingest behavior stays covered by ReceiptIngestionServiceTests / OcrSpendBoundsTests.
public sealed class ReceiptDocumentTests
{
    private static Dictionary<string, object?> Field(object? value, string kind = "valueString", double conf = 0.9) =>
        new() { [kind] = value, ["confidence"] = conf };

    private static Dictionary<string, object?> Money(double amount) =>
        new() { ["valueCurrency"] = new Dictionary<string, object?> { ["amount"] = amount }, ["confidence"] = 0.9 };

    private static Dictionary<string, object?> Raw(Dictionary<string, object?> fields) => new()
    {
        ["documents"] = new List<object?> { new Dictionary<string, object?> { ["fields"] = fields } },
    };

    private static Dictionary<string, object?> Line(params (string Key, object Field)[] fields)
    {
        var obj = fields.ToDictionary(f => f.Key, f => (object?)f.Field);
        return new Dictionary<string, object?> { ["valueObject"] = obj };
    }

    [Fact]
    public void Header_parses_aliases_truncates_merchant_and_keeps_only_iso_dates()
    {
        var doc = ReceiptDocument.Parse(Raw(new Dictionary<string, object?>
        {
            ["Merchant"] = Field(new string('M', 300)),          // alias name + over-cap merchant
            ["TransactionDate"] = Field("June 1st 2026"),        // not ISO -> dropped, caller decides fallback
            ["Total"] = Money(12.34),
            ["Subtotal"] = Field("$10.99"),                      // currency-string scrubbing
        }), maxMerchantChars: 200);

        Assert.Equal(200, doc.Header.Merchant.Length);
        Assert.Equal("", doc.Header.IsoDate);
        Assert.Equal(12.34, doc.Header.Total);
        Assert.Equal(10.99, doc.Header.Subtotal);
        Assert.NotNull(doc.Header.OverallConfidence);
    }

    [Fact]
    public void Lines_derive_missing_prices_drop_negatives_and_flag_invalid_quantities()
    {
        var doc = ReceiptDocument.Parse(Raw(new Dictionary<string, object?>
        {
            ["Items"] = new Dictionary<string, object?>
            {
                ["valueArray"] = new List<object?>
                {
                    // unit price derived from total / qty
                    Line(("Description", Field("Milk")), ("Quantity", Field(2.0, "valueNumber")), ("TotalPrice", Money(10.00))),
                    // negative total dropped, then re-derived from the unit price
                    Line(("Description", Field("Eggs")), ("UnitPrice", Money(3.00)), ("TotalPrice", Money(-1.00))),
                    // OCR claimed a quantity we refuse -> default 1.0 + disclosed flag
                    Line(("Description", Field("Bread")), ("Quantity", Field("zero", "valueString")), ("UnitPrice", Money(2.50))),
                    // no description -> skipped entirely
                    Line(("Quantity", Field(1.0, "valueNumber"))),
                },
            },
        }), maxMerchantChars: 200);

        var lines = doc.ParseLines(maxLines: 300, maxFieldChars: 500);

        Assert.Equal(3, lines.Count);
        Assert.Equal(5.00, lines[0].UnitPrice);
        Assert.Equal(3.00, lines[1].UnitPrice);
        Assert.Equal(3.00, lines[1].LineTotal); // derived back from unit price after the negative was dropped
        Assert.Equal(1.0, lines[2].Quantity);
        Assert.True(lines[2].QuantityReportedButInvalid);
        Assert.False(lines[0].QuantityReportedButInvalid);
    }

    [Fact]
    public void Line_count_over_the_cap_throws_and_descriptions_truncate_to_the_field_cap()
    {
        var many = Enumerable.Range(0, 4).Select(i =>
            (object?)Line(("Description", Field(new string('D', 600))))).ToList();
        var doc = ReceiptDocument.Parse(Raw(new Dictionary<string, object?>
        {
            ["Items"] = new Dictionary<string, object?> { ["valueArray"] = many },
        }), maxMerchantChars: 200);

        Assert.Throws<InvalidDataException>(() => doc.ParseLines(maxLines: 3, maxFieldChars: 500));

        var lines = doc.ParseLines(maxLines: 300, maxFieldChars: 500);
        Assert.All(lines, l => Assert.Equal(500, l.Description.Length));
    }

    [Fact]
    public void Missing_documents_yield_an_empty_but_honest_document()
    {
        var doc = ReceiptDocument.Parse(new Dictionary<string, object?>(), maxMerchantChars: 200);

        Assert.Equal("", doc.Header.Merchant);
        Assert.Null(doc.Header.Total);
        Assert.Null(doc.Header.OverallConfidence);
        Assert.Empty(doc.ParseLines(maxLines: 300, maxFieldChars: 500));
    }
}
