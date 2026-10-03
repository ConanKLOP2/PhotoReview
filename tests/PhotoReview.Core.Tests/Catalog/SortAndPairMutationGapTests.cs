using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>Mutation-testing gaps in ImageSortService, ManagedNaturalComparer and ComparePairService.</summary>
public sealed class SortAndPairMutationGapTests
{
    /// <summary>Plain reverse ordinal order, to prove that an injected comparer is used instead of the natural one.</summary>
    private sealed class ReverseOrdinalComparer : INaturalComparer
    {
        public int Compare(string? x, string? y) => string.CompareOrdinal(y, x);
    }

    [Fact]
    public void SortEntries_InjectedComparer_IsUsedForNameOrder()
    {
        var entries = new[] { new CatalogEntry(@"C:\a.jpg"), new CatalogEntry(@"C:\c.jpg"), new CatalogEntry(@"C:\b.jpg") };

        var sorted = ImageSortService.SortEntries(entries, ImageSortMode.Name, new ReverseOrdinalComparer());

        Assert.Equal([@"C:\c.jpg", @"C:\b.jpg", @"C:\a.jpg"], sorted.Select(e => e.Path));
    }

    [Fact]
    public void SortPaths_InjectedComparer_IsUsedForNameOrder()
    {
        var sorted = ImageSortService.Sort([@"C:\a.jpg", @"C:\c.jpg", @"C:\b.jpg"], ImageSortMode.Name, new ReverseOrdinalComparer());

        Assert.Equal([@"C:\c.jpg", @"C:\b.jpg", @"C:\a.jpg"], sorted);
    }

    [Fact]
    public void SortPaths_UnreadableSize_IsLoggedAndSortsAsSmallest()
    {
        using var root = new TempRoot("sort-gap");
        var log = new MutationRecordingLog();
        var big = root.File("big.jpg", 1, 2, 3, 4, 5);
        var missing = root.Combine("missing.jpg");

        var descending = ImageSortService.Sort([missing, big], ImageSortMode.SizeDescending, null, log);
        var ascending = ImageSortService.Sort([big, missing], ImageSortMode.SizeAscending, null, log);

        Assert.Equal([big, missing], descending);
        Assert.Equal([missing, big], ascending);
        Assert.NotEmpty(log.Errors);
        Assert.All(log.Errors, e =>
        {
            Assert.Contains(missing, e.Message, StringComparison.Ordinal);
            Assert.NotNull(e.Exception);
        });
    }

    [Fact]
    public void Compare_NamesDifferingOnlyInCaseAndLeadingZeros_OrderByTheCaseInsensitiveTextBeforeTheOrdinalTie()
    {
        var comparer = ManagedNaturalComparer.Instance;

        // "a01" and "A1" have the same natural key; the case-insensitive text ("a01" < "a1") decides, not the ordinal 'A' < 'a'.
        Assert.True(comparer.Compare("a01", "A1") < 0);
        Assert.True(comparer.Compare("A1", "a01") > 0);
        // Same text apart from case: the ordinal tie-break puts the upper-case spelling first.
        Assert.True(comparer.Compare("A1", "a1") < 0);
        Assert.Equal(0, comparer.Compare("a1", "a1"));
    }

    [Fact]
    public void BuildNaturalKey_NullOrEmpty_IsEmpty()
    {
        Assert.Equal("", ManagedNaturalComparer.BuildNaturalKey(null!));
        Assert.Equal("", ManagedNaturalComparer.BuildNaturalKey(""));
    }

    [Fact]
    public void BuildIndex_NumberedVariantsInPathOrderDifferentFromNumericOrder_PicksTheLowestNumber()
    {
        var files = new[] { @"C:\p\a.jpg", @"C:\p\a (10).jpg", @"C:\p\a (2).jpg", @"C:\p\a (3).jpg" };

        var index = ComparePairService.BuildIndex(files);
        var found = ComparePairService.Find(files, @"C:\p\a.jpg");

        Assert.Equal((@"C:\p\a.jpg", @"C:\p\a (2).jpg"), index[@"C:\p\a.jpg"]);
        Assert.Equal(found, index[@"C:\p\a.jpg"]);
    }

    [Fact]
    public void BuildIndex_VariantsWithTheSameNumber_KeepTheirPathOrder()
    {
        var files = new[] { @"C:\p\a.jpg", @"C:\p\a (1).jpg", @"C:\p\a (01).jpg" };

        var index = ComparePairService.BuildIndex(files);

        Assert.Equal(@"C:\p\a (01).jpg", index[@"C:\p\a.jpg"].Right);
        Assert.Equal(ComparePairService.Find(files, @"C:\p\a.jpg"), index[@"C:\p\a.jpg"]);
    }

    [Fact]
    public void BuildIndex_ADriveRootEntry_DoesNotThrow()
    {
        var ex = Record.Exception(() => ComparePairService.BuildIndex([@"C:\", @"C:\a.jpg"]));

        Assert.Null(ex);
    }
}