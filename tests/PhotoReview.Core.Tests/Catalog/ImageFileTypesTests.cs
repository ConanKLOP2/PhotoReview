using PhotoReview.Core.Catalog;
using Xunit;

namespace PhotoReview.Core.Tests.Catalog;

[Trait("Category", "HotPath")]
public class ImageFileTypesTests
{
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

