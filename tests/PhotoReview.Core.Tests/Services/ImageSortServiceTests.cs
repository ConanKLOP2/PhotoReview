using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Services;

/// <summary>Image sorting.</summary>
public sealed class ImageSortServiceTests : IDisposable
{
    private readonly TempRoot _root = new("sort");
    private readonly string[] _fixture;

    public ImageSortServiceTests()
    {
        _fixture =
        [
            _root.Combine("img10.jpg"),
            _root.Combine("img2.jpg"),
            _root.Combine("img1.jpg")
        ];
    }

    public void Dispose() => _root.Dispose();

    private void WriteSizes()
    {
        File.WriteAllBytes(_fixture[0], [1]);
        File.WriteAllBytes(_fixture[1], [1, 2, 3]);
        File.WriteAllBytes(_fixture[2], [1, 2]);
    }

    [Fact(DisplayName = "Natural filename sort orders numeric suffixes")]
    public void NaturalFilenameSortOrdersNumericSuffixes()
    {
        var sorted = ImageSortService.Sort(_fixture, "Name");
        Assert.True(Path.GetFileName(sorted[0]) == "img1.jpg"
            && Path.GetFileName(sorted[1]) == "img2.jpg"
            && Path.GetFileName(sorted[2]) == "img10.jpg");
    }

    [Fact(DisplayName = "Natural filename sort handles numeric runs over 12 digits")]
    public void NaturalFilenameSortHandlesLongNumericRuns()
    {
        var sorted = ImageSortService.Sort(["img1000000000000.jpg", "img2.jpg", "img10.jpg"], "Name");
        Assert.True(Path.GetFileName(sorted[0]) == "img2.jpg"
            && Path.GetFileName(sorted[2]) == "img1000000000000.jpg");
    }

    [Fact(DisplayName = "Size sort orders files by descending bytes")]
    public void SizeSortOrdersFilesByDescendingBytes()
    {
        WriteSizes();
        var sorted = ImageSortService.Sort(_fixture, "Size");
        Assert.True(Path.GetFileName(sorted[0]) == "img2.jpg" && Path.GetFileName(sorted[2]) == "img10.jpg");
    }

    [Fact(DisplayName = "Size sort orders files by ascending bytes")]
    public void SizeSortOrdersFilesByAscendingBytes()
    {
        WriteSizes();
        var sorted = ImageSortService.Sort(_fixture, "SizeAscending");
        Assert.True(Path.GetFileName(sorted[0]) == "img10.jpg" && Path.GetFileName(sorted[2]) == "img2.jpg");
    }
}

