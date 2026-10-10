using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Pixels;
using PhotoReview.TestSupport.Windows;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>WP-03: the WPF app's C-02 codec (temporary in Imaging until WP-06).</summary>
public sealed class WpfBitmapSourceCodecTests
{
    [Fact]
    public void Name_IsWpf() => Assert.Equal("wpf", WpfBitmapSourceCodec.Instance.Name);

    [Theory]
    [InlineData(PixelLayout.Bgr32, 1, 1)]
    [InlineData(PixelLayout.Bgr32, 33, 7)]
    [InlineData(PixelLayout.Pbgra32, 1, 9)]
    [InlineData(PixelLayout.Pbgra32, 70, 45)]
    public void FromPixels_CopiesEveryByteIntoAFrozenBitmap_AndDisposesTheInput(PixelLayout layout, int width, int height)
    {
        using var expected = PixelAssert.CreatePattern(width, height, layout, seed: width * 31 + height);
        var input = PixelAssert.Clone(expected);

        var bitmap = Assert.IsAssignableFrom<BitmapSource>(WpfBitmapSourceCodec.Instance.FromPixels(input));

        Assert.True(input.IsDisposed);
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(layout == PixelLayout.Bgr32 ? PixelFormats.Bgr32 : PixelFormats.Pbgra32, bitmap.Format);
        Assert.Equal((96.0, 96.0), (bitmap.DpiX, bitmap.DpiY));
        Assert.Null(bitmap.Palette);
        PixelAssert.Equal(bitmap, expected);
    }

    [Fact]
    public void FromPixels_DisposedOrNullInput_Throws()
    {
        var disposed = PixelBuffer.Allocate(2, 2, PixelLayout.Bgr32);
        disposed.Dispose();

        Assert.Throws<ObjectDisposedException>(() => WpfBitmapSourceCodec.Instance.FromPixels(disposed));
        Assert.Throws<ArgumentNullException>(() => WpfBitmapSourceCodec.Instance.FromPixels(null!));
    }

    [Theory]
    [InlineData(PixelLayout.Bgr32)]
    [InlineData(PixelLayout.Pbgra32)]
    public void ToPixels_NativeFormats_AreAnOwnedExactCopy(PixelLayout layout)
    {
        using var original = PixelAssert.CreatePattern(13, 11, layout, seed: 5);
        var bitmap = PixelAssert.ToBitmapSource(original);

        using var lease = WpfBitmapSourceCodec.Instance.ToPixels(bitmap);

        Assert.True(lease.Owned);
        Assert.Equal(layout, lease.Pixels.Layout);
        PixelAssert.Equal(original, lease.Pixels);
        lease.Dispose();
        Assert.True(lease.Pixels.IsDisposed);
    }

    [Fact]
    public void ToPixels_StraightBgra_IsPremultipliedLikeWpfConverts()
    {
        using var pattern = PixelAssert.CreatePattern(9, 7, PixelLayout.Pbgra32, seed: 9);
        var straight = new FormatConvertedBitmap(PixelAssert.ToBitmapSource(pattern), PixelFormats.Bgra32, null, 0);
        straight.Freeze();

        using var lease = WpfBitmapSourceCodec.Instance.ToPixels(straight);

        Assert.Equal(PixelLayout.Pbgra32, lease.Pixels.Layout);
        PixelAssert.Equal(new FormatConvertedBitmap(straight, PixelFormats.Pbgra32, null, 0), lease.Pixels);
    }

    [Fact]
    public void ToPixels_OpaqueOtherFormat_BecomesBgr32_AndIndexedBecomesPbgra32()
    {
        using var pattern = PixelAssert.CreatePattern(6, 5, PixelLayout.Bgr32, seed: 3);
        var gray = new FormatConvertedBitmap(PixelAssert.ToBitmapSource(pattern), PixelFormats.Gray8, null, 0);
        var indexed = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Indexed8,
            new BitmapPalette([Colors.Transparent, Colors.Red]), new byte[] { 0, 1 }, 2);

        using var grayLease = WpfBitmapSourceCodec.Instance.ToPixels(gray);
        using var indexedLease = WpfBitmapSourceCodec.Instance.ToPixels(indexed);

        Assert.Equal(PixelLayout.Bgr32, grayLease.Pixels.Layout);
        PixelAssert.Equal(new FormatConvertedBitmap(gray, PixelFormats.Bgr32, null, 0), grayLease.Pixels);
        Assert.Equal(PixelLayout.Pbgra32, indexedLease.Pixels.Layout);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, indexedLease.Pixels.GetRow(0)[..4].ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, indexedLease.Pixels.GetRow(0)[4..].ToArray());
    }

    [Fact]
    public void ToPixels_NotABitmapSource_Throws()
    {
        using var pixels = PixelBuffer.Allocate(1, 1, PixelLayout.Bgr32);
        Assert.Throws<ArgumentException>(() => WpfBitmapSourceCodec.Instance.ToPixels(pixels));
        Assert.Throws<ArgumentNullException>(() => WpfBitmapSourceCodec.Instance.ToPixels(null!));
    }

    [Fact]
    public void RoundTrip_PixelsToBitmapToPixels_IsLossless()
    {
        using var original = PixelAssert.CreatePattern(17, 3, PixelLayout.Pbgra32, seed: 77);
        var bitmap = WpfBitmapSourceCodec.Instance.FromPixels(PixelAssert.Clone(original));

        using var lease = WpfBitmapSourceCodec.Instance.ToPixels(bitmap);

        Assert.Equal(PixelLayout.Pbgra32, lease.Pixels.Layout);
        PixelAssert.Equal(original, lease.Pixels);
    }
}
