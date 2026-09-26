using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

public sealed partial class FolderLoadCoordinatorTests
{
    [Fact(DisplayName = "The Explorer snapshot of a load is kept for the Diagnostics window")]
    public async Task LoadAsync_ExplorerSort_PublishesSnapshotToLatestExplorerSnapshot()
    {
        var folder = @"C:\photos";
        var (a, b, c) = CreateImagesScanOrderCab(folder);
        _settingsStore.Current.ImageSortMode = ImageSortMode.Name;
        _explorerOrder.SnapshotHook = _ => Task.FromResult(MakeSnapshot(folder, [b, c, a]));
        var latest = new LatestExplorerSnapshot();

        using var coordinator = new FolderLoadCoordinator(
            _catalog, _genClock, _explorerOrder, _fs, _sessionStore, _settingsStore, _sink, latestExplorer: latest);
        await coordinator.LoadAsync(folder);

        var snapshot = latest.Current;
        Assert.NotNull(snapshot);
        Assert.Equal(ExplorerOrderStatus.Available, snapshot.Status);
        Assert.Equal(new[] { b, c, a }, snapshot.OrderedPaths);
    }

    [Fact(DisplayName = "An app-decided sort mode leaves nothing queried, so the previous folder's snapshot is not shown")]
    public async Task LoadAsync_AppDecidedSort_ClearsLatestExplorerSnapshot()
    {
        var folder = @"C:\photos";
        var (a, b, c) = CreateImagesScanOrderCab(folder);
        _explorerOrder.SnapshotHook = _ => Task.FromResult(MakeSnapshot(folder, [b, c, a]));
        var latest = new LatestExplorerSnapshot();
        using var coordinator = new FolderLoadCoordinator(
            _catalog, _genClock, _explorerOrder, _fs, _sessionStore, _settingsStore, _sink, latestExplorer: latest);

        _settingsStore.Current.ImageSortMode = ImageSortMode.Name;
        await coordinator.LoadAsync(folder);
        Assert.NotNull(latest.Current);

        _settingsStore.Current.ImageSortMode = ImageSortMode.Default;
        await coordinator.LoadAsync(folder);

        Assert.Null(latest.Current);
    }

    [Fact(DisplayName = "A snapshot still being queried is not reported")]
    public void Current_QueryStillRunning_IsNull()
    {
        var latest = new LatestExplorerSnapshot();
        var running = new TaskCompletionSource<ExplorerViewSnapshot>();

        latest.Set(running.Task);

        Assert.Null(latest.Current);
    }
}
