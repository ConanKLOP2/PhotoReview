using PhotoReview.Core.Catalog;

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

    // Q-FMT-WEBP-HEIC: exactly WebP + HEIC/HEIF joined the list; JXL/AVIF are deferred (not listed even though WIC may decode them).
    [Fact]
    public void WebpHeicExtensions_AreExactlyWebpHeicAndHeif()
    {
        Assert.True(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".webp", ".heic", ".heif" }
            .SetEquals(ImageFileTypes.WebpHeicExtensions));
        Assert.Empty(ImageFileTypes.WebpHeicExtensions.Intersect(ImageFileTypes.SupportedExtensions));
        Assert.Empty(ImageFileTypes.WebpHeicExtensions.Intersect(ImageFileTypes.RawExtensions));
    }

    [Theory]
    [InlineData("a.webp")]
    [InlineData("a.WEBP")]
    [InlineData("a.heic")]
    [InlineData(@"C:\iPhone\IMG_0001.HEIC")]
    [InlineData("a.heif")]
    public void IsSupported_WebpHeic_OnlyWhenTheirSwitchIsOn(string path)
    {
        Assert.False(ImageFileTypes.IsSupported(path));
        Assert.False(ImageFileTypes.IsSupported(path, rawEnabled: true));
        Assert.False(ImageFileTypes.IsSupported(path, rawEnabled: true, webpHeicEnabled: false));
        Assert.True(ImageFileTypes.IsSupported(path, rawEnabled: false, webpHeicEnabled: true));
    }

    [Theory]
    [InlineData("a.avif")]
    [InlineData("a.jxl")]
    [InlineData("a.hif")]
    [InlineData("a.psd")]
    public void IsSupported_DeferredFormats_AreNeverListed(string path)
    {
        Assert.False(ImageFileTypes.IsSupported(path, rawEnabled: true, webpHeicEnabled: true));
    }

    [Fact]
    public void IsSupported_WebpHeicSwitch_DoesNotEnableRawOrDisableTheBaseFormats()
    {
        Assert.False(ImageFileTypes.IsSupported("a.cr2", rawEnabled: false, webpHeicEnabled: true));
        Assert.True(ImageFileTypes.IsSupported("a.cr2", rawEnabled: true, webpHeicEnabled: false));
        Assert.True(ImageFileTypes.IsSupported("a.png", rawEnabled: false, webpHeicEnabled: false));
        Assert.False(ImageFileTypes.IsSupported("", rawEnabled: true, webpHeicEnabled: true));
        Assert.False(ImageFileTypes.IsSupported("webp", rawEnabled: true, webpHeicEnabled: true));
    }

    [Theory]
    [InlineData("a.webp", WicImageFormat.WebP)]
    [InlineData("A.WebP", WicImageFormat.WebP)]
    [InlineData("a.heic", WicImageFormat.Heif)]
    [InlineData("a.HEIF", WicImageFormat.Heif)]
    [InlineData("a.jpg", WicImageFormat.None)]
    [InlineData("a.avif", WicImageFormat.None)]
    [InlineData("webp", WicImageFormat.None)]
    [InlineData(@"C:\x.webp\a.png", WicImageFormat.None)]
    [InlineData(null, WicImageFormat.None)]
    public void GetWicFormat_MapsByExtension(string? path, WicImageFormat expected)
    {
        Assert.Equal(expected, ImageFileTypes.GetWicFormat(path));
    }
}

