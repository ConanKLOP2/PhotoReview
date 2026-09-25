using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Tests.Catalog;

public sealed class CatalogEntryTests
{
    private static readonly DateTime T = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Theory(DisplayName = "An entry cannot get an empty path through the constructor or a with-expression")]
    [InlineData("")]
    [InlineData("   ")]
    public void Path_RejectsBlank_ViaConstructorAndWith(string blank)
    {
        Assert.Throws<ArgumentException>(() => new CatalogEntry(blank));
        var entry = new CatalogEntry("a.jpg");
        Assert.Throws<ArgumentException>(() => entry with { Path = blank });
    }

    [Fact(DisplayName = "Refreshing metadata of an unchanged file keeps its known dimensions")]
    public void WithMetadata_UnchangedStat_KeepsDimensions()
    {
        var entry = new CatalogEntry("a.jpg").WithMetadata(10, T, 4000, 3000);

        var refreshed = entry.WithMetadata(10, T);

        Assert.Equal(4000, refreshed.Width);
        Assert.Equal(3000, refreshed.Height);
    }

    [Fact(DisplayName = "Refreshing metadata of a changed file drops the stale dimensions unless new ones are given")]
    public void WithMetadata_ChangedStat_DropsOrReplacesDimensions()
    {
        var entry = new CatalogEntry("a.jpg").WithMetadata(10, T, 4000, 3000);

        var changed = entry.WithMetadata(11, T);
        var replaced = entry.WithMetadata(11, T, 10, 20);

        Assert.Null(changed.Width);
        Assert.Null(changed.Height);
        Assert.Equal((10, 20), (replaced.Width, replaced.Height));
    }
}
