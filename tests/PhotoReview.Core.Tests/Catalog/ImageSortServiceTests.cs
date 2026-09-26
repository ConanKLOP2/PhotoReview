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
        var key1 = ManagedNaturalComparer.BuildNaturalKey("photo2.jpg");
        var key2 = ManagedNaturalComparer.BuildNaturalKey("photo10.jpg");
        Assert.True(string.Compare(key1, key2, StringComparison.OrdinalIgnoreCase) < 0);
    }

    private static List<CatalogEntry> OutOfOrderEntries() =>
    [
        new CatalogEntry(@"C:\f\img10.jpg").WithMetadata(50, DateTime.UtcNow),
        new CatalogEntry(@"C:\f\IMG2.jpg").WithMetadata(300, DateTime.UtcNow),
        new CatalogEntry(@"C:\f\b.jpg").WithMetadata(10, DateTime.UtcNow),
        new CatalogEntry(@"C:\f\img2.jpg").WithMetadata(300, DateTime.UtcNow),
        new CatalogEntry(@"C:\f\A.jpg").WithMetadata(999, DateTime.UtcNow),
        new CatalogEntry(@"C:\f\img10.jpg").WithMetadata(60, DateTime.UtcNow),
    ];

    [Fact]
    public void Default_SortEntries_ReturnsInputOrderIncludingDuplicates()
    {
        var input = OutOfOrderEntries();
        var result = ImageSortService.SortEntries(input, ImageSortMode.Default);
        Assert.Equal(input.Select(e => e.Path), result.Select(e => e.Path));
        Assert.NotSame(input, result);
        Assert.Empty(ImageSortService.SortEntries([], ImageSortMode.Default));
    }

    [Fact]
    public void Default_LegacySort_ReturnsInputOrder()
    {
        string[] input = [@"C:\f\img10.jpg", @"C:\f\B.jpg", @"C:\f\img2.jpg", @"C:\f\a.jpg", @"C:\f\img2.jpg"];
        Assert.Equal(input, ImageSortService.Sort(input, ImageSortMode.Default));
        Assert.Equal(input, ImageSortService.Sort(input, "Default"));
        Assert.Empty(ImageSortService.Sort([], ImageSortMode.Default));
    }

    [Fact]
    public void NameAscending_EqualsNameMode_AndIsNaturalCaseInsensitive()
    {
        var input = OutOfOrderEntries();
        var name = ImageSortService.SortEntries(input, ImageSortMode.Name).Select(e => e.Path).ToList();
        Assert.Equal(name, ImageSortService.SortEntries(input, ImageSortMode.NameAscending).Select(e => e.Path));
        Assert.Equal("A.jpg", Path.GetFileName(name[0]));
        Assert.Equal("b.jpg", Path.GetFileName(name[1]));
        Assert.Equal("img10.jpg", Path.GetFileName(name[^1]));
        var paths = input.Select(e => e.Path).ToList();
        Assert.Equal(ImageSortService.Sort(paths, ImageSortMode.Name), ImageSortService.Sort(paths, ImageSortMode.NameAscending));
    }

    [Fact]
    public void NameDescending_IsExactReverseOfAscending_IncludingTies()
    {
        var input = OutOfOrderEntries();
        var asc = ImageSortService.SortEntries(input, ImageSortMode.NameAscending);
        var desc = ImageSortService.SortEntries(input, ImageSortMode.NameDescending);
        // Entry identity: the two equal-name entries (img10.jpg twice) reverse deterministically too.
        Assert.Equal(asc.AsEnumerable().Reverse(), desc);
        Assert.Equal("img10.jpg", Path.GetFileName(desc[0].Path));

        var paths = input.Select(e => e.Path).ToList();
        var ascPaths = ImageSortService.Sort(paths, ImageSortMode.NameAscending);
        Assert.Equal(ascPaths.AsEnumerable().Reverse(), ImageSortService.Sort(paths, ImageSortMode.NameDescending));
        Assert.Equal(ascPaths.AsEnumerable().Reverse(), ImageSortService.Sort(paths, "NameDescending"));
        Assert.Empty(ImageSortService.SortEntries([], ImageSortMode.NameDescending));
    }

    [Fact]
    public void ExplorerPolicy_OnlyNameAndSizeModesFollowExplorer()
    {
        Assert.True(SortModePolicy.UsesExplorerOrder(ImageSortMode.Name));
        Assert.True(SortModePolicy.UsesExplorerOrder(ImageSortMode.SizeAscending));
        Assert.True(SortModePolicy.UsesExplorerOrder(ImageSortMode.SizeDescending));
        Assert.False(SortModePolicy.UsesExplorerOrder(ImageSortMode.Default));
        Assert.False(SortModePolicy.UsesExplorerOrder(ImageSortMode.NameAscending));
        Assert.False(SortModePolicy.UsesExplorerOrder(ImageSortMode.NameDescending));
    }

    [Fact]
    public void EnumValues_AreStableAppendOnly()
    {
        Assert.Equal(0, (int)ImageSortMode.Name);
        Assert.Equal(1, (int)ImageSortMode.SizeDescending);
        Assert.Equal(2, (int)ImageSortMode.SizeAscending);
        Assert.Equal(3, (int)ImageSortMode.Default);
        Assert.Equal(4, (int)ImageSortMode.NameAscending);
        Assert.Equal(5, (int)ImageSortMode.NameDescending);
    }
}
