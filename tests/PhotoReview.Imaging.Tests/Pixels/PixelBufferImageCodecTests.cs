using PhotoReview.Core.Model;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Pixels;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.Imaging.Tests.Pixels;

/// <summary>WP-02 / C-02: <see cref="PixelBufferImageCodec"/>, <see cref="PixelLease"/>, <see cref="DecodedImage"/>.</summary>
public sealed class PixelBufferImageCodecTests
{
    [Fact]
    public void Codec_IsSingleton_NamedPixels()
    {
        var codec = Assert.IsAssignableFrom<IPlatformImageCodec>(PixelBufferImageCodec.Instance);

        Assert.Same(PixelBufferImageCodec.Instance, codec);
        Assert.Equal("pixels", codec.Name);
    }

    [Fact]
    public void FromPixels_ReturnsTheSameBuffer_WithoutDisposingIt()
    {
        using var pixels = PixelBuffer.Allocate(3, 3, PixelLayout.Bgr32);

        var platform = PixelBufferImageCodec.Instance.FromPixels(pixels);

        Assert.Same(pixels, platform);
        Assert.False(pixels.IsDisposed);
    }

    [Fact]
    public void FromPixels_NullOrDisposed_Throws()
    {
        var disposed = PixelBuffer.Allocate(1, 1, PixelLayout.Bgr32);
        disposed.Dispose();

        Assert.Throws<ArgumentNullException>(() => PixelBufferImageCodec.Instance.FromPixels(null!));
        Assert.Throws<ObjectDisposedException>(() => PixelBufferImageCodec.Instance.FromPixels(disposed));
    }

    [Fact]
    public void ToPixels_LendsTheBuffer_AndDisposingTheLeaseKeepsIt()
    {
        using var pixels = PixelBuffer.Allocate(4, 2, PixelLayout.Pbgra32);

        var lease = PixelBufferImageCodec.Instance.ToPixels(pixels);
        Assert.Same(pixels, lease.Pixels);
        Assert.False(lease.Owned);

        lease.Dispose();
        Assert.False(pixels.IsDisposed);
    }

    [Fact]
    public void ToPixels_RoundTripsFromPixels()
    {
        using var pixels = PixelAssert.CreatePattern(5, 4, PixelLayout.Pbgra32, seed: 3);
        var codec = PixelBufferImageCodec.Instance;

        using var lease = codec.ToPixels(codec.FromPixels(pixels));

        Assert.Same(pixels, lease.Pixels);
    }

    [Fact]
    public void ToPixels_UnknownPlatformImage_ThrowsArgumentException()
    {
        var wpf = PixelAssert.ToBitmapSource(PixelAssert.CreatePattern(2, 2, PixelLayout.Bgr32, seed: 1));

        Assert.Throws<ArgumentException>(() => PixelBufferImageCodec.Instance.ToPixels(wpf));
        Assert.Throws<ArgumentException>(() => PixelBufferImageCodec.Instance.ToPixels("not an image"));
        Assert.Throws<ArgumentNullException>(() => PixelBufferImageCodec.Instance.ToPixels(null!));
    }

    [Fact]
    public void ToPixels_DisposedBuffer_ThrowsObjectDisposed()
    {
        var pixels = PixelBuffer.Allocate(2, 2, PixelLayout.Bgr32);
        pixels.Dispose();

        Assert.Throws<ObjectDisposedException>(() => PixelBufferImageCodec.Instance.ToPixels(pixels));
    }

    [Fact]
    public void Lease_Owned_DisposesItsPixels()
    {
        var pixels = PixelBuffer.Allocate(2, 2, PixelLayout.Bgr32);
        var lease = new PixelLease(pixels, owned: true);

        Assert.True(lease.Owned);
        Assert.Same(pixels, lease.Pixels);
        lease.Dispose();

        Assert.True(pixels.IsDisposed);
        lease.Dispose(); // lần hai an toàn
    }

    [Fact]
    public void Lease_Borrowed_LeavesPixelsAlive()
    {
        using var pixels = PixelBuffer.Allocate(2, 2, PixelLayout.Bgr32);

        new PixelLease(pixels, owned: false).Dispose();

        Assert.False(pixels.IsDisposed);
    }

