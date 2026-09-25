using System;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Catalog;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>Folder switching while loading, stale scan/Explorer results and misuse of a coordinator that was closed.</summary>
public sealed partial class FolderLoadCoordinatorTests
{
    [Fact(DisplayName = "Switching folder while the first scan is still running: the slow first load never touches the catalog or the sink")]
    public async Task LoadAsync_SecondFolderWhileFirstScanBlocked_FirstLoadIsDroppedSilently()
    {
        CreateThreeImages(@"C:slow");
        _fs.CreateDirectory(@"C:\fast");
        _fs.WriteAllTextAtomic(@"C:\fast\x.jpg", "x");
        var scanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseScan = new ManualResetEventSlim(false);
        var calls = 0;
        _fs.OnEnumerateFiles = () =>
        {
            if (Interlocked.Increment(ref calls) != 1) return; // only the first (slow) scan blocks
            scanEntered.TrySetResult();
            releaseScan.Wait();
        };

        using var coordinator = CreateCoordinator();
        var slow = coordinator.LoadAsync(@"C:slow");
        await scanEntered.Task.WaitAsync(Wait.DefaultTimeout);

        await coordinator.LoadAsync(@"C:\fast");
        releaseScan.Set();
        await slow.WaitAsync(Wait.DefaultTimeout);

        Assert.Equal(1, _sink.CatalogReadyCount);
        Assert.Equal(1, _sink.ResetCachesCount);
        Assert.Equal([@"C:\fast\x.jpg"], _catalog.Paths);
        Assert.Single(_sink.Presented);
        Assert.Empty(_sink.Failures);
    }

    [Fact(DisplayName = "A folder generation bumped by anything else while the scan runs also invalidates the load, even though its token was never cancelled")]
    public async Task LoadAsync_FolderGenerationBumpedDuringScan_AppliesNothing()
    {
        CreateThreeImages(@"C:slow");
        var scanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseScan = new ManualResetEventSlim(false);
        _fs.OnEnumerateFiles = () =>
        {
            scanEntered.TrySetResult();
            releaseScan.Wait();
        };
        using var coordinator = CreateCoordinator();
        var load = coordinator.LoadAsync(@"C:slow");
        await scanEntered.Task.WaitAsync(Wait.DefaultTimeout);

        _genClock.NextFolder();
        releaseScan.Set();
        await load.WaitAsync(Wait.DefaultTimeout);

        Assert.Equal(0, _sink.ResetCachesCount);
        Assert.Equal(0, _sink.CatalogReadyCount);
        Assert.Equal(0, _catalog.Count);
        Assert.Empty(_sink.Presented);
    }

    [Fact(DisplayName = "Three rapid folder switches: only the last one is ever applied, whatever order the scans finish in")]
    public async Task LoadAsync_RapidSwitches_OnlyTheLastIsApplied()
    {
        foreach (var name in new[] { "one", "two", "three" })
        {
            _fs.CreateDirectory(@"C:\" + name);
            _fs.WriteAllTextAtomic($@"C:\{name}\{name}.jpg", name);
        }
        var entered = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var release = new ManualResetEventSlim(false);
        var calls = -1;
        _fs.OnEnumerateFiles = () =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call >= entered.Length) return; // the last scan is not blocked
            entered[call].TrySetResult();
            release.Wait();
        };

        using var coordinator = CreateCoordinator();
        var first = coordinator.LoadAsync(@"C:\one");
        await entered[0].Task.WaitAsync(Wait.DefaultTimeout);
        var second = coordinator.LoadAsync(@"C:\two");
        await entered[1].Task.WaitAsync(Wait.DefaultTimeout);
        var third = coordinator.LoadAsync(@"C:\three");
        await third.WaitAsync(Wait.DefaultTimeout);

        release.Set(); // the two superseded scans now finish
        await Task.WhenAll(first, second).WaitAsync(Wait.DefaultTimeout);

