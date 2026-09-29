using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Tests.Catalog;

public sealed class CaptureGroupTests
{
    [Fact]
    public void Equals_SamePathsIgnoringCase_AreEqualWithSameHashCode()
    {
        var left = new CaptureGroup(@"C:\photos\a.jpg", @"C:\photos\a.cr2", @"C:\photos\a.xmp");
        var right = new CaptureGroup(@"C:\photos\A.JPG", @"C:\photos\A.CR2", @"C:\photos\A.XMP");

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.Single(new HashSet<CaptureGroup> { left, right });
    }

    [Fact]
    public void Equals_DifferentRawOrSidecar_AreNotEqual()
    {
        var group = new CaptureGroup(@"C:\photos\a.jpg", @"C:\photos\a.cr2", @"C:\photos\a.xmp");

        Assert.NotEqual(group, new CaptureGroup(@"C:\photos\a.jpg", @"C:\photos\a.nef", @"C:\photos\a.xmp"));
        Assert.NotEqual(group, new CaptureGroup(@"C:\photos\a.jpg", @"C:\photos\a.cr2"));
        Assert.NotEqual(new CaptureGroup(@"C:\photos\a.jpg", @"C:\photos\a.cr2"), group);
    }
}
