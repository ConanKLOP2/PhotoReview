using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.Core.Tests.Catalog;

[Trait("Category", "HotPath")]
public class ImageSortServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _fixture;

    public ImageSortServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview-SortTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _fixture =
        [
            CreateFile("img10.jpg", 300),
            CreateFile("img2.jpg", 100),
            CreateFile("img1.jpg", 200)
        ];
    }

    private string CreateFile(string name, int size)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void NaturalFilenameSortOrdersNumericSuffixes()
    {
        var sorted = ImageSortService.Sort(_fixture, "Name");
        Assert.Equal(["img1.jpg", "img2.jpg", "img10.jpg"], sorted.Select(Path.GetFileName).ToList());
    }

    [Fact]
    public void NaturalFilenameSortHandlesLongNumericRuns()
    {
        var sorted = ImageSortService.Sort(["img1000000000000.jpg", "img2.jpg", "img10.jpg"], "Name");
        Assert.Equal(["img2.jpg", "img10.jpg", "img1000000000000.jpg"], sorted.Select(Path.GetFileName).ToList());
    }

    [Fact]
    public void SizeSortOrdersFilesByDescendingBytes()
    {
        var sorted = ImageSortService.Sort(_fixture, "Size");
        Assert.Equal(["img10.jpg", "img1.jpg", "img2.jpg"], sorted.Select(Path.GetFileName).ToList());
    }

    [Fact]
    public void SizeSortOrdersFilesByAscendingBytes()
    {
        var sorted = ImageSortService.Sort(_fixture, "SizeAscending");
        Assert.Equal(["img2.jpg", "img1.jpg", "img10.jpg"], sorted.Select(Path.GetFileName).ToList());
    }

    [Fact]
    public void SortEntries_UsesScannedMetadataAndKeepsEntryMetadataAttached()
    {
        var entries = new[]
        {
            new CatalogEntry(_fixture[0]) { Length = 300, LastWriteUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new CatalogEntry(_fixture[1]) { Length = 100, LastWriteUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) },
            new CatalogEntry(_fixture[2]) { Length = 200, LastWriteUtc = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc) }
        };

        var sorted = ImageSortService.SortEntries(entries, ImageSortMode.SizeAscending);

        Assert.Equal([_fixture[1], _fixture[2], _fixture[0]], [.. sorted.Select(entry => entry.Path)]);
        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), sorted[0].LastWriteUtc);
        Assert.Equal(100, sorted[0].Length);
    }

    [Fact]
    public void NaturalKeyBuildsExpectedOrder()
    {
        var key1 = ImageSortService.NaturalKey("photo2.jpg");
        var key2 = ImageSortService.NaturalKey("photo10.jpg");
        Assert.True(string.Compare(key1, key2, StringComparison.OrdinalIgnoreCase) < 0);
    }
}

