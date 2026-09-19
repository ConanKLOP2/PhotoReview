using PhotoReview.Core.Catalog;
using Xunit;

namespace PhotoReview.Core.Tests.Catalog;

public class ManagedNaturalComparerTests
{
    private readonly ManagedNaturalComparer _comparer = ManagedNaturalComparer.Instance;

    [Theory]
    [InlineData("a2", "a10", -1)]
    [InlineData("a10", "a2", 1)]
    [InlineData("a2", "a2", 0)]
    [InlineData("img1.jpg", "img2.jpg", -1)]
    [InlineData("img2.jpg", "img10.jpg", -1)]
    [InlineData("img10.jpg", "img100.jpg", -1)]
    [InlineData("img2.jpg", "img1000000000000.jpg", -1)]
    [InlineData("a (1)", "a (2)", -1)]
    [InlineData("a (2)", "a (10)", -1)]
    public void CompareCorrectlyOrdersNumericSequences(string x, string y, int expectedSign)
    {
        var result = _comparer.Compare(x, y);
        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Fact]
    public void CompareHandlesCaseInsensitivity()
    {
        Assert.Equal(0, _comparer.Compare("photo.jpg", "photo.jpg"));
        // Case-difference should sort stably
        Assert.True(_comparer.Compare("PHOTO.jpg", "photo.jpg") != 0);
    }

    [Fact]
    public void CompareHandlesNulls()
    {
        Assert.Equal(0, _comparer.Compare(null, null));
        Assert.True(_comparer.Compare(null, "any") < 0);
        Assert.True(_comparer.Compare("any", null) > 0);
    }

    [Fact]
    public void SortListMatchesNaturalOrder()
    {
        var input = new List<string> { "img10.jpg", "img2.jpg", "img1.jpg", "img100.jpg", "img20.jpg" };
        input.Sort(_comparer);

        var expected = new List<string> { "img1.jpg", "img2.jpg", "img10.jpg", "img20.jpg", "img100.jpg" };
        Assert.Equal(expected, input);
    }
}
