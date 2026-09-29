using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Tests.Catalog;

public sealed class CaptureGroupBuilderTests
{
    [Fact]
    public void Build_MatchesSameFolderAndBasenameIgnoringCase_AndIncludesXmp()
    {
        var groups = CaptureGroupBuilder.Build(
            [@"C:\photos\IMG_0001.JPG", @"C:\photos\img_0001.cr3", @"C:\photos\Img_0001.xmp"],
            [@"C:\photos\IMG_0001.xmp"]);

        var group = Assert.Single(groups);
        Assert.Equal(@"C:\photos\IMG_0001.JPG", group.JpegPath);
        Assert.Equal(@"C:\photos\img_0001.cr3", group.RawPath);
        Assert.Equal(@"C:\photos\IMG_0001.xmp", group.XmpPath);
        Assert.Equal(3, group.Paths.Count);
    }

    [Fact]
    public void Build_LeavesUnpairedAndAmbiguousFilesSeparate()
    {
        var groups = CaptureGroupBuilder.Build(
        [
            @"C:\photos\only-raw.nef",
            @"C:\photos\only-jpeg.jpg",
            @"C:\photos\ambiguous.jpg",
            @"C:\photos\ambiguous.jpeg",
            @"C:\photos\ambiguous.arw",
            @"C:\photos\valid.jpg",
            @"C:\photos\valid.nef",
            @"C:\other\valid.cr2"
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(@"C:\photos\valid.jpg", group.JpegPath);
        Assert.Equal(@"C:\photos\valid.nef", group.RawPath);
        Assert.Null(group.XmpPath);
    }

    [Fact]
    public void Build_IgnoresUnmatchedAndAmbiguousSidecars()
    {
        var groups = CaptureGroupBuilder.Build(
            [@"C:\photos\a.jpg", @"C:\photos\a.cr2", @"C:\photos\b.jpg", @"C:\photos\b.cr2"],
            [@"C:\photos\a.xmp", @"C:\photos\a.XMP", @"C:\photos\orphan.xmp"]);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Null(group.XmpPath));
    }

    [Fact]
    public void GroupEntries_UsesSelectedRepresentativeAndKeepsBothMembersAddressable()
    {
        var jpeg = new CatalogEntry(@"C:\photos\a.jpg") { Length = 10, Width = 3000 };
        var raw = new CatalogEntry(@"C:\photos\a.cr2") { Length = 100, Width = 6000 };
        var other = new CatalogEntry(@"C:\photos\b.jpg");
        var catalog = new ReviewCatalog();

        catalog.Reset([jpeg, raw, other], PhotoReview.Core.Model.RawPairMode.PreferJpeg,
            [@"C:\photos\a.xmp"]);

        Assert.Equal(2, catalog.Count);
        Assert.Equal(jpeg.Path, catalog.PathAt(0));
        Assert.Equal(0, catalog.IndexOf(raw.Path));
        Assert.Equal(10, catalog.Find(raw.Path)!.Length);
        Assert.Equal([jpeg.Path, raw.Path, @"C:\photos\a.xmp"], catalog.Current!.CaptureGroup!.Paths);
    }

    [Fact]
    public void GroupEntries_PreferRawUsesRawMetadata_AndExplorerOrderCollapsesMembers()
    {
        var jpeg = new CatalogEntry(@"C:\photos\a.jpg") { Length = 10 };
        var raw = new CatalogEntry(@"C:\photos\a.cr2") { Length = 100 };
        var other = new CatalogEntry(@"C:\photos\b.jpg");
        var catalog = new ReviewCatalog();
        catalog.Reset([jpeg, raw, other], PhotoReview.Core.Model.RawPairMode.PreferRaw);

        Assert.Equal(raw.Path, catalog.PathAt(0));
        Assert.Equal(100, catalog.Find(jpeg.Path)!.Length);
        Assert.True(catalog.ReplaceOrder([other.Path, raw.Path, jpeg.Path]));
        Assert.Equal([other.Path, raw.Path], catalog.Paths);
        Assert.Equal(1, catalog.IndexOf(jpeg.Path));
    }

    [Fact]
    public void Reset_DefaultModeKeepsPairMembersSeparate()
    {
        var catalog = new ReviewCatalog();
        catalog.Reset([@"C:\photos\a.jpg", @"C:\photos\a.cr2"]);

        Assert.Equal(2, catalog.Count);
        Assert.All(catalog.Entries, entry => Assert.Null(entry.CaptureGroup));
    }
}
