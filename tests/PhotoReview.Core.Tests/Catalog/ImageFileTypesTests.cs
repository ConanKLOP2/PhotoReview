using PhotoReview.Core.Catalog;
using Xunit;

namespace PhotoReview.Core.Tests.Catalog;

[Trait("Category", "HotPath")]
public class ImageFileTypesTests
{
    [Fact]
    public void RawExtensions_AreExactlyTheEightApprovedFormats()
    {
        Assert.True(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2"
        }.SetEquals(ImageFileTypes.RawExtensions));
        Assert.False(ImageFileTypes.IsSupported("photo.nrw", rawEnabled: true));
        Assert.False(ImageFileTypes.IsSupported("photo.pef", rawEnabled: true));
    }

    [Theory]
    [InlineData("photo.jpg", true)]
    [InlineData("photo.JPEG", true)]
    [InlineData("IMAGE.PNG", true)]
    [InlineData("bitmap.bmp", true)]
    [InlineData("anim.gif", true)]
    [InlineData("document.pdf", false)]
    [InlineData("executable.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupportedValidatesExtensions(string? path, bool expected)
    {
        Assert.Equal(expected, ImageFileTypes.IsSupported(path!));
    }
}

