using System.IO;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// Stryker gap pins for the sink callbacks <see cref="PhotoReview.App.ViewModels.MainViewModel"/> offers the folder-load and
/// file-action controllers: which of them re-centre the preload and which evict cached files.
/// </summary>
public sealed partial class MainViewModelNavigationTests
{
    private int PreloadAroundCalls() => _preloadController.Calls.Count(call => call.StartsWith("around:", StringComparison.Ordinal));

    private int EvictCallsFor(string fileName) =>
        _preloadController.Calls.Count(call => call.StartsWith("evict:", StringComparison.Ordinal)
            && call.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData(true, 1, true)]
    [InlineData(false, 1, false)] // the current image is gone: nothing to re-centre on
    [InlineData(true, -1, false)] // kept, but no valid index
    public async Task OnOrderApplied_RecentresThePreloadOnlyWhenTheCurrentImageWasKept(bool currentKept, int currentIndex, bool expectPreload)
    {
        var folder = Path.Combine(_tempDir, "order_applied_" + currentKept + currentIndex);
        Directory.CreateDirectory(folder);
        CreateImageFile(folder, "a.png");
        CreateImageFile(folder, "b.png");
        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        var before = PreloadAroundCalls();

        ((IFolderLoadSink)vm).OnOrderApplied(2, currentIndex, currentKept);

        Assert.Equal(expectPreload, PreloadAroundCalls() > before);
    }

    [Fact]
    public async Task EvictCachedPaths_FromAController_DropsEachPathFromThePreloadedKeys()
    {
        var folder = Path.Combine(_tempDir, "evict_paths");
        Directory.CreateDirectory(folder);
        var a = CreateImageFile(folder, "a.png");
        var b = CreateImageFile(folder, "b.png");
        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);

        ((IFileActionSink)vm).EvictCachedPaths([a, b]);

        Assert.Equal(1, EvictCallsFor("a.png"));
        Assert.Equal(1, EvictCallsFor("b.png"));
    }

    [Fact]
    public async Task OnCatalogChanged_WithARemovedPath_EvictsOnlyThatPath()
    {
        var folder = Path.Combine(_tempDir, "catalog_changed");
        Directory.CreateDirectory(folder);
        var a = CreateImageFile(folder, "a.png");
        CreateImageFile(folder, "b.png");
        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        var raised = 0;
        vm.CatalogChanged += () => raised++;

        ((IFileActionSink)vm).OnCatalogChanged(null);
        Assert.Equal(1, raised);
        Assert.Equal(0, EvictCallsFor("a.png"));

        ((IFileActionSink)vm).OnCatalogChanged(a);
        Assert.Equal(2, raised);
        Assert.Equal(1, EvictCallsFor("a.png"));
        Assert.Equal(0, EvictCallsFor("b.png"));
    }
}
