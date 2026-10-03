using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Catalog;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>RV-A05..A07: a superseded or cancelled load must neither throw, nor overwrite the current probe, nor keep scanning.</summary>
public sealed partial class FolderLoadCoordinatorTests
{
    [Fact(DisplayName = "RV-A05: a superseded load whose sink throws completes without an exception and does not disturb the newer load")]
    public async Task LoadAsync_SupersededLoadSinkThrows_DoesNotEscapeAndNewerLoadIsUnaffected()
    {
        CreateThreeImages(@"C:\a");
        _fs.CreateDirectory(@"C:\b");
        _fs.WriteAllTextAtomic(@"C:\b\x.jpg", "x");
        using var coordinator = CreateCoordinator();
        Task? loadB = null;
        _sink.OnCatalogReadyHook = folder =>
        {
            if (!string.Equals(folder, @"C:\a", StringComparison.OrdinalIgnoreCase)) return;
            // Load B starts (and supersedes A) while A is inside its sink call, which then fails.
            loadB = coordinator.LoadAsync(@"C:\b");
            throw new InvalidOperationException("sink failed for a superseded load");
        };

        await coordinator.LoadAsync(@"C:\a").WaitAsync(Wait.DefaultTimeout);
        Assert.NotNull(loadB);
        await loadB!.WaitAsync(Wait.DefaultTimeout);

        Assert.Empty(_sink.Failures); // a stale load reports nothing to the user either
        Assert.Equal([@"C:\b\x.jpg"], _catalog.Paths);
    }

    [Fact(DisplayName = "RV-A06: a superseded load that unwinds late does not replace the newer load's ReadabilityProbe")]
    public async Task LoadAsync_SupersededLoadUnwindsLate_KeepsTheNewerProbe()
    {
        CreateThreeImages(@"C:\a");
        _fs.CreateDirectory(@"C:\b");
        _fs.WriteAllTextAtomic(@"C:\b\x.jpg", "x");
        var staleSnapshot = new TaskCompletionSource<ExplorerViewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _explorerOrder.SnapshotHook = f => f.StartsWith(@"C:\a", StringComparison.OrdinalIgnoreCase)
            ? staleSnapshot.Task
            : Task.FromResult(MakeSnapshot(f, [], ExplorerOrderStatus.NoMatchingWindow));
        using var coordinator = CreateCoordinator();

        // A presents and starts its probe, then waits for the (held) Explorer snapshot.
        var loadA = coordinator.LoadAsync(@"C:\a");
        await _sink.FirstPresented.Task.WaitAsync(Wait.DefaultTimeout);
        await coordinator.LoadAsync(@"C:\b").WaitAsync(Wait.DefaultTimeout);
        var probeOfB = coordinator.ReadabilityProbe;

        staleSnapshot.SetResult(MakeSnapshot(@"C:\a", [], ExplorerOrderStatus.NoMatchingWindow)); // A unwinds now
        await loadA.WaitAsync(Wait.DefaultTimeout);

        Assert.Same(probeOfB, coordinator.ReadabilityProbe);
    }

    [Fact(DisplayName = "RV-A07: cancelling a load mid-scan stops the directory listing within a few entries")]
    public async Task LoadAsync_CancelledDuringScan_StopsEnumerating()
    {
        const int Total = 200;
        _fs.CreateDirectory(@"C:\big");
        for (var i = 0; i < Total; i++)
        {
            _fs.WriteAllTextAtomic($@"C:\big\p{i:D4}.jpg", "x");
        }

        using var coordinator = CreateCoordinator();
        _fs.OnScanEntry = (_, count) =>
        {
            if (count == 10) coordinator.Dispose(); // cancels the load token from inside the scan
        };

        await coordinator.LoadAsync(@"C:\big").WaitAsync(Wait.DefaultTimeout);

        Assert.True(_fs.ScannedEntries < Total, $"scan kept going: {_fs.ScannedEntries} of {Total}");
        Assert.Equal(0, _sink.CatalogReadyCount);
    }
}
