using System.IO;
using PhotoReview.App;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// R11 (full-code-review-2026-09-27): <see cref="BenchmarkWindow.EnumerateImageFiles"/> is the folder-exists check,
/// the top-level file listing (filtered to supported image types, then capped) that
/// <see cref="BenchmarkWindow.RunAsync"/> now runs on a background thread (via <c>Task.Run</c>) instead of
/// synchronously on the dialog's dispatcher, so a large or slow/NAS folder no longer blocks the window while it
/// scans. These tests exercise the extracted static logic directly (no WPF window/dispatcher needed) -- same
/// production method the window calls.
/// </summary>
public sealed class BenchmarkWindowEnumerateImageFilesTests : IDisposable
{
    private readonly TempRoot _root = new("bench-enumerate");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "A missing folder throws DirectoryNotFoundException (mapped by the caller to BenchmarkStatusFolderMissing)")]
    public void EnumerateImageFiles_MissingFolder_ThrowsDirectoryNotFound()
    {
        var missing = Path.Combine(_root.Path, "does-not-exist");

        Assert.Throws<DirectoryNotFoundException>(() => BenchmarkWindow.EnumerateImageFiles(missing, imageLimit: 0));
    }

    [Fact(DisplayName = "Non-image files are filtered out and the remaining supported files keep their enumeration order")]
    public void EnumerateImageFiles_FiltersToSupportedTypes_KeepsEnumerationOrder()
    {
        var dir = _root.Dir("mixed");
        File.WriteAllBytes(Path.Combine(dir, "a.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, "b.txt"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(dir, "c.png"), [1, 2, 3, 4, 5]);
        var expectedOrder = Directory.EnumerateFiles(dir).Where(PhotoReview.Core.Catalog.ImageFileTypes.IsSupported)
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();

        var files = BenchmarkWindow.EnumerateImageFiles(dir, imageLimit: 0);

        Assert.Equal(2, files.Length);
        Assert.All(files, f => Assert.True(PhotoReview.Core.Catalog.ImageFileTypes.IsSupported(f)));
        Assert.Equal(expectedOrder.OrderBy(p => p, StringComparer.Ordinal), files.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact(DisplayName = "Camera RAW files are never benchmarked: the executor has no RAW-routed decoder, so they would fail or measure WIC")]
    public void EnumerateImageFiles_RawFilesInFolder_AreExcluded()
    {
        var dir = _root.Dir("rawmix");
        File.WriteAllBytes(Path.Combine(dir, "a.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, "b.cr2"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(dir, "c.nef"), [1, 2, 3, 4, 5]);

        var files = BenchmarkWindow.EnumerateImageFiles(dir, imageLimit: 0);

        Assert.Equal([Path.Combine(dir, "a.jpg")], files);
    }

    [Fact(DisplayName = "An image-count cap keeps only the first N supported files")]
    public void EnumerateImageFiles_ImageLimit_CapsFiles()
    {
        var dir = _root.Dir("many");
        for (var i = 0; i < 10; i++) File.WriteAllBytes(Path.Combine(dir, $"img{i:00}.jpg"), new byte[i + 1]);

        var uncapped = BenchmarkWindow.EnumerateImageFiles(dir, imageLimit: 0);
        var capped = BenchmarkWindow.EnumerateImageFiles(dir, imageLimit: 3);

        Assert.Equal(10, uncapped.Length);
        Assert.Equal(3, capped.Length);
        Assert.Equal(BenchmarkWindow.ApplyImageLimit(uncapped, 3), capped);
    }

    [Fact(DisplayName = "A folder with only unsupported files returns an empty list, not an error (the caller maps this to BenchmarkStatusNoImages)")]
    public void EnumerateImageFiles_NoSupportedFiles_ReturnsEmpty()
    {
        var dir = _root.Dir("no-images");
        File.WriteAllBytes(Path.Combine(dir, "readme.txt"), [1]);

        var files = BenchmarkWindow.EnumerateImageFiles(dir, imageLimit: 0);

        Assert.Empty(files);
    }
}
