using System.IO;

namespace PhotoReview.App.Tests;

/// <summary>
/// Behavior replacements for the former ThumbnailCache source-presence checks: the disk thumbnail cache honours
/// its clear operation. Uses a private temp directory, never the user's LocalAppData cache.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ThumbnailCacheDiskTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview-ThumbDisk-" + Guid.NewGuid().ToString("N"));
    private readonly string _diskDir;

    public ThumbnailCacheDiskTests()
    {
        _diskDir = Path.Combine(_root, "disk");
        Directory.CreateDirectory(_diskDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Fact(DisplayName = "ClearDisk tolerates a file that cannot be deleted (locked by another process)")]
    public void ClearDiskToleratesLockedFile()
    {
        var locked = Path.Combine(_diskDir, "locked.png");
        File.WriteAllBytes(locked, [1, 2, 3]);
        var free = Path.Combine(_diskDir, "free.png");
        File.WriteAllBytes(free, [4, 5, 6]);
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        using var cache = new ThumbnailCache(WpfBitmapSourceCodec.Instance, _diskDir, maxRamBytes: 16 * 1024 * 1024);

        var ex = Record.Exception(cache.ClearDisk);

        Assert.Null(ex);
        Assert.False(File.Exists(free), "The unlocked file must still be cleared.");
        Assert.True(File.Exists(locked));
    }
}
