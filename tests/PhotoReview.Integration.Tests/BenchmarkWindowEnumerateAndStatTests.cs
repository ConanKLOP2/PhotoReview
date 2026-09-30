using System.IO;
using PhotoReview.App;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// R11 (full-code-review-2026-09-27): <see cref="BenchmarkWindow.EnumerateAndStat"/> is the folder-exists check,
/// the top-level file listing (filtered to supported image types, capped, then stat-summed) that
/// <see cref="BenchmarkWindow.RunAsync"/> now runs on a background thread (via <c>Task.Run</c>) instead of
/// synchronously on the dialog's dispatcher, so a large or slow/NAS folder no longer blocks the window while it
/// scans. These tests exercise the extracted static logic directly (no WPF window/dispatcher needed) -- same
/// production method the window calls.
/// </summary>
public sealed class BenchmarkWindowEnumerateAndStatTests : IDisposable
{
    private readonly TempRoot _root = new("bench-enumerate");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "A missing folder throws DirectoryNotFoundException (mapped by the caller to BenchmarkStatusFolderMissing)")]
    public void EnumerateAndStat_MissingFolder_ThrowsDirectoryNotFound()
    {
        var missing = Path.Combine(_root.Path, "does-not-exist");

        Assert.Throws<DirectoryNotFoundException>(() => BenchmarkWindow.EnumerateAndStat(missing, imageLimit: 0));
    }

    [Fact(DisplayName = "Non-image files are filtered out and the remaining supported files keep their enumeration order")]
    public void EnumerateAndStat_FiltersToSupportedTypes_KeepsEnumerationOrder()
    {
        var dir = _root.Dir("mixed");
        File.WriteAllBytes(Path.Combine(dir, "a.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, "b.txt"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(dir, "c.png"), [1, 2, 3, 4, 5]);
        var expectedOrder = Directory.EnumerateFiles(dir).Where(PhotoReview.Core.Catalog.ImageFileTypes.IsSupported)
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();

        var (files, totalSourceBytes) = BenchmarkWindow.EnumerateAndStat(dir, imageLimit: 0);

        Assert.Equal(2, files.Length);
        Assert.All(files, f => Assert.True(PhotoReview.Core.Catalog.ImageFileTypes.IsSupported(f)));
        Assert.Equal(8, totalSourceBytes); // 3 (a.jpg) + 5 (c.png); b.txt must not be counted
        Assert.Equal(expectedOrder.OrderBy(p => p, StringComparer.Ordinal), files.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact(DisplayName = "Camera RAW files are never benchmarked: the executor has no RAW-routed decoder, so they would fail or measure WIC")]
    public void EnumerateAndStat_RawFilesInFolder_AreExcluded()
    {
        var dir = _root.Dir("rawmix");
        File.WriteAllBytes(Path.Combine(dir, "a.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, "b.cr2"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(dir, "c.nef"), [1, 2, 3, 4, 5]);

        var (files, totalSourceBytes) = BenchmarkWindow.EnumerateAndStat(dir, imageLimit: 0);

        Assert.Equal([Path.Combine(dir, "a.jpg")], files);
        Assert.Equal(3, totalSourceBytes);
    }

    [Fact(DisplayName = "An image-count cap keeps only the first N supported files and the stat pass only covers those")]
    public void EnumerateAndStat_ImageLimit_CapsFilesAndTotalBytes()
    {
        var dir = _root.Dir("many");
        for (var i = 0; i < 10; i++) File.WriteAllBytes(Path.Combine(dir, $"img{i:00}.jpg"), new byte[i + 1]);

        var (uncapped, uncappedBytes) = BenchmarkWindow.EnumerateAndStat(dir, imageLimit: 0);
        var (capped, cappedBytes) = BenchmarkWindow.EnumerateAndStat(dir, imageLimit: 3);

        Assert.Equal(10, uncapped.Length);
        Assert.Equal(3, capped.Length);
        Assert.Equal(BenchmarkWindow.ApplyImageLimit(uncapped, 3), capped);
        Assert.Equal(capped.Sum(p => new FileInfo(p).Length), cappedBytes);
        Assert.True(cappedBytes < uncappedBytes);
    }

    [Fact(DisplayName = "A folder with only unsupported files returns an empty list, not an error (the caller maps this to BenchmarkStatusNoImages)")]
    public void EnumerateAndStat_NoSupportedFiles_ReturnsEmpty()
    {
        var dir = _root.Dir("no-images");
        File.WriteAllBytes(Path.Combine(dir, "readme.txt"), [1]);

        var (files, totalSourceBytes) = BenchmarkWindow.EnumerateAndStat(dir, imageLimit: 0);

        Assert.Empty(files);
        Assert.Equal(0, totalSourceBytes);
    }
}
