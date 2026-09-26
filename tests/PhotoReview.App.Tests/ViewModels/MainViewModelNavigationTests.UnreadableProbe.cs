using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// AR16: files found unreadable by the background probe (real locked files) leave the catalog, the count, the
/// preload and the session, and are reported through the "skipped" warning.
/// </summary>
public sealed partial class MainViewModelNavigationTests
{
    private static string PreloadKey(string path) => "evict:" + Path.GetFullPath(path).ToUpperInvariant();

    [Fact(DisplayName = "AR16: an unreadable neighbour is removed, reported, evicted from preload and preload re-centers")]
    public async Task UnreadableProbe_NonCurrent_RemovedFromCatalogPreloadAndCount()
    {
        var a = CreateImageFile(_tempDir, "a.png");
        var b = CreateImageFile(_tempDir, "b.png");
        var c = CreateImageFile(_tempDir, "c.png");
        var (vm, _, _) = CreateViewModel();

        using (new FileStream(b, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await vm.OpenFolderAsync(_tempDir);
            await vm.ReadabilityProbeTask.WithTimeout(Wait.DefaultTimeout, "readability probe");
        }

        Assert.Equal([a, c], _catalog.Paths);
        Assert.Equal(a, _catalog.Current?.Path);
        Assert.Equal(2, vm.TotalFiles);
        Assert.Equal(b, Assert.Single(vm.SkippedEntries).Path);
        Assert.True(vm.HasSkippedEntries);

        // The preload loop is stopped and the removed file's keys dropped before preload restarts on the new catalog.
        var calls = _preloadController.Calls.ToList();
        var evict = calls.IndexOf(PreloadKey(b));
        Assert.True(evict > 0, "the removed file's preload keys were not dropped: " + string.Join(", ", calls));
        Assert.Equal("cancel", calls[evict - 1]);
        Assert.Equal("around:0", calls[^1]);
        Assert.True(calls.Count - 1 > evict);
    }

    [Fact(DisplayName = "AR16: an unreadable current image is removed like a Delete: the next image is shown and saved in the session")]
    public async Task UnreadableProbe_Current_AdvancesToNextAndUpdatesSession()
    {
        var a = CreateImageFile(_tempDir, "a.png");
        var b = CreateImageFile(_tempDir, "b.png");
        var c = CreateImageFile(_tempDir, "c.png");
        var (vm, _, _) = CreateViewModel();

        using (new FileStream(a, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await vm.OpenFolderAsync(_tempDir);
            await vm.ReadabilityProbeTask.WithTimeout(Wait.DefaultTimeout, "readability probe");
        }

        Assert.Equal([b, c], _catalog.Paths);
        Assert.Equal(b, _catalog.Current?.Path);
        Assert.Equal(b, vm.Session?.CurrentPath);
        Assert.Equal(2, vm.TotalFiles);
        Assert.Equal(a, Assert.Single(vm.SkippedEntries).Path);
        var calls = _preloadController.Calls.ToList();
        var evict = calls.IndexOf(PreloadKey(a));
        Assert.True(evict > 0, "the removed file's preload keys were not dropped: " + string.Join(", ", calls));
        Assert.Equal("cancel", calls[evict - 1]);
    }
}
