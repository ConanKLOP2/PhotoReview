using System;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Catalog;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// AR16 (Q-AR7 option c): the per-file readability probe runs in the background after the first frame; unreadable
/// files are then removed from the catalog and reported (ADR 0007 s3), and a superseded load's result is dropped.
/// </summary>
public sealed partial class FolderLoadCoordinatorTests
{
    /// <summary>Holds every probe of <paramref name="heldPath"/> until <paramref name="release"/> is set.</summary>
    private void HoldProbeOf(string heldPath, ManualResetEventSlim release, TaskCompletionSource? entered = null) =>
        _fs.OnProbe = p =>
        {
            if (!string.Equals(p, heldPath, StringComparison.OrdinalIgnoreCase)) return;
            entered?.TrySetResult();
            release.Wait();
        };

    [Fact(DisplayName = "AR16: the first image is presented while the probe is still held; then the unreadable file is removed and reported once")]
    public async Task Probe_FirstFrameDoesNotWaitForProbe_ThenUnreadableIsRemovedAndReported()
    {
        var (a, b, c) = CreateThreeImages(@"C:\photos");
        _fs.Unreadable[b] = "locked";
        using var release = new ManualResetEventSlim(false);
        HoldProbeOf(b, release);
        using var coordinator = CreateCoordinator();

        Task load;
        try
        {
            load = coordinator.LoadAsync(@"C:\photos");
            // Fails (times out) if the first frame waits for the probe.
            await _sink.FirstPresented.Task.WithTimeout(Wait.DefaultTimeout, "first frame before the readability probe completes");

            Assert.Equal([a, b, c], _catalog.Paths);
            Assert.Empty(_sink.SkippedCalls);
            Assert.Empty(_sink.Removals);
        }
        finally
        {
            release.Set();
        }

        await load.WithTimeout(Wait.DefaultTimeout, "load");
        await coordinator.ReadabilityProbe.WithTimeout(Wait.DefaultTimeout, "readability probe");

        Assert.Equal([a, c], _catalog.Paths);
        Assert.Equal(a, _catalog.Current?.Path);
        var call = Assert.Single(_sink.SkippedCalls);
        Assert.Equal(@"C:\photos", call.Folder);
        var entry = Assert.Single(call.Skipped);
        Assert.Equal(b, entry.Path);
        Assert.Equal("locked", entry.Reason);
        var removal = Assert.Single(_sink.Removals);
        Assert.Equal([b], removal.Paths);
        Assert.False(removal.CurrentRemoved);
        Assert.Single(_sink.Presented); // the current image is kept, not re-presented
        Assert.Empty(_sink.Failures);
    }

    [Fact(DisplayName = "AR16: removing a file before the current one keeps the current path at its shifted index")]
    public async Task Probe_NonCurrentRemoved_KeepsCurrentPathAtNewIndex()
    {
        var (a, b, c) = CreateThreeImages(@"C:\photos");
        _fs.Unreadable[a] = "denied";
        _explorerOrder.SnapshotHook = f => Task.FromResult(MakeSnapshot(f, [a, b, c]));
        using var coordinator = CreateCoordinator();

        await coordinator.LoadAsync(@"C:\photos", initialPath: c);
        await coordinator.ReadabilityProbe.WithTimeout(Wait.DefaultTimeout, "readability probe");

        Assert.Equal([b, c], _catalog.Paths);
        Assert.Equal(c, _catalog.Current?.Path);
        Assert.Equal(1, _catalog.CurrentIndex);
        Assert.False(Assert.Single(_sink.Removals).CurrentRemoved);
    }

    [Fact(DisplayName = "AR16: an unreadable current image is removed and the catalog advances like a Delete")]
    public async Task Probe_CurrentUnreadable_AdvancesLikeDelete()
    {
        var (a, b, c) = CreateThreeImages(@"C:\photos");
        _fs.Unreadable[b] = "locked";
        _explorerOrder.SnapshotHook = f => Task.FromResult(MakeSnapshot(f, [a, b, c]));
        using var coordinator = CreateCoordinator();

        await coordinator.LoadAsync(@"C:\photos", initialPath: b);
        Assert.Equal(1, _catalog.CurrentIndex);
        await coordinator.ReadabilityProbe.WithTimeout(Wait.DefaultTimeout, "readability probe");

        // Delete of index 1 in [a, b, c] -> [a, c] at index 1.
        Assert.Equal([a, c], _catalog.Paths);
        Assert.Equal(c, _catalog.Current?.Path);
        Assert.Equal(1, _catalog.CurrentIndex);
        var removal = Assert.Single(_sink.Removals);
        Assert.True(removal.CurrentRemoved);
        Assert.Equal(b, Assert.Single(Assert.Single(_sink.SkippedCalls).Skipped).Path);
    }

