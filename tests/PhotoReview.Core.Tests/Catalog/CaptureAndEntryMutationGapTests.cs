using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>Mutation-testing gaps in CaptureGroup, CaptureGroupBuilder and CatalogEntry.</summary>
public sealed class CaptureAndEntryMutationGapTests
{
    [Fact]
    public void CaptureGroup_EqualsNull_IsFalseWithoutThrowing()
    {
        var group = new CaptureGroup(@"C:\a.jpg", @"C:\a.cr2");

        Assert.False(group.Equals((CaptureGroup?)null));
        Assert.False(group.Equals((object?)null));
    }

    [Fact]
    public void CaptureGroup_GroupsDifferingInOnePathOrSidecar_AreNotEqual()
    {
        var group = new CaptureGroup(@"C:\a.jpg", @"C:\a.cr2", @"C:\a.xmp");

        Assert.NotEqual(group, new CaptureGroup(@"C:\b.jpg", @"C:\a.cr2", @"C:\a.xmp"));
        Assert.NotEqual(group, new CaptureGroup(@"C:\a.jpg", @"C:\b.cr2", @"C:\a.xmp"));
        Assert.NotEqual(group, new CaptureGroup(@"C:\a.jpg", @"C:\a.cr2", @"C:\b.xmp"));
        Assert.NotEqual(group, new CaptureGroup(@"C:\a.jpg", @"C:\a.cr2"));
    }

    [Fact]
    public void CaptureGroup_SamePathsInDifferentCase_AreEqualWithEqualHashCodes()
    {
        var lower = new CaptureGroup(@"C:\photos\a.jpg", @"C:\photos\a.cr2", @"C:\photos\a.xmp");
        var upper = new CaptureGroup(@"C:\PHOTOS\A.JPG", @"C:\PHOTOS\A.CR2", @"C:\PHOTOS\A.XMP");

        Assert.Equal(lower, upper);
        Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
    }

    [Fact]
    public void CaptureGroup_DifferentGroups_HaveDifferentHashCodes()
    {
        var a = new CaptureGroup(@"C:\a.jpg", @"C:\a.cr2");
        var b = new CaptureGroup(@"C:\b.jpg", @"C:\b.cr2");
        var withSidecar = new CaptureGroup(@"C:\a.jpg", @"C:\a.cr2", @"C:\a.xmp");

        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a.GetHashCode(), withSidecar.GetHashCode());
    }

    [Fact]
    public void Build_SidecarThatIsNotAnXmpFile_IsNotAttachedToTheGroup()
    {
        var group = Assert.Single(CaptureGroupBuilder.Build(
            [@"C:\shots\a.jpg", @"C:\shots\a.cr2"],
            [@"C:\shots\a.txt", @"C:\shots\a.cr2.txt", "  ", @"C:\shots\a"]));

        Assert.Null(group.XmpPath);
    }

    [Fact]
    public void Build_XmpSidecarWithMatchingName_IsAttached()
    {
        var group = Assert.Single(CaptureGroupBuilder.Build(
            [@"C:\shots\a.jpg", @"C:\shots\a.cr2"],
            [@"C:\shots\a.xmp"]));

        Assert.Equal(@"C:\shots\a.xmp", group.XmpPath);
    }

    [Fact]
    public void WithMetadata_SuppliedDimensionsReplaceTheKnownOnesEvenWhenTheStatIsUnchanged()
    {
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = new CatalogEntry(@"C:\a.jpg").WithMetadata(10, stamp, 100, 50);

        var updated = entry.WithMetadata(10, stamp, 200, 80);

        Assert.Equal(200, updated.Width);
        Assert.Equal(80, updated.Height);
    }

    [Fact]
    public void WithMetadata_UnsuppliedDimensions_AreKeptWhileTheStatIsUnchangedAndDroppedWhenItChanged()
    {
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = new CatalogEntry(@"C:\a.jpg").WithMetadata(10, stamp, 100, 50);

        var same = entry.WithMetadata(10, stamp);
        var otherLength = entry.WithMetadata(11, stamp);
        var otherStamp = entry.WithMetadata(10, stamp.AddSeconds(1));

        Assert.Equal((100, 50), (same.Width, same.Height));
        Assert.Equal((null, null), (otherLength.Width, otherLength.Height));
        Assert.Equal((null, null), (otherStamp.Width, otherStamp.Height));
    }
}