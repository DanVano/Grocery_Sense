using System.Text.Json;
using GrocerySense.Core;

namespace GrocerySense.Tests;

// The shared Azure-JSON navigation both ingest services used to copy privately. It must behave identically
// on the two representations that actually reach it: JsonElement (from the OCR/layout clients' JsonDocument)
// and plain dict/list (from the test fixtures). One fixture per method, both representations.
public sealed class RawJsonTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void AsDict_reads_both_representations_and_rejects_non_objects()
    {
        Assert.NotNull(RawJson.AsDict(new Dictionary<string, object?> { ["a"] = 1 }));
        Assert.NotNull(RawJson.AsDict(Parse("""{"a":1}""")));

        Assert.Null(RawJson.AsDict(Parse("[1,2]")));   // array, not object
        Assert.Null(RawJson.AsDict("scalar"));
        Assert.Null(RawJson.AsDict(null));
    }

    [Fact]
    public void AsList_reads_both_representations_and_rejects_non_arrays()
    {
        Assert.Equal(2, RawJson.AsList(new List<object?> { 1, 2 })!.Count);
        Assert.Equal(3, RawJson.AsList(Parse("[1,2,3]"))!.Count);

        Assert.Null(RawJson.AsList(Parse("""{"a":1}"""))); // object, not array
        Assert.Null(RawJson.AsList("scalar"));             // a string is enumerable but is never a JSON array
        Assert.Null(RawJson.AsList(null));
    }

    [Fact]
    public void GetProp_navigates_both_representations()
    {
        var dict = new Dictionary<string, object?> { ["outer"] = new Dictionary<string, object?> { ["inner"] = 7 } };
        Assert.Equal(7, RawJson.GetProp(RawJson.GetProp(dict, "outer"), "inner"));

        var je = Parse("""{"outer":{"inner":7}}""");
        Assert.Equal(7.0, RawJson.ToDouble(RawJson.GetProp(RawJson.GetProp(je, "outer"), "inner")));

        Assert.Null(RawJson.GetProp(dict, "missing"));
    }

    [Fact]
    public void Str_coerces_strings_numbers_and_null()
    {
        Assert.Equal("hi", RawJson.Str("hi"));
        Assert.Equal("hi", RawJson.Str(Parse("\"hi\"")));
        Assert.Equal("42", RawJson.Str(42));
        Assert.Equal("", RawJson.Str(null));
        // A JsonElement number stringifies to its raw text, not empty.
        Assert.Equal("42", RawJson.Str(Parse("42")));
    }

    [Fact]
    public void ToDouble_coerces_every_numeric_source_and_rejects_garbage()
    {
        Assert.Equal(1.5, RawJson.ToDouble(1.5));
        Assert.Equal(3.0, RawJson.ToDouble(3));
        Assert.Equal(4.0, RawJson.ToDouble(4L));
        Assert.Equal(2.99, RawJson.ToDouble(Parse("2.99")));       // JsonElement number
        Assert.Equal(2.99, RawJson.ToDouble(Parse("\"2.99\"")));   // JsonElement string that is numeric
        Assert.Equal(5.0, RawJson.ToDouble("5"));                  // plain numeric string

        Assert.Null(RawJson.ToDouble(null));
        Assert.Null(RawJson.ToDouble("not a number"));
        Assert.Null(RawJson.ToDouble(Parse("\"abc\"")));
    }
}