        Assert.Equal(1, _sink.CatalogReadyCount);
        Assert.Equal([@"C:\three\three.jpg"], _catalog.Paths);
        Assert.Empty(_sink.Failures);
    }

    [Fact(DisplayName = "An Explorer order that arrives for a folder the user already left is not applied to the new folder")]
    public async Task LoadAsync_StaleExplorerSnapshotFromSupersededLoad_IsIgnored()
    {
        var (a, b, c) = CreateThreeImages(@"C:\photos");
        _fs.CreateDirectory(@"C:\other");
        _fs.WriteAllTextAtomic(@"C:\other\x.jpg", "x");
        _fs.WriteAllTextAtomic(@"C:\other\y.jpg", "y");
        var stale = new TaskCompletionSource<ExplorerViewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _explorerOrder.SnapshotHook = f => f.StartsWith(@"C:\photos", StringComparison.OrdinalIgnoreCase)
            ? stale.Task
            : Task.FromResult(MakeSnapshot(f, [], ExplorerOrderStatus.NoMatchingWindow));

        using var coordinator = CreateCoordinator();
        var first = coordinator.LoadAsync(@"C:\photos");
        await WaitUntilAsync(() => _sink.Presented.Count == 1, "first presentation");
        await coordinator.LoadAsync(@"C:\other");
        var presentedBefore = _sink.Presented.Count;

        stale.SetResult(MakeSnapshot(@"C:\photos", [c, b, a]));
        await first.WaitAsync(Wait.DefaultTimeout);

        Assert.Equal(0, _sink.OrderAppliedCount);
        Assert.Equal(presentedBefore, _sink.Presented.Count);
        Assert.Equal([@"C:\other\x.jpg", @"C:\other\y.jpg"], _catalog.Paths);
    }

    [Fact(DisplayName = "A folder generation bumped while the Explorer order is pending also stops the order from being applied")]
    public async Task LoadAsync_FolderGenerationBumpedWhileOrderPending_DoesNotApplyTheOrder()
    {
        var (a, b, c) = CreateThreeImages(@"C:\photos");
        var snapshot = new TaskCompletionSource<ExplorerViewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _explorerOrder.SnapshotHook = _ => snapshot.Task;
        using var coordinator = CreateCoordinator();
        var load = coordinator.LoadAsync(@"C:\photos");
        await WaitUntilAsync(() => _sink.Presented.Count == 1, "fallback presentation");

        _genClock.NextFolder();
        snapshot.SetResult(MakeSnapshot(@"C:\photos", [c, b, a]));
        await load.WaitAsync(Wait.DefaultTimeout);

        Assert.Equal(0, _sink.OrderAppliedCount);
        Assert.Equal([a, b, c], _catalog.Paths);
    }

    [Fact(DisplayName = "A requested file that is not in the folder falls back to the first image without failing")]
    public async Task LoadAsync_InitialPathNotInFolder_PresentsFirstImage()
    {
        var (a, _, _) = CreateThreeImages(@"C:\photos");

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(@"C:\photos", initialPath: @"C:\photos\ghost.jpg");

        Assert.Empty(_sink.Failures);
        Assert.Equal(a, _catalog.Current?.Path);
        Assert.Equal(0, Assert.Single(_sink.Presented).Index);
    }

    [Theory(DisplayName = "A malformed folder or file path is reported through OnFailed, never thrown out of the load")]
    [InlineData("C:\\ph|otos", null)]
    [InlineData("C:\\photos", "C:\\photos\\a\0.jpg")]
    [InlineData("C:\\photos\0", null)]
    public async Task LoadAsync_MalformedPath_ReportsFailure(string folder, string? initialPath)
    {
        CreateThreeImages(@"C:\photos");

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(folder, initialPath);

        Assert.Single(_sink.Failures);
        Assert.Equal(0, _sink.CatalogReadyCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LoadAsync_BlankFolder_IsRejectedUpFront(string? folder)
    {
        using var coordinator = CreateCoordinator();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => coordinator.LoadAsync(folder!));

        Assert.Equal(0, _sink.ResetCachesCount);
    }

    [Fact(DisplayName = "Loading through a coordinator whose window closed fails loudly instead of touching the sink (documents current behavior)")]
    public async Task LoadAsync_AfterDispose_ThrowsObjectDisposed_AndDoesNotTouchTheSink()
    {
        CreateThreeImages(@"C:\photos");
        var coordinator = CreateCoordinator();
        coordinator.Dispose();
        coordinator.Dispose(); // idempotent

        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.LoadAsync(@"C:\photos"));

        Assert.Equal(0, _sink.ResetCachesCount);
        Assert.True(coordinator.PendingOrder.IsCompleted);
    }

    [Fact(DisplayName = "Closing while the Explorer order is still pending releases the navigation gate and applies nothing")]
    public async Task Dispose_WhileExplorerOrderPending_ReleasesGate_AndAppliesNothing()
    {
        var (_, b, _) = CreateThreeImages(@"C:\photos");
        var snapshot = new TaskCompletionSource<ExplorerViewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _explorerOrder.SnapshotHook = _ => snapshot.Task;
        var coordinator = CreateCoordinator();
        var load = coordinator.LoadAsync(@"C:\photos", initialPath: b);
        await WaitUntilAsync(() => _sink.Presented.Count == 1, "first presentation");
        var gate = coordinator.PendingOrder;
        Assert.False(gate.IsCompleted);

        coordinator.Dispose();

        Assert.True(gate.IsCompleted);
        snapshot.SetResult(MakeSnapshot(@"C:\photos", [b]));
        await load.WaitAsync(Wait.DefaultTimeout);
        Assert.Equal(0, _sink.OrderAppliedCount);
    }
}
