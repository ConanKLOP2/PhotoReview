using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
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

    [Fact(DisplayName = "A1: a sink that throws while the probe result is applied is logged, the probe task does not fault")]
    public async Task Probe_SinkThrowsWhileApplyingResult_ProbeTaskDoesNotFault()
    {
        var (_, b, _) = CreateThreeImages(@"C:\photos");
        _fs.Unreadable[b] = "locked";
        _sink.OnFilesSkippedHook = () => throw new InvalidOperationException("sink bug");
        using var coordinator = CreateCoordinator();

        await coordinator.LoadAsync(@"C:\photos");
        await coordinator.ReadabilityProbe.WithTimeout(Wait.DefaultTimeout, "readability probe");

        Assert.True(coordinator.ReadabilityProbe.IsCompletedSuccessfully);
    }

    private async Task<FolderLoadCoordinator> LoadPairFolderAsync(RawPairMode mode, params string[] unreadableFiles)
    {
        const string folder = @"C:\photos";
        _fs.CreateDirectory(folder);
        _fs.WriteAllTextAtomic(@"C:\photos\a.jpg", "jpeg");
        _fs.WriteAllTextAtomic(@"C:\photos\a.cr2", "raw");
        _fs.WriteAllTextAtomic(@"C:\photos\b.jpg", "other");
        _settingsStore.Current.RawSupportEnabled = true;
        _settingsStore.Current.RawPairMode = mode;
        foreach (var file in unreadableFiles) _fs.Unreadable[file] = "locked";
        var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(folder);
        await coordinator.ReadabilityProbe.WithTimeout(Wait.DefaultTimeout, "readability probe");
        return coordinator;
    }

    [Fact(DisplayName = "Probe: an unreadable representative of a JPEG+RAW capture degrades it to the readable RAW instead of dropping the pair")]
    public async Task Probe_UnreadableRepresentativeOfCapture_DegradesToTheReadablePartner()
    {
        using var coordinator = await LoadPairFolderAsync(RawPairMode.PreferJpeg, @"C:\photos\a.jpg");

        Assert.Equal([@"C:\photos\a.cr2", @"C:\photos\b.jpg"], _catalog.Paths);
        Assert.Null(_catalog.Entries[0].CaptureGroup);
        Assert.Equal(0, _catalog.CurrentIndex);
        var removal = Assert.Single(_sink.Removals);
        Assert.Equal([@"C:\photos\a.jpg"], removal.Paths);
        Assert.True(removal.CurrentRemoved); // the sink presents the survivor now at the current index
        Assert.Equal(@"C:\photos\a.jpg", Assert.Single(Assert.Single(_sink.SkippedCalls).Skipped).Path);
    }

    [Fact(DisplayName = "Probe: an unreadable non-representative member keeps the readable representative and reports the member")]
    public async Task Probe_UnreadablePartnerOfCapture_KeepsTheRepresentativeAndReportsThePartner()
    {
        using var coordinator = await LoadPairFolderAsync(RawPairMode.PreferJpeg, @"C:\photos\a.cr2");

        Assert.Equal([@"C:\photos\a.jpg", @"C:\photos\b.jpg"], _catalog.Paths);
        Assert.Equal(0, _catalog.CurrentIndex);
        var removal = Assert.Single(_sink.Removals);
        Assert.Equal([@"C:\photos\a.cr2"], removal.Paths);
        Assert.False(removal.CurrentRemoved);
        Assert.Equal(@"C:\photos\a.cr2", Assert.Single(Assert.Single(_sink.SkippedCalls).Skipped).Path);
    }

    [Fact(DisplayName = "Probe: a capture whose both image members are unreadable is removed and both are reported")]
    public async Task Probe_BothMembersOfCaptureUnreadable_RemovesEntryAndReportsBoth()
    {
        using var coordinator = await LoadPairFolderAsync(RawPairMode.PreferJpeg, @"C:\photos\a.jpg", @"C:\photos\a.cr2");

        Assert.Equal([@"C:\photos\b.jpg"], _catalog.Paths);
        var skipped = Assert.Single(_sink.SkippedCalls).Skipped.Select(s => s.Path).Order(StringComparer.OrdinalIgnoreCase);
        Assert.Equal([@"C:\photos\a.cr2", @"C:\photos\a.jpg"], skipped);
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

    [Fact(DisplayName = "ADR 0007 s3: a file action (StopForAction) while the probe runs does not drop the result; the unreadable file is removed and reported once")]
    public async Task Probe_StopForActionWhileProbing_StillRemovesAndReportsOnce()
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
            _genClock.StopForAction(); // what FileActionController / DuplicateCleanupController do; same folder
        }
        finally
        {
            release.Set();
        }

        await load.WithTimeout(Wait.DefaultTimeout, "load");
        await coordinator.ReadabilityProbe.WithTimeout(Wait.DefaultTimeout, "readability probe");

        Assert.Equal([a, c], _catalog.Paths);
        var call = Assert.Single(_sink.SkippedCalls);
        Assert.Equal(b, Assert.Single(call.Skipped).Path);
        Assert.Equal([b], Assert.Single(_sink.Removals).Paths);
        Assert.Empty(_sink.Failures);
    }

    [Fact(DisplayName = "ADR 0007 s3: an unreadable file that the user's action already removed during the probe is not reported")]
    public async Task Probe_StopForActionAndFileAlreadyRemoved_ReportsNothing()
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
            _genClock.StopForAction();
            _catalog.Remove(b); // the action moved/recycled it first
        }
        finally
        {
            release.Set();
        }

        await load.WithTimeout(Wait.DefaultTimeout, "load");
        await coordinator.ReadabilityProbe.WithTimeout(Wait.DefaultTimeout, "readability probe");

        Assert.Equal([a, c], _catalog.Paths);
        Assert.Empty(_sink.SkippedCalls);
        Assert.Empty(_sink.Removals);
    }

    [Fact(DisplayName = "AR16: a probe result that arrives after the coordinator was disposed is dropped")]
    public async Task Probe_DisposedBeforeResult_IsDropped()
    {
        var (a, b, c) = CreateThreeImages(@"C:\photos");
        _fs.Unreadable[b] = "locked";
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HoldProbeOf(b, release, entered);
        var coordinator = CreateCoordinator();

        Task load;
        try
        {
            load = coordinator.LoadAsync(@"C:\photos");
            await entered.Task.WithTimeout(Wait.DefaultTimeout, "probe of the unreadable file");
            coordinator.Dispose(); // cancels the load token
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

    /// <summary>Minimal single-thread "UI" context: continuations queue up until the pump loop runs them.</summary>
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly object _gate = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_gate)
            {
                _queue.Enqueue((d, state));
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>Blocks (no polling) until at least one continuation is queued.</summary>
        public bool WaitForQueued(TimeSpan timeout)
        {
            lock (_gate)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (_queue.Count == 0)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || !Monitor.Wait(_gate, left)) return _queue.Count > 0;
                }
                return true;
            }
        }

        public void Pump(Task until)
        {
            while (!until.IsCompleted)
            {
                (SendOrPostCallback Callback, object? State) item;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !until.IsCompleted) Monitor.Wait(_gate, 50);
                    if (_queue.Count == 0) continue;
                    item = _queue.Dequeue();
                }
                item.Callback(item.State);
            }
        }
    }

    [Fact(DisplayName = "AR16: a probe that finished before Dispose but whose apply step had not run yet is still dropped (load token gate)")]
    public void Probe_DoneButNotYetApplied_ThenDisposed_IsDropped()
    {
        var (a, b, c) = CreateThreeImages(@"C:\photos");
        _fs.Unreadable[b] = "locked";
        // FLAKY-FolderLoad: without this hold, a starved "UI" thread could be preempted between StartReadabilityProbe
        // and the finally block long enough for the probe to finish first; ApplyReadabilityProbeAsync's `await probe`
        // then completed synchronously inside LoadAsync, nothing was ever queued and WaitForQueued timed out.
        // Holding the probe until LoadAsync has returned makes the apply step always go through the context queue.
        using var releaseProbe = new ManualResetEventSlim(false);
        HoldProbeOf(b, releaseProbe);
        var coordinator = CreateCoordinator();
        var context = new QueueContext();

        Exception? bodyFailure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var body = BodyAsync();
            context.Pump(body);
            try { body.GetAwaiter().GetResult(); }
            catch (Exception ex) { bodyFailure = ex; } // surfaced below instead of crashing the test host
        });
        thread.Start();
        var finished = thread.Join(Wait.DefaultTimeout);
        releaseProbe.Set(); // never leave a pool thread blocked, whatever happened
        Assert.True(finished, "test body finished");
        Assert.Null(bodyFailure);

        Assert.Equal([a, b, c], _catalog.Paths);
        Assert.Empty(_sink.SkippedCalls);
        Assert.Empty(_sink.Removals);

        async Task BodyAsync()
        {
            await coordinator.LoadAsync(@"C:\photos");
            releaseProbe.Set(); // the apply step is now already awaiting the probe, so its continuation must be posted
            // LoadAsync is done, so only the probe's apply continuation can be queued: wait until the probe has
            // completed and posted it, then cancel the load BEFORE the "UI thread" gets to run it.
            Assert.True(context.WaitForQueued(Wait.DefaultTimeout), "the probe completed and queued its apply step");
            coordinator.Dispose();
            await coordinator.ReadabilityProbe;
        }
    }
}