    [Fact]
    public void Lease_DefaultIsEmpty_AndNullIsRejected()
    {
        var empty = default(PixelLease);
        empty.Dispose();

        Assert.Null(empty.Pixels);
        Assert.False(empty.Owned);
        Assert.Throws<ArgumentNullException>(() => new PixelLease(null!, owned: true));
    }

    [Fact]
    public void DecodedImage_Defaults()
    {
        using var pixels = PixelBuffer.Allocate(6, 4, PixelLayout.Bgr32);

        IDecodedImage image = new DecodedImage(pixels, 6, 4, estimatedBytes: 96);

        Assert.Same(pixels, image.PlatformImage);
        Assert.Equal(6, image.PixelWidth);
        Assert.Equal(4, image.PixelHeight);
        Assert.Equal(96, image.EstimatedBytes);
        Assert.False(image.Downscaled);
        Assert.Equal(1, image.Orientation);
        // Qua interface: phải là giá trị của lớp (WicDirect), không phải mặc định của interface (Wpf).
        Assert.Equal(DecoderBackend.WicDirect, image.ActualBackend);
        Assert.Equal(6, image.OriginalWidth);
        Assert.Equal(4, image.OriginalHeight);
        Assert.Null(image.Exif);
        Assert.False(image.IsDegradedFallback);
    }

    [Fact]
    public void DecodedImage_CarriesEveryArgument()
    {
        using var pixels = PixelBuffer.Allocate(3, 2, PixelLayout.Pbgra32);
        var exif = new ExifSummary { CameraModel = "Cam" };

        IDecodedImage image = new DecodedImage(pixels, 3, 2, 24, downscaled: true, orientation: 6,
            actualBackend: DecoderBackend.TurboJpeg, originalWidth: 3000, originalHeight: 2000, exif: exif, isDegradedFallback: true);

        Assert.True(image.Downscaled);
        Assert.Equal(6, image.Orientation);
        Assert.Equal(DecoderBackend.TurboJpeg, image.ActualBackend);
        Assert.Equal(3000, image.OriginalWidth);
        Assert.Equal(2000, image.OriginalHeight);
        Assert.Same(exif, image.Exif);
        Assert.True(image.IsDegradedFallback);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, -1)]
    [InlineData(0, 900)]
    [InlineData(1200, 0)]
    public void DecodedImage_NonPositiveOriginal_FallsBackToPixelSize(int originalWidth, int originalHeight)
    {
        var image = new DecodedImage(new object(), 40, 30, 4800, originalWidth: originalWidth, originalHeight: originalHeight);

        Assert.Equal(originalWidth > 0 ? originalWidth : 40, image.OriginalWidth);
        Assert.Equal(originalHeight > 0 ? originalHeight : 30, image.OriginalHeight);
    }

    [Fact]
    public void DecodedImage_RejectsInvalidArguments()
    {
        using var pixels = PixelBuffer.Allocate(4, 3, PixelLayout.Bgr32);

        Assert.Throws<ArgumentNullException>(() => new DecodedImage(null!, 4, 3, 48));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedImage(new object(), 0, 3, 48));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedImage(new object(), 4, 0, 48));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedImage(new object(), 4, 3, -1));
        // Kích thước phải khớp buffer (vd. truyền kích thước trước khi xoay 90 độ).
        Assert.Throws<ArgumentException>(() => new DecodedImage(pixels, 3, 4, 48));
        Assert.Throws<ArgumentException>(() => new DecodedImage(pixels, 4, 4, 48));
        Assert.Throws<ArgumentException>(() => new DecodedImage(pixels, 5, 3, 48));
    }

    [Fact]
    public void DecodedImage_AcceptsZeroEstimateAndOneByOne()
    {
        var image = new DecodedImage(new object(), 1, 1, 0);

        Assert.Equal(0, image.EstimatedBytes);
        Assert.Equal(1, image.PixelWidth);
        Assert.Equal(1, image.PixelHeight);
    }
}
