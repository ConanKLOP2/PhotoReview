using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Catalog;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// Stryker round 3 (App): the load's perf marks say which Explorer-order branch ran (applied / ignored / fallback / traced),
/// so they pin those branches. Needs a PhotoReview-Perf listener, hence the non-parallel GlobalState collection (the
/// class is partial: the attribute covers every part).
/// </summary>
[Collection("GlobalState")]
public sealed partial class FolderLoadCoordinatorTests
{
    private sealed class PerfListener : EventListener
    {
        private readonly ConcurrentQueue<(string Name, string Phase, string Detail)> _events = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "PhotoReview-Perf") EnableEvents(eventSource, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            var names = e.PayloadNames ?? [];
            string Get(string name) => names.IndexOf(name) is var i and >= 0 ? e.Payload![i]?.ToString() ?? "" : "";
            if (e.EventName is "Folder" or "FolderInfo") _events.Enqueue((e.EventName, Get("phase"), Get("detail")));
        }

        public List<string> Phases => _events.Where(x => x.Name == "Folder").Select(x => x.Phase).ToList();

        public List<(string Phase, string Detail)> Infos => _events.Where(x => x.Name == "FolderInfo").Select(x => (x.Phase, x.Detail)).ToList();
    }

    private void TwoImages(out string a, out string b)
    {
        _fs.CreateDirectory(@"C:\photos");
        a = @"C:\photos\a.jpg";
        b = @"C:\photos\b.jpg";
        _fs.WriteAllTextAtomic(a, "1");
        _fs.WriteAllTextAtomic(b, "2");
    }

    [Fact]
    public async Task PerfMarks_EarlySnapshotThatReorders_IsMarkedApplied()
    {
        TwoImages(out var a, out var b);
        _explorerOrder.SnapshotHook = folder => Task.FromResult(MakeSnapshot(folder, [b, a]));
        using var listener = new PerfListener();

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(@"C:\photos");

        Assert.Contains("explorerApplied", listener.Phases);
        Assert.DoesNotContain("explorerIgnored", listener.Phases);
    }

    [Fact]
    public async Task PerfMarks_LateSnapshotThatNoLongerMatchesTheCatalog_IsMarkedIgnored()
    {
        TwoImages(out var a, out var b);
        var tcs = new TaskCompletionSource<ExplorerViewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _explorerOrder.SnapshotHook = _ => tcs.Task;
        using var listener = new PerfListener();

        using var coordinator = CreateCoordinator();
        var load = coordinator.LoadAsync(@"C:\photos");
        await Wait.UntilAsync(() => _sink.CatalogReadyCount > 0, "catalog ready");
        _catalog.Remove(b); // the catalog changed after the scan the snapshot was validated against
        tcs.SetResult(MakeSnapshot(@"C:\photos", [a, b]));
        await load;

        Assert.Contains("explorerIgnored", listener.Phases);
        Assert.DoesNotContain("explorerApplied", listener.Phases);
    }

    [Fact]
    public async Task PerfMarks_LateSnapshotThatIsUnavailable_IsMarkedFallback()
    {
        TwoImages(out _, out _);
        var tcs = new TaskCompletionSource<ExplorerViewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _explorerOrder.SnapshotHook = _ => tcs.Task;
        using var listener = new PerfListener();

        using var coordinator = CreateCoordinator();
        var load = coordinator.LoadAsync(@"C:\photos");
        await Wait.UntilAsync(() => _sink.CatalogReadyCount > 0, "catalog ready");
        tcs.SetResult(MakeSnapshot(@"C:\photos", [], ExplorerOrderStatus.NativeViewUnavailable));
        await load;

        Assert.Contains("explorerFallback", listener.Phases);
        Assert.DoesNotContain("explorerApplied", listener.Phases);
    }

    [Fact]
    public async Task PerfMarks_TheExplorerQueryIsTracedWithItsStatusAndCount()
    {
        TwoImages(out var a, out var b);
        _explorerOrder.SnapshotHook = folder => Task.FromResult(MakeSnapshot(folder, [a, b]));
        using var listener = new PerfListener();

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(@"C:\photos");

        await Wait.UntilAsync(() => listener.Infos.Any(i => i.Phase == "explorerSnapshot"), "explorer trace");
        Assert.Contains(listener.Infos, i => i.Phase == "explorerSnapshot" && i.Detail == nameof(ExplorerOrderStatus.Available));
    }

    [Fact]
    public async Task PerfMarks_AFailedExplorerQueryIsTracedWithTheExceptionType()
    {
        TwoImages(out _, out _);
        _explorerOrder.SnapshotHook = _ => Task.FromException<ExplorerViewSnapshot>(new InvalidOperationException("query failed"));
        using var listener = new PerfListener();

        using var coordinator = CreateCoordinator();
        try { await coordinator.LoadAsync(@"C:\photos"); } catch (InvalidOperationException) { }

        await Wait.UntilAsync(() => listener.Infos.Any(i => i.Phase == "explorerSnapshot"), "explorer failure trace");
        Assert.Contains(listener.Infos, i => i.Phase == "explorerSnapshot" && i.Detail == nameof(InvalidOperationException));
    }
}
