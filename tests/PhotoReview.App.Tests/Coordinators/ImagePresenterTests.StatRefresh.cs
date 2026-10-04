using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>Stryker round 3 (App): the stat taken after the image is on screen refreshes the catalog entry when the file changed meanwhile.</summary>
public sealed partial class ImagePresenterTests
{
    private async Task<CatalogEntry> PresentWhoseRefreshStatDiffersAsync(string name, Func<FileStat, FileStat> refreshed)
    {
        var path = CreateFakeImageFile(name);
        _catalog.Reset([path]);
        var fileSystem = new DelayedStatFileSystem(new PhotoReview.Core.IO.PhysicalFileSystem());
        var real = new PhotoReview.Core.IO.PhysicalFileSystem().GetFileStat(path)!;
        var calls = 0;
        // The first stat (before the decode) is the real one; the refresh stat after the image is shown reports a change.
        fileSystem.Override(path, () => Interlocked.Increment(ref calls) == 1 ? real : refreshed(real));
        var presenter = CreatePresenterWithDelayedStat(fileSystem);

        await presenter.PresentAsync(0);

        Assert.True(calls >= 2, "the post-present refresh stat did not run");
        return _catalog.Find(path)!;
    }

    [Fact]
    public async Task Present_WhenOnlyTheLengthChangedWhileShowing_TheCatalogEntryTakesTheNewLength()
    {
        var entry = await PresentWhoseRefreshStatDiffersAsync("refresh_len.png", real => new FileStat(real.Length + 7, real.LastWriteUtc));

        Assert.Equal(new System.IO.FileInfo(entry.Path).Length + 7, entry.Length);
    }

    [Fact]
    public async Task Present_WhenOnlyTheModifiedTimeChangedWhileShowing_TheCatalogEntryTakesTheNewTime()
    {
        var shifted = DateTime.UtcNow.AddMinutes(-5);
        var entry = await PresentWhoseRefreshStatDiffersAsync("refresh_time.png", real => new FileStat(real.Length, shifted));

        Assert.Equal(shifted, entry.LastWriteUtc);
    }
}
