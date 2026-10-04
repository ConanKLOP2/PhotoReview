using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoReview.Imaging.Tests.Decoding;

[Trait("Category", "HotPath")]
public sealed class WpfDecodedImageTests
{
    [Fact(DisplayName = "WpfDecodedImage wraps BitmapSource and freezes unfrozen source")]
    public void WrapsAndFreezesBitmapSource()
    {
        var bitmap = new RenderTargetBitmap(10, 20, 96, 96, PixelFormats.Pbgra32);
        Assert.False(bitmap.IsFrozen);

        var decoded = new WpfDecodedImage(bitmap, downscaled: true, orientation: 6);

        Assert.Equal(10, decoded.PixelWidth);
        Assert.Equal(20, decoded.PixelHeight);
        Assert.True(decoded.Downscaled);
        Assert.Equal(6, decoded.Orientation);
        Assert.Equal(10 * 20 * 4, decoded.EstimatedBytes);
        Assert.Same(bitmap, decoded.PlatformImage);
        Assert.True(bitmap.IsFrozen);
    }

    [Fact(DisplayName = "WpfDecodedImage throws ArgumentNullException on null source")]
    public void ThrowsOnNullSource()
    {
        Assert.Throws<ArgumentNullException>(() => new WpfDecodedImage(null!));
    }
}

