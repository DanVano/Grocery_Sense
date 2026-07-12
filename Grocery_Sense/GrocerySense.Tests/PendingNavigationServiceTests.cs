using GrocerySense.Core;
using Xunit;

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
}
