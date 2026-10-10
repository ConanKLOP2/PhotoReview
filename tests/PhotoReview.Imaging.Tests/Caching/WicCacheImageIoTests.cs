using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Pixels;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// WP-04: tests trực tiếp cho <see cref="WicImageEncoder"/> / <see cref="WicCacheImageReader"/> (trước đó chỉ được kiểm qua
/// PreviewCacheFile / DiskCacheStore): chuyển BGRX -> BGR theo dải, tham số chất lượng JPEG, chọn layout khi đọc, hộp thu nhỏ,
/// kiểm kích thước header và phân loại lỗi (hết bộ nhớ không phải entry hỏng).
/// </summary>
public sealed class WicCacheImageIoTests
{
    private static byte[] EncodePng(PixelBuffer pixels)
    {
        using var ms = new MemoryStream();
        WicImageEncoder.EncodePng(pixels, ms);
        return ms.ToArray();
    }

    private static byte[] EncodeJpeg(PixelBuffer pixels, int quality)
    {
        using var ms = new MemoryStream();
        WicImageEncoder.EncodeJpeg(pixels, ms, quality);
        return ms.ToArray();
    }

    [Theory(DisplayName = "WP-04: Bgrx32ToBgr24 drops the 4th byte of every pixel (vector body and scalar tail, any width)")]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(64)]
    public void Bgrx32ToBgr24_DropsFourthByte(int pixelCount)
    {
        var source = new byte[pixelCount * 4];
        for (var i = 0; i < source.Length; i++) source[i] = (byte)(i % 4 == 3 ? 0xEE : (i * 7) + 1);
        var destination = new byte[(pixelCount * 3) + 16];

        WicImageEncoder.Bgrx32ToBgr24(source, destination);

        for (var p = 0; p < pixelCount; p++)
        {
            Assert.Equal(source[p * 4], destination[p * 3]);
            Assert.Equal(source[(p * 4) + 1], destination[(p * 3) + 1]);
            Assert.Equal(source[(p * 4) + 2], destination[(p * 3) + 2]);
        }
    }

    [Fact(DisplayName = "WP-04: Bgrx32ToBgr24 refuses a destination shorter than 3 bytes per pixel")]
    public void Bgrx32ToBgr24_TooShortDestination_Throws()
        => Assert.Throws<ArgumentException>(() => WicImageEncoder.Bgrx32ToBgr24(new byte[16], new byte[11]));

    [Theory(DisplayName = "WP-04: EncodeJpeg rejects a quality outside 1..100")]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-5)]
    public void EncodeJpeg_QualityOutOfRange_Throws(int quality)
    {
        using var pixels = LegacyWpfPreviewCache.PatternPixels(16, 16, PixelLayout.Bgr32);
        Assert.Throws<ArgumentOutOfRangeException>(() => EncodeJpeg(pixels, quality));
    }

    [Fact(DisplayName = "WP-04: EncodeJpeg honours the quality: higher quality = a larger payload and a closer picture")]
    public void EncodeJpeg_QualityChangesPayload()
    {
        using var pixels = LegacyWpfPreviewCache.PatternPixels(200, 120, PixelLayout.Bgr32);
        var low = EncodeJpeg(pixels, 30);
        var high = EncodeJpeg(pixels, 95);

        Assert.True(high.Length > low.Length, $"q95 {high.Length} bytes vs q30 {low.Length} bytes");
        using var fromLow = WicCacheImageReader.Decode(low, PixelLayout.Bgr32, DecodeBox.Unbounded, 200, 120, out _);
        using var fromHigh = WicCacheImageReader.Decode(high, PixelLayout.Bgr32, DecodeBox.Unbounded, 200, 120, out _);
        var psnrLow = PixelAssert.Psnr(pixels, fromLow);
        var psnrHigh = PixelAssert.Psnr(pixels, fromHigh);
        Assert.True(psnrHigh > psnrLow + 1, $"PSNR q95 {psnrHigh:F1} dB vs q30 {psnrLow:F1} dB");
    }

    [Fact(DisplayName = "WP-04: an opaque PNG round-trips pixel for pixel and decodes as Bgr32 when no layout is requested")]
    public void OpaquePng_RoundTrips_AsBgr32()
    {
        using var pixels = LegacyWpfPreviewCache.PatternPixels(37, 23, PixelLayout.Bgr32);
        var png = EncodePng(pixels);

        using var decoded = WicCacheImageReader.Decode(png, layout: null, DecodeBox.Unbounded, 0, 0, out var info);

        Assert.Equal(PixelLayout.Bgr32, decoded.Layout);
        Assert.Equal((37, 23), (decoded.Width, decoded.Height));
        Assert.Equal(new WicCacheImageInfo(37, 23, Downscaled: false), info);
        PixelAssert.Equal(pixels, decoded, compareUnusedByte: false);
    }

    [Fact(DisplayName = "WP-04: a PNG written from Pbgra32 pixels carries alpha and decodes as Pbgra32 when no layout is requested")]
    public void AlphaPng_DecodesAsPbgra32_WithAlphaKept()
    {
        using var pixels = PixelBuffer.Allocate(40, 8, PixelLayout.Pbgra32);
        var bytes = LegacyWpfPreviewCache.Pattern(40, 8, alpha: true); // alpha ramps 255 -> 0 across the row
        for (var y = 0; y < 8; y++) bytes.AsSpan(y * 40 * 4, 40 * 4).CopyTo(pixels.GetRow(y));
        var png = EncodePng(pixels);

        using var decoded = WicCacheImageReader.Decode(png, layout: null, DecodeBox.Unbounded, 0, 0, out _);

        Assert.Equal(PixelLayout.Pbgra32, decoded.Layout);
        Assert.False(PixelOps.IsFullyOpaque(decoded));
    }

    [Fact(DisplayName = "WP-04: a box smaller than the frame shrinks it (never upscales) and reports Downscaled with the source size")]
    public void Box_ShrinksButNeverUpscales()
    {
        using var pixels = LegacyWpfPreviewCache.PatternPixels(400, 200, PixelLayout.Bgr32);
        var png = EncodePng(pixels);

        using var small = WicCacheImageReader.Decode(png, PixelLayout.Bgr32, new DecodeBox(100, 0), 0, 0, out var smallInfo);
        Assert.Equal(100, small.Width);
        Assert.Equal(50, small.Height);
        Assert.Equal(new WicCacheImageInfo(400, 200, Downscaled: true), smallInfo);

        using var same = WicCacheImageReader.Decode(png, PixelLayout.Bgr32, new DecodeBox(4000, 0), 0, 0, out var sameInfo);
        Assert.Equal((400, 200), (same.Width, same.Height));
        Assert.False(sameInfo.Downscaled);
    }

    [Theory(DisplayName = "WP-04: a payload whose frame size differs from the expected (header) size is InvalidDataException, in either dimension")]
    [InlineData(31, 20)]
    [InlineData(32, 21)]
    public void ExpectedSizeMismatch_Throws(int expectedWidth, int expectedHeight)
    {
        using var pixels = LegacyWpfPreviewCache.PatternPixels(32, 20, PixelLayout.Bgr32);
        var png = EncodePng(pixels);

        Assert.Throws<InvalidDataException>(() => WicCacheImageReader.Decode(png, PixelLayout.Bgr32, DecodeBox.Unbounded, expectedWidth, expectedHeight, out _));
    }

    [Fact(DisplayName = "WP-04: an empty payload is InvalidDataException; a payload WIC cannot parse is a COMException (both are cache-entry failures)")]
    public void EmptyOrGarbagePayload_IsEntryFailure()
    {
        var empty = Assert.Throws<InvalidDataException>(() => WicCacheImageReader.Decode([], PixelLayout.Bgr32, DecodeBox.Unbounded, 0, 0, out _));
        Assert.True(DiskCacheStore.IsCacheEntryFailure(empty));

        var garbage = Assert.ThrowsAny<Exception>(() => WicCacheImageReader.Decode(new byte[64], PixelLayout.Bgr32, DecodeBox.Unbounded, 0, 0, out _));
        Assert.True(DiskCacheStore.IsCacheEntryFailure(garbage), garbage.GetType().FullName);
    }

    [Fact(DisplayName = "WP-04: only E_OUTOFMEMORY is classified as out-of-memory (a resource failure, never 'corrupt entry')")]
    public void IsOutOfMemory_OnlyForEOutOfMemory()
    {
        Assert.True(WicCacheImageReader.IsOutOfMemory(new FakeCom(unchecked((int)0x8007000E))));
        Assert.False(WicCacheImageReader.IsOutOfMemory(new FakeCom(unchecked((int)0x88982F60))));
        Assert.False(DiskCacheStore.IsCacheEntryFailure(new InsufficientMemoryException()));
    }

    [Fact(DisplayName = "WP-04: encoding a disposed buffer or a null stream fails fast without leaving a half-written file")]
    public void Encode_BadArguments_Throw()
    {
        var pixels = LegacyWpfPreviewCache.PatternPixels(8, 8, PixelLayout.Bgr32);
        pixels.Dispose();
        Assert.Throws<ObjectDisposedException>(() => EncodePng(pixels));

        using var live = LegacyWpfPreviewCache.PatternPixels(8, 8, PixelLayout.Bgr32);
        Assert.Throws<ArgumentNullException>(() => WicImageEncoder.EncodePng(live, null!));
    }

    private sealed class FakeCom(int hresult) : COMException("fake", hresult);
}
