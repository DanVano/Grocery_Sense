using GrocerySense.Core;

namespace GrocerySense.Tests;

// P0-2: the share intake is an atomic state machine (Idle -> Copying -> Pending -> Importing -> Idle).
// One batch at a time; a share arriving mid-flight is rejected loudly with zero copies.
public sealed class PendingSharedReceiptsServiceTests
{
    [Fact]
    public void Nothing_pending_by_default()
    {
        var svc = new PendingSharedReceiptsService();
        Assert.Equal(ShareIntakeState.Idle, svc.State);
    }

    [Fact]
    public void Only_one_of_two_concurrent_intents_can_reserve_the_copy_slot()
    {
        var svc = new PendingSharedReceiptsService();
        var wins = new bool[64];

        Parallel.For(0, wins.Length, i => wins[i] = svc.TryBeginCopy());

        Assert.Equal(1, wins.Count(w => w));
        Assert.Equal(ShareIntakeState.Copying, svc.State);
    }

    [Fact]
    public void Share_during_pending_is_rejected_and_copies_nothing()
    {
        var svc = new PendingSharedReceiptsService();
        Assert.True(svc.TryBeginCopy());
        svc.CompleteCopy(["/data/receipts/a.jpg"], []);
        Assert.Equal(ShareIntakeState.Pending, svc.State);

        Assert.False(svc.TryBeginCopy()); // second share must not start copying
        svc.RejectShare("another shared batch is still being processed");

        var (paths, errors) = svc.Peek();
        Assert.Single(paths); // the original batch is intact — never silently replaced
        Assert.Single(errors);
    }

    [Fact]
    public void Share_during_importing_is_rejected_and_surfaces_after_the_import()
    {
        var svc = new PendingSharedReceiptsService();
        Assert.True(svc.TryBeginCopy());
        svc.CompleteCopy(["/data/receipts/a.jpg"], []);
        Assert.True(svc.TryBeginImport(out var claimed, out _));
        Assert.Single(claimed);

        Assert.False(svc.TryBeginCopy());
        svc.RejectShare("busy importing");
        svc.CompleteImport();

        // The mid-import rejection is not swallowed: it survives as an error-only pending batch.
        Assert.Equal(ShareIntakeState.Pending, svc.State);
        Assert.Single(svc.Peek().Errors);
        Assert.Empty(svc.Peek().Paths);
    }

    [Fact]
    public void Peek_renders_without_claiming_and_import_claims_exactly_once()
    {
        var svc = new PendingSharedReceiptsService();
        Assert.True(svc.TryBeginCopy());
        svc.CompleteCopy(["/a.jpg", "/b.jpg"], ["one rejected"]);

        Assert.Equal(2, svc.Peek().Paths.Count);
        Assert.Equal(2, svc.Peek().Paths.Count); // peeking twice changes nothing

        Assert.True(svc.TryBeginImport(out var paths, out var errors));
        Assert.Equal(2, paths.Count);
        Assert.Single(errors);
        Assert.False(svc.TryBeginImport(out _, out _)); // already claimed

        svc.CompleteImport();
        Assert.Equal(ShareIntakeState.Idle, svc.State);
    }

    [Fact]
    public void Error_only_batch_is_pending_and_dismissible()
    {
        var svc = new PendingSharedReceiptsService();
        Assert.True(svc.TryBeginCopy());
        svc.CompleteCopy([], ["exceeds 20 MiB", "unsupported type"]);

        Assert.Equal(ShareIntakeState.Pending, svc.State);
        Assert.False(svc.TryBeginImport(out _, out _)); // nothing to import

        var released = svc.Discard(); // the Dismiss control routes here
        Assert.Empty(released);
        Assert.Equal(ShareIntakeState.Idle, svc.State);
    }

    [Fact]
    public void Discard_releases_the_paths_for_deletion_and_returns_to_idle()
    {
        var svc = new PendingSharedReceiptsService();
        Assert.True(svc.TryBeginCopy());
        svc.CompleteCopy(["/a.jpg", "/b.jpg"], []);

        var released = svc.Discard();

        Assert.Equal(["/a.jpg", "/b.jpg"], released);
        Assert.Equal(ShareIntakeState.Idle, svc.State);
        Assert.True(svc.TryBeginCopy()); // machine is free again
    }

    [Fact]
    public void Empty_copy_result_returns_to_idle_instead_of_wedging()
    {
        var svc = new PendingSharedReceiptsService();
        Assert.True(svc.TryBeginCopy());
        svc.CompleteCopy([], []);
        Assert.Equal(ShareIntakeState.Idle, svc.State);
    }

    [Fact]
    public void Transitions_raise_Changed_for_the_warm_share_path()
    {
        var svc = new PendingSharedReceiptsService();
        var fired = 0;
        svc.Changed += () => fired++;

        Assert.True(svc.TryBeginCopy());
        svc.CompleteCopy(["/a.jpg"], []);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void Error_strings_are_length_bounded_before_storage()
    {
        var svc = new PendingSharedReceiptsService();
        Assert.True(svc.TryBeginCopy());
        svc.CompleteCopy([], [new string('x', 5000)]);

        Assert.Equal(PendingSharedReceiptsService.MaxErrorChars, svc.Peek().Errors[0].Length);
    }

    // Locks the intake ceilings against silent loosening. What this does NOT prove: the branching that
    // applies them lives in MainActivity.CopySharedReceiptsAsync (over-count truncation and the aggregate
    // stop), Android-only code the test project can't reference — that path stays on-device verification.
    [Fact]
    public void Share_intake_caps_hold_their_values()
    {
        Assert.Equal(10, PendingSharedReceiptsService.MaxUrisPerShare);
        Assert.Equal(100L * 1024 * 1024, PendingSharedReceiptsService.MaxAggregateBytes);
        Assert.Equal(TimeSpan.FromMinutes(2), PendingSharedReceiptsService.CopyDeadline);
        Assert.Equal(128, PendingSharedReceiptsService.MaxDisplayNameChars);
    }
}

