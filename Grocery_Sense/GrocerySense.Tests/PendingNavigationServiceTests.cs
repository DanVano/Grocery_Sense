using GrocerySense.Core;

namespace GrocerySense.Tests;

public sealed class PendingNavigationServiceTests
{
    [Fact]
    public void Route_is_handed_off_exactly_once()
    {
        var svc = new PendingNavigationService();
        Assert.Null(svc.TakePendingRoute()); // nothing pending

        svc.Set("/savings");
        Assert.Equal("/savings", svc.TakePendingRoute());
        Assert.Null(svc.TakePendingRoute()); // consumed
    }

    [Fact]
    public void Set_raises_RouteSet_for_the_warm_tap_path()
    {
        var svc = new PendingNavigationService();
        var fired = 0;
        svc.RouteSet += () => fired++;

        svc.Set("/savings");

        Assert.Equal(1, fired);
        Assert.Equal("/savings", svc.TakePendingRoute());
    }

    [Fact]
    public void Blank_route_is_ignored()
    {
        var svc = new PendingNavigationService();
        var fired = 0;
        svc.RouteSet += () => fired++;

        svc.Set("   ");

        Assert.Equal(0, fired);
        Assert.Null(svc.TakePendingRoute());
    }

    // Security: the route arrives from an OS intent (Android's exported launcher activity), so anything
    // outside the allowlist — an external URL, a scheme, or an unintended in-app page — must be dropped.
    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example")]
    [InlineData("/preferences")]
    [InlineData("/savings?x=1")]
    public void Non_allowlisted_route_is_rejected(string hostile)
    {
        var svc = new PendingNavigationService();
        var fired = 0;
        svc.RouteSet += () => fired++;

        svc.Set(hostile);

        Assert.Equal(0, fired);
        Assert.Null(svc.TakePendingRoute());
    }
}
