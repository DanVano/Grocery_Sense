using GrocerySense.Core;
using Xunit;

namespace GrocerySense.Tests;

public sealed class PendingSharedReceiptsServiceTests
{
    [Fact]
    public void Nothing_pending_by_default()
    {
        var svc = new PendingSharedReceiptsService();
        Assert.False(svc.HasPending);
        var (paths, errors) = svc.Take();
        Assert.Empty(paths);
        Assert.Empty(errors);
    }

    [Fact]
    public void Paths_and_errors_are_handed_off_exactly_once()
    {
        var svc = new PendingSharedReceiptsService();
        svc.Set(["/data/receipts/a.jpg", "/data/receipts/b.jpg"], ["one rejected"]);

        Assert.True(svc.HasPending);
        var (paths, errors) = svc.Take();
        Assert.Equal(["/data/receipts/a.jpg", "/data/receipts/b.jpg"], paths);
        Assert.Equal(["one rejected"], errors);

        // Consumed: a second drain is empty, so a share can't be double-imported.
        Assert.False(svc.HasPending);
        var (paths2, errors2) = svc.Take();
        Assert.Empty(paths2);
        Assert.Empty(errors2);
    }

    [Fact]
    public void Errors_alone_still_count_as_pending()
    {
        // A share where every item was rejected must still surface so the user sees why nothing imported.
        var svc = new PendingSharedReceiptsService();
        svc.Set([], ["exceeds 20 MiB", "unsupported type"]);

        Assert.True(svc.HasPending);
        var (paths, errors) = svc.Take();
        Assert.Empty(paths);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Set_raises_Changed_for_the_warm_share_path()
    {
        var svc = new PendingSharedReceiptsService();
        var fired = 0;
        svc.Changed += () => fired++;

        svc.Set(["/data/receipts/a.jpg"], []);

        Assert.Equal(1, fired);
    }
}
