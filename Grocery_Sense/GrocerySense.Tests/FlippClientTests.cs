using System.Net;
using System.Net.Http.Headers;
using System.Text;
using GrocerySense.Core.Abstractions;
using GrocerySense.Integrations;

namespace GrocerySense.Tests;

// Canned-JSON tests only — never live HTTP. The backflipp API is unofficial; these tests pin OUR mapping
// and failure behavior, not Flipp's schema.
public sealed class FlippClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(fn(request));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private const string FlyersJson =
        """
        {"flyers":[
            {"id":101,"merchant":"No Frills Ontario","valid_from":"2026-07-09T00:00:00-04:00","valid_to":"2026-07-16T00:00:00-04:00"},
            {"id":202,"merchant":"Walmart","valid_from":"2026-07-09T00:00:00-04:00","valid_to":"2026-07-16T00:00:00-04:00"}
        ]}
        """;

    private const string ItemsJson =
        """
        [
            {"name":"Gala Apples","current_price":"1.99","sale_story":"2/$5","description":"Product of Canada","page":3},
            {"name":"Whole Chicken","current_price":8.99},
            {"name":"","current_price":2.00}
        ]
        """;

    private static (FlippClient Client, FakeHandler Handler) Build(Func<HttpRequestMessage, HttpResponseMessage> fn)
    {
        var handler = new FakeHandler(fn);
        return (new FlippClient(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task Maps_matching_merchant_flyer_items_to_provider_deals()
    {
        var (client, handler) = Build(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/flyer_items") ? Json(ItemsJson) : Json(FlyersJson));

        var deals = await client.FetchFlyersForStoreAsync("No Frills", "M5V 1A1");

        Assert.Equal(2, deals.Count); // blank-name item dropped
        var apples = deals[0];
        Assert.Equal("Gala Apples", apples.Title);
        Assert.Equal("2/$5", apples.PriceText);            // promo phrase wins over the plain price
        Assert.Equal(1.99, apples.UnitPrice!.Value, 4);     // string price parsed
        Assert.Equal("Product of Canada", apples.Description);
        Assert.Equal("2026-07-09", apples.ValidFrom);      // flyer window, date-only
        Assert.Equal("2026-07-16", apples.ValidTo);
        Assert.Equal(3, apples.PageIndex);

        var chicken = deals[1];
        Assert.Equal("$8.99", chicken.PriceText); // no promo phrase -> plain price text

        // Only flyer 101 (No Frills) was fetched; Walmart's 202 was filtered out.
        Assert.Equal(2, handler.Urls.Count);
        Assert.Contains("/flyers/101/flyer_items", handler.Urls[1]);

        // Postal code is normalized (spaces stripped, uppercased) into the flyers query.
        Assert.Contains("postal_code=M5V1A1", handler.Urls[0]);
    }

    [Fact]
    public async Task Bare_array_flyers_root_is_accepted()
    {
        var (client, _) = Build(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/flyer_items")
                ? Json(ItemsJson)
                : Json("""[{"id":7,"merchant":"NoFrills","valid_from":"2026-07-09","valid_to":"2026-07-16"}]"""));

        var deals = await client.FetchFlyersForStoreAsync("No Frills", "M5V");
        Assert.Equal(2, deals.Count);
    }

    [Fact]
    public async Task No_matching_merchant_returns_empty_without_item_fetches()
    {
        var (client, handler) = Build(_ => Json(FlyersJson));
        var deals = await client.FetchFlyersForStoreAsync("Costco", "M5V");
        Assert.Empty(deals);
        Assert.Single(handler.Urls); // just the flyers listing
    }

    [Fact]
    public async Task Http_error_throws_loud()
    {
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.FetchFlyersForStoreAsync("No Frills", "M5V"));
    }

    // 429/403 must surface as the typed throttle exception (not plain HttpRequestException) so
    // FlyerSyncService can persist retry_not_before and abort the remaining stores.
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Throttle_status_throws_typed_exception_with_null_retry_after_when_header_absent(
        HttpStatusCode status)
    {
        var (client, _) = Build(_ => new HttpResponseMessage(status));

        var ex = await Assert.ThrowsAsync<FlyerProviderThrottledException>(
            () => client.FetchFlyersForStoreAsync("No Frills", "M5V"));

        Assert.Null(ex.RetryAfter);
    }

    [Fact]
    public async Task Retry_after_delta_header_is_surfaced()
    {
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90)) },
        });

        var ex = await Assert.ThrowsAsync<FlyerProviderThrottledException>(
            () => client.FetchFlyersForStoreAsync("No Frills", "M5V"));

        Assert.Equal(TimeSpan.FromSeconds(90), ex.RetryAfter);
    }

    [Fact]
    public async Task Retry_after_date_header_is_converted_to_delay()
    {
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(5)) },
        });

        var ex = await Assert.ThrowsAsync<FlyerProviderThrottledException>(
            () => client.FetchFlyersForStoreAsync("No Frills", "M5V"));

        Assert.NotNull(ex.RetryAfter);
        Assert.InRange(ex.RetryAfter!.Value, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Item_level_validity_dates_override_flyer_window()
    {
        const string items =
            """
            [{"name":"Ribeye Steak","current_price":9.99,"valid_from":"2026-07-11T00:00:00-04:00","valid_to":"2026-07-12T00:00:00-04:00"}]
            """;
        var (client, _) = Build(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/flyer_items") ? Json(items) : Json(FlyersJson));

        var deal = Assert.Single(await client.FetchFlyersForStoreAsync("No Frills", "M5V"));

        Assert.Equal("2026-07-11", deal.ValidFrom); // item's own window wins over the flyer's 07-09..07-16
        Assert.Equal("2026-07-12", deal.ValidTo);
    }

    [Fact]
    public async Task Unexpected_response_shape_throws_loud()
    {
        var (client, _) = Build(_ => Json("""{"totally":"different"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchFlyersForStoreAsync("No Frills", "M5V"));
    }

    [Fact]
    public async Task Missing_postal_code_throws_with_actionable_message()
    {
        var (client, _) = Build(_ => Json(FlyersJson));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.FetchFlyersForStoreAsync("No Frills", "  "));
        Assert.Contains("Preferences", ex.Message);
    }

    [Fact]
    public async Task Oversized_response_is_rejected()
    {
        var (client, _) = Build(_ => Json(new string('x', 4 * 1024 * 1024 + 1)));

        await Assert.ThrowsAnyAsync<Exception>(
            () => client.FetchFlyersForStoreAsync("Mart", "M5V"));
    }

    [Fact]
    public async Task Excessive_flyer_item_count_is_rejected()
    {
        var items = "[" + string.Join(",", Enumerable.Range(0, 2001)
            .Select(i => $"{{\"name\":\"Item {i}\",\"current_price\":1}}")) + "]";
        var (client, _) = Build(request => request.RequestUri!.AbsolutePath.EndsWith("/flyer_items")
            ? Json(items)
            : Json("""[{"id":7,"merchant":"Mart"}]"""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.FetchFlyersForStoreAsync("Mart", "M5V"));
        Assert.Contains("too many items", error.Message);
    }

    [Theory]
    [InlineData("No Frills Ontario", "No Frills", true)]
    [InlineData("NoFrills", "No Frills", true)]
    [InlineData("no frills", "No Frills West", true)] // containment works both directions
    [InlineData("Walmart", "No Frills", false)]
    [InlineData("", "No Frills", false)]
    public void MerchantMatches_uses_normalized_containment(string merchant, string store, bool expected)
        => Assert.Equal(expected, FlippClient.MerchantMatches(merchant, store));
}
