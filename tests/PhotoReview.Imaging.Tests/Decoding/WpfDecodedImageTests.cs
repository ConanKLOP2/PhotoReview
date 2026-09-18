using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Tests.Decoding;

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

    [Fact(DisplayName = "WpfImageAdapter.FromBgra32 creates frozen Bgra32 BitmapSource")]
    public void WpfImageAdapterCreatesFrozenBgra32Bitmap()
    {
        var width = 4;
        var height = 2;
        var stride = width * 4;
        var rawBytes = new byte[stride * height];
        for (var i = 0; i < rawBytes.Length; i++) rawBytes[i] = 128;

        var bitmap = WpfImageAdapter.FromBgra32(rawBytes, width, height, stride);

        Assert.NotNull(bitmap);
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(width, bitmap.PixelWidth);
        Assert.Equal(height, bitmap.PixelHeight);
        Assert.Equal(PixelFormats.Bgra32, bitmap.Format);
    }
}