    [Fact(DisplayName = "AR16: a probe result that arrives after the folder generation moved on is dropped")]
    public async Task Probe_FolderGenerationBumpedBeforeResult_IsDropped()
    {
        var (a, b, c) = CreateThreeImages(@"C:\photos");
        _fs.Unreadable[b] = "locked";
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HoldProbeOf(b, release, entered);
        using var coordinator = CreateCoordinator();

        Task load;
        try
        {
            load = coordinator.LoadAsync(@"C:\photos");
            await entered.Task.WithTimeout(Wait.DefaultTimeout, "probe of the unreadable file");
            _genClock.NextFolder(); // superseded without cancelling this load's token
        }
        finally
        {
            release.Set();
        }

        await load.WithTimeout(Wait.DefaultTimeout, "load");
        await coordinator.ReadabilityProbe.WithTimeout(Wait.DefaultTimeout, "readability probe");

        Assert.Equal([a, b, c], _catalog.Paths);
        Assert.Empty(_sink.SkippedCalls);
        Assert.Empty(_sink.Removals);
    }

    [Fact(DisplayName = "AR16: the old folder's late probe never touches the new folder's catalog or warning")]
    public async Task Probe_LateResultAfterFolderSwitch_DoesNotTouchTheNewFolder()
    {
        var (_, b, _) = CreateThreeImages(@"C:\photos");
        _fs.CreateDirectory(@"C:\other");
        _fs.WriteAllTextAtomic(@"C:\other\x.jpg", "x");
        _fs.WriteAllTextAtomic(@"C:\other\y.jpg", "y");
        _fs.Unreadable[b] = "locked";
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HoldProbeOf(b, release, entered);
        using var coordinator = CreateCoordinator();

        Task first;
        try
        {
            first = coordinator.LoadAsync(@"C:\photos");
            await entered.Task.WithTimeout(Wait.DefaultTimeout, "probe of the unreadable file");
            await coordinator.LoadAsync(@"C:\other");
        }
        finally
        {
            release.Set();
        }

        await first.WithTimeout(Wait.DefaultTimeout, "first load");
        await coordinator.ReadabilityProbe.WithTimeout(Wait.DefaultTimeout, "readability probe");

        Assert.Equal([@"C:\other\x.jpg", @"C:\other\y.jpg"], _catalog.Paths);
        Assert.Empty(_sink.SkippedCalls);
        Assert.Empty(_sink.Removals);
    }

    [Fact(DisplayName = "AR16 + INV-9: a probe done before the Explorer order is held until the order is applied to the full listing")]
    public async Task Probe_DoneBeforePendingExplorerOrder_IsAppliedAfterTheOrder()
    {
        var (a, b, c) = CreateThreeImages(@"C:\photos");
        _fs.Unreadable[a] = "locked";
        var allProbed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probed = 0;
        _fs.OnProbe = _ =>
        {
            if (Interlocked.Increment(ref probed) == 3) allProbed.TrySetResult();
        };
        var snapshot = new TaskCompletionSource<ExplorerViewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _explorerOrder.SnapshotHook = _ => snapshot.Task;
        using var coordinator = CreateCoordinator();

        var load = coordinator.LoadAsync(@"C:\photos", initialPath: b);
        await allProbed.Task.WithTimeout(Wait.DefaultTimeout, "all files probed");

        // The order is still pending: the catalog keeps the full listing and navigation stays gated.
        Assert.Equal(3, _catalog.Count);
        Assert.False(coordinator.PendingOrder.IsCompleted);
        Assert.Empty(_sink.SkippedCalls);

        snapshot.SetResult(MakeSnapshot(@"C:\photos", [c, b, a]));
        await load.WithTimeout(Wait.DefaultTimeout, "load");
        await coordinator.ReadabilityProbe.WithTimeout(Wait.DefaultTimeout, "readability probe");

        Assert.Equal(1, _sink.OrderAppliedCount);
        Assert.Equal([c, b], _catalog.Paths);
        Assert.Equal(b, _catalog.Current?.Path);
        Assert.Equal(a, Assert.Single(Assert.Single(_sink.SkippedCalls).Skipped).Path);
    }

    [Fact(DisplayName = "AR16: a file deleted after the listing is not reported as unreadable (the presenter drops it when reached)")]
    public async Task Probe_FileDeletedAfterListing_IsNotReportedOrRemoved()
    {
        var (a, b, c) = CreateThreeImages(@"C:\photos");
        _fs.Unreadable[b] = "Could not find file";
        _fs.OnProbe = p =>
        {
            if (string.Equals(p, b, StringComparison.OrdinalIgnoreCase)) _fs.Files.Remove(b); // deleted behind the app's back
        };
        using var coordinator = CreateCoordinator();

        await coordinator.LoadAsync(@"C:\photos");
        await coordinator.ReadabilityProbe.WithTimeout(Wait.DefaultTimeout, "readability probe");

        Assert.Equal([a, b, c], _catalog.Paths);
        Assert.Empty(_sink.SkippedCalls);
        Assert.Empty(_sink.Removals);
    }

    [Fact(DisplayName = "AR16: a folder where every file is readable reports and removes nothing")]
    public async Task Probe_AllReadable_ReportsNothing()
    {
        var (a, b, c) = CreateThreeImages(@"C:\photos");
        using var coordinator = CreateCoordinator();

        await coordinator.LoadAsync(@"C:\photos");
        await coordinator.ReadabilityProbe.WithTimeout(Wait.DefaultTimeout, "readability probe");

        Assert.Equal([a, b, c], _catalog.Paths);
        Assert.Equal(3, _fs.ProbeCount);
        Assert.Empty(_sink.SkippedCalls);
        Assert.Empty(_sink.Removals);
    }
}
