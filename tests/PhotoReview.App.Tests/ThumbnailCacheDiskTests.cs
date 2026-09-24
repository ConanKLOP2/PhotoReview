using System.IO;
using PhotoReview.Imaging.Caching;
using PhotoReview.TestSupport.Windows.Fixtures;

namespace PhotoReview.App.Tests;

/// <summary>
/// Behavior replacements for the former ThumbnailCache source-presence checks: the disk thumbnail cache honours
/// its quota and its clear operation. Uses a private temp directory, never the user's LocalAppData cache.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ThumbnailCacheDiskTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview-ThumbDisk-" + Guid.NewGuid().ToString("N"));
    private readonly string _diskDir;
    private readonly string _jpeg;

    public ThumbnailCacheDiskTests()
    {
        _diskDir = Path.Combine(_root, "disk");
        Directory.CreateDirectory(_diskDir);
        _jpeg = Path.Combine(_root, "with-thumb.jpg");
        File.WriteAllBytes(_jpeg, EmbeddedThumbnailJpegFixture.CreateWithThumbnail(mainSize: 64, thumbnailSize: 16));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Fact(DisplayName = "ThumbnailCache persists a new thumbnail to disk within quota, and ClearDisk empties the directory")]
    public async Task ThumbnailIsPersistedThenClearDiskRemovesIt()
    {
        using var cache = new ThumbnailCache(_diskDir, maxRamBytes: 16 * 1024 * 1024);

        Assert.NotNull(await cache.GetAsync(_jpeg));
        Assert.True(await cache.WaitForPruneAsync(TimeSpan.FromSeconds(10)));
        Assert.NotEmpty(Directory.GetFiles(_diskDir, "*.png"));

        cache.ClearDisk();

        Assert.Empty(Directory.GetFiles(_diskDir, "*.png"));
    }

    [Fact(DisplayName = "ClearDisk tolerates a file that cannot be deleted (locked by another process)")]
    public void ClearDiskToleratesLockedFile()
    {
        var locked = Path.Combine(_diskDir, "locked.png");
        File.WriteAllBytes(locked, [1, 2, 3]);
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        using var cache = new ThumbnailCache(_diskDir, maxRamBytes: 16 * 1024 * 1024);

        var ex = Record.Exception(cache.ClearDisk);

        Assert.Null(ex);
    }

    [Fact(DisplayName = "ThumbnailCache prunes the disk cache down to its byte quota after a write")]
    public async Task DiskQuotaEvictsPersistedThumbnail()
    {
        // 1 byte is smaller than any PNG, so the quota can only be honoured by evicting the file just written.
        using var cache = new ThumbnailCache(_diskDir, maxRamBytes: 16 * 1024 * 1024, maxDiskBytes: 1);

        Assert.NotNull(await cache.GetAsync(_jpeg));
        Assert.True(await cache.WaitForPruneAsync(TimeSpan.FromSeconds(10)));

        Assert.Empty(Directory.GetFiles(_diskDir, "*.png"));
    }
}
