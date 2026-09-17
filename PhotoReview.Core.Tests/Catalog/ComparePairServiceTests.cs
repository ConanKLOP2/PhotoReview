using PhotoReview.Core.Catalog;
using Xunit;

namespace PhotoReview.Core.Tests.Catalog;

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
}
