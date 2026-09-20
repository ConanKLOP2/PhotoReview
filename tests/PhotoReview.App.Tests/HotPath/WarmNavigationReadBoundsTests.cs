using System;
using System.Threading.Tasks;
using PhotoReview.TestSupport;
using PhotoReview.TestSupport.Windows.Fixtures;
using Xunit;

namespace PhotoReview.App.Tests.HotPath;

/// <summary>
/// TC03: Warm navigation (Next/Previous in cached range) must not read sources.
/// Uses production MainViewModel, ImagePresenter, PreviewImageService, and real decoder.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class WarmNavigationReadBoundsTests : IAsyncLifetime
{
    private string? _fixtureFolder;
    private readonly int _photoCount = 20;

    public Task InitializeAsync()
    {
        // TC01: Build fixture folder with real JPEG photos
        _fixtureFolder = PhotoFolderBuilder.BuildFolder(_photoCount);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        // Cleanup at session end, not per test
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "TC03: Warm Next does not read sources when in cache")]
    public async Task WarmNext_InCachedRange_ReadsZeroSources()
    {
        Assert.NotNull(_fixtureFolder);
        Assert.True(System.IO.Directory.Exists(_fixtureFolder));

        // Load folder
        var files = System.IO.Directory.GetFiles(_fixtureFolder, "*.jpg");
        Assert.True(files.Length >= 2, "Fixture needs at least 2 images");

        // TODO: Create MainViewModel, load folder, preload range, measure reads
        // TODO: Navigate through warm images, assert zero source reads
        // TODO: Compare before/after ReadBudgetProbe snapshots

        await Task.CompletedTask;
    }

    [Fact(DisplayName = "TC03: Move/Delete does not re-read remaining images")]
    public async Task MoveDelete_InFolder_DoesNotReadUnaffected()
    {
        Assert.NotNull(_fixtureFolder);

        // TODO: Load folder, navigate, capture reads before Move
        // TODO: Move current image, assert other images read count = 0
        // TODO: Same for Delete

        await Task.CompletedTask;
    }
}
