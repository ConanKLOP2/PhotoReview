using PhotoReview.Core.Catalog;
using Xunit;

namespace PhotoReview.Core.Tests.Catalog;

[Trait("Category", "HotPath")]
public class ComparePairServiceTests
{
    [Fact]
    public void FindMatchesNumberedVariantWithOriginal()
    {
        var files = new List<string>
        {
            @"C:\Photos\DSC0001.JPG",
            @"C:\Photos\DSC0001 (1).JPG",
            @"C:\Photos\DSC0002.JPG"
        };

        var resultFromOriginal = ComparePairService.Find(files, @"C:\Photos\DSC0001.JPG");
        Assert.NotNull(resultFromOriginal);
        Assert.Equal(@"C:\Photos\DSC0001.JPG", resultFromOriginal.Value.Left);
        Assert.Equal(@"C:\Photos\DSC0001 (1).JPG", resultFromOriginal.Value.Right);

        var resultFromNumbered = ComparePairService.Find(files, @"C:\Photos\DSC0001 (1).JPG");
        Assert.NotNull(resultFromNumbered);
        Assert.Equal(@"C:\Photos\DSC0001.JPG", resultFromNumbered.Value.Left);
        Assert.Equal(@"C:\Photos\DSC0001 (1).JPG", resultFromNumbered.Value.Right);
    }

    [Fact]
    public void FindReturnsNullWhenNoPairExists()
    {
        var files = new List<string>
        {
            @"C:\Photos\DSC0001.JPG",
            @"C:\Photos\DSC0002.JPG"
        };

        var result = ComparePairService.Find(files, @"C:\Photos\DSC0001.JPG");
        Assert.Null(result);
    }

    [Fact(DisplayName = "Compare pair detection stays within selected folder")]
    public void FindStaysWithinSelectedFolder()
    {
        var files = new List<string>
        {
            @"C:\Photos\DSC0001.JPG",
            @"C:\Photos\DSC0001 (1).JPG",
            @"C:\Photos\Other\DSC0001.JPG"
        };

        Assert.Null(ComparePairService.Find(files, @"C:\Photos\Other\DSC0001.JPG"));
    }

    [Fact(DisplayName = "Compare pair detection rejects an incomplete pair")]
    public void FindRejectsSingleFileList()
    {
        Assert.Null(ComparePairService.Find([@"C:\Photos\DSC0001.JPG"], @"C:\Photos\DSC0001.JPG"));
    }

    [Fact(DisplayName = "BuildIndex matches Find for every path in the catalog")]
    public void BuildIndexMatchesFindForEveryPath()
    {
        var files = new List<string>
        {
            @"C:\Photos\DSC0001.JPG",
            @"C:\Photos\DSC0001 (1).JPG",
            @"C:\Photos\DSC0001 (2).JPG",
            @"C:\Photos\DSC0002.JPG",
            @"C:\Photos\Other\DSC0001.JPG",
            @"C:\Photos\DSC0003.PNG",
            @"C:\Photos\DSC0003 (1).PNG"
        };

        var index = ComparePairService.BuildIndex(files);

        foreach (var path in files)
        {
            var expected = ComparePairService.Find(files, path);
            var found = index.TryGetValue(path, out var pair);
            Assert.Equal(expected is not null, found);
            if (expected is not null)
            {
                Assert.Equal(expected.Value.Left, pair.Left);
                Assert.Equal(expected.Value.Right, pair.Right);
            }
        }
    }

    [Fact(DisplayName = "BuildIndex pairs the original with the smallest-numbered variant")]
    public void BuildIndexPairsOriginalWithSmallestNumbered()
    {
        var files = new List<string>
        {
            @"C:\Photos\DSC0001.JPG",
            @"C:\Photos\DSC0001 (2).JPG",
            @"C:\Photos\DSC0001 (1).JPG"
        };

        var index = ComparePairService.BuildIndex(files);

        Assert.True(index.TryGetValue(@"C:\Photos\DSC0001.JPG", out var fromOriginal));
        Assert.Equal(@"C:\Photos\DSC0001 (1).JPG", fromOriginal.Right);

        Assert.True(index.TryGetValue(@"C:\Photos\DSC0001 (2).JPG", out var fromNumbered));
        Assert.Equal(@"C:\Photos\DSC0001.JPG", fromNumbered.Left);
        Assert.Equal(@"C:\Photos\DSC0001 (2).JPG", fromNumbered.Right);
    }

    [Fact(DisplayName = "BuildIndex omits a lone original with no numbered variant")]
    public void BuildIndexOmitsUnpairedOriginal()
    {
        var index = ComparePairService.BuildIndex([@"C:\Photos\DSC0001.JPG", @"C:\Photos\DSC0002.JPG"]);
        Assert.Empty(index);
    }

    // Find compares extensions and folders with OrdinalIgnoreCase; BuildIndex's folder+extension
    // grouping must agree, or a pair like these silently stops being detected once BuildIndex
    // replaces Find on the hot path.
    [Theory(DisplayName = "BuildIndex matches Find when extension or folder case differs across entries")]
    [InlineData(@"C:\Photos\DSC0001.JPG", @"C:\Photos\DSC0001 (1).jpg")]
    [InlineData(@"C:\Photos\DSC0001.JPG", @"c:\photos\DSC0001 (1).JPG")]
    public void BuildIndexMatchesFindWithMixedCase(string original, string variant)
    {
        var files = new List<string> { original, variant };

        var expected = ComparePairService.Find(files, original);
        Assert.NotNull(expected);

        var index = ComparePairService.BuildIndex(files);
        Assert.True(index.TryGetValue(original, out var pair));
        Assert.Equal(expected.Value.Left, pair.Left);
        Assert.Equal(expected.Value.Right, pair.Right);
    }
}
