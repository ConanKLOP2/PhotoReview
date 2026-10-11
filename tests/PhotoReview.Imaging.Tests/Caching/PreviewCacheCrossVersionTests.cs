using PhotoReview.Core.Model;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Pixels;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// WP-04: định dạng cache đĩa không đổi khi encode/decode chuyển từ WPF sang WIC. Hai chiều: file bản cũ (fixture commit trong
/// <c>tests/Fixtures/PreviewCache</c>, sinh bằng mã trước WP-04, và fixture sinh trong test bằng
/// <see cref="LegacyWpfPreviewCache"/>) đọc được bằng mã mới; file mã mới ghi đọc được bằng đường WPF cũ (BitmapImage) và
/// giống từng byte với file bản cũ ghi từ cùng pixel.
/// </summary>
public sealed class PreviewCacheCrossVersionTests : IDisposable
{
    private readonly TempRoot _root = new("PreviewCacheCrossVersion");

    public void Dispose() => _root.Dispose();

    private static ExifSummary FixtureExif() => ExifSummary.Create("2024:05:06 07:08:09", "Canon", "EOS R5", "RF24-70mm F2.8 L IS USM", 400,
        new ExifRational(50, 1), new ExifRational(28, 10), new ExifRational(1, 250))!;

    [Theory(DisplayName = "WP-04: a committed pre-WP-04 entry reads with the new WIC path: same header fields and the same pixels the WPF reader produced")]
    [InlineData("v7-exif-bgr32-96x64.pv4", DecoderBackend.WicDirect, 6, 6000, 4000, true)]
    [InlineData("v7-noexif-pbgra32-96x64.pv4", DecoderBackend.TurboJpeg, 1, 96, 64, false)]
    public void CommittedOldFixture_ReadsWithNewCode_IdenticalToOldReader(string name, DecoderBackend backend, int orientation,
        int originalWidth, int originalHeight, bool hasExif)
    {
        var path = LegacyWpfPreviewCache.FixturePath(name);
        var bytes = File.ReadAllBytes(path);

        var entry = PreviewCacheFile.Read(path);
        using var pixels = entry.Pixels;
        Assert.Equal(backend, entry.ActualBackend);
        Assert.Equal(orientation, entry.Orientation);
        Assert.Equal(originalWidth, entry.OriginalWidth);
        Assert.Equal(originalHeight, entry.OriginalHeight);
        Assert.Equal(bytes.LongLength, entry.FileBytes);
        Assert.Equal(hasExif ? FixtureExif() : null, entry.Exif);
        Assert.Equal(PixelLayout.Bgr32, pixels.Layout);
        Assert.Equal((96, 64), (pixels.Width, pixels.Height));

        var old = LegacyWpfPreviewCache.Read(bytes);
        using var reference = LegacyWpfPreviewCache.ToPixels(old.Bitmap, PixelLayout.Bgr32);
        AssertSamePixels(reference, pixels);

        using var source = LegacyWpfPreviewCache.PatternPixels(96, 64, PixelLayout.Bgr32);
        var psnr = PixelAssert.Psnr(source, pixels);
        Assert.True(psnr >= 20, $"decoded fixture resembles its source pattern (PSNR {psnr:F1} dB; sharp red checkerboard edges + chroma subsampling cap it)");
    }

    [Theory(DisplayName = "WP-04: the new writer reproduces a committed pre-WP-04 entry byte for byte from the same pixels and header fields")]
    [InlineData("v7-exif-bgr32-96x64.pv4", PixelLayout.Bgr32, DecoderBackend.WicDirect, 6, 6000, 4000, true)]
    [InlineData("v7-noexif-pbgra32-96x64.pv4", PixelLayout.Pbgra32, DecoderBackend.TurboJpeg, 1, 0, 0, false)]
    public async Task NewWriter_ReproducesCommittedOldFixtureBytes(string name, PixelLayout layout, DecoderBackend backend, int orientation,
        int originalWidth, int originalHeight, bool hasExif)
    {
        var path = Path.Combine(_root.Dir("cache"), "new.pv4");
        using var pixels = LegacyWpfPreviewCache.PatternPixels(96, 64, layout);

        await PreviewCacheFile.WriteAtomicallyAsync(pixels, backend, orientation, originalWidth, originalHeight, path, exif: hasExif ? FixtureExif() : null);

        Assert.Equal(File.ReadAllBytes(LegacyWpfPreviewCache.FixturePath(name)), File.ReadAllBytes(path));
    }

    [Theory(DisplayName = "WP-04: entries the new code writes are byte-identical to what the WPF writer produced for the same bitmap")]
    [InlineData(96, 64, false)]
    [InlineData(333, 217, false)]
    [InlineData(1, 1, false)]
    [InlineData(1, 37, true)]
    [InlineData(1200, 800, true)]
    public async Task NewWrite_ByteIdenticalToOldWpfWrite(int width, int height, bool premultiplied)
    {
        var format = premultiplied ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
        var bitmap = LegacyWpfPreviewCache.PatternBitmap(width, height, format);
        var expected = LegacyWpfPreviewCache.WriteV7(bitmap, DecoderBackend.Wpf, 3, 4 * width, 4 * height, FixtureExif());

        var path = Path.Combine(_root.Dir("cache"), $"{width}x{height}.pv4");
        using var pixels = LegacyWpfPreviewCache.ToPixels(bitmap, premultiplied ? PixelLayout.Pbgra32 : PixelLayout.Bgr32);
        await PreviewCacheFile.WriteAtomicallyAsync(pixels, DecoderBackend.Wpf, 3, 4 * width, 4 * height, path, exif: FixtureExif());

        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "WP-04: an entry the new code writes reads with the old WPF reader: same header, same pixels as the new reader, PSNR vs the old writer >= 45 dB")]
    public async Task NewWrite_ReadableByOldWpfReader()
    {
        const int width = 640, height = 427;
        var path = Path.Combine(_root.Dir("cache"), "new.pv4");
        using (var pixels = LegacyWpfPreviewCache.PatternPixels(width, height, PixelLayout.Bgr32))
            await PreviewCacheFile.WriteAtomicallyAsync(pixels, DecoderBackend.TurboJpeg, 8, 6000, 4000, path, exif: FixtureExif());

        var old = LegacyWpfPreviewCache.Read(File.ReadAllBytes(path));
        Assert.Equal(DecoderBackend.TurboJpeg, old.Backend);
        Assert.Equal(8, old.Orientation);
        Assert.Equal((6000, 4000), (old.OriginalWidth, old.OriginalHeight));
        Assert.Equal(FixtureExif(), old.Exif);
        Assert.Equal(PixelFormats.Bgr32, old.Bitmap.Format);

        var entry = PreviewCacheFile.Read(path);
        using var mine = entry.Pixels;
        using var theirs = LegacyWpfPreviewCache.ToPixels(old.Bitmap, PixelLayout.Bgr32);
        AssertSamePixels(theirs, mine);

        // Quality: decoded new entry vs decoded entry the old writer makes from the same pixels.
        var oldBytes = LegacyWpfPreviewCache.WriteV7(LegacyWpfPreviewCache.PatternBitmap(width, height, PixelFormats.Bgr32), DecoderBackend.TurboJpeg, 8, 6000, 4000, FixtureExif());
        using var oldDecoded = LegacyWpfPreviewCache.ToPixels(LegacyWpfPreviewCache.Read(oldBytes).Bitmap, PixelLayout.Bgr32);
        Assert.True(PixelAssert.Psnr(oldDecoded, mine) >= 45);
    }

    [Fact(DisplayName = "WP-04: a v6 entry (no EXIF block) written by the old layout still reads, as 'no EXIF'")]
    public void OldV6Entry_ReadsWithNewCode()
    {
        var v7 = File.ReadAllBytes(LegacyWpfPreviewCache.FixturePath("v7-exif-bgr32-96x64.pv4"));
        var exifLength = BitConverter.ToUInt16(v7, 24);
        var v6 = v7.AsSpan(0, 24).ToArray().Concat(v7.Skip(26 + exifLength)).ToArray();
        v6[4] = 6;
        var path = Path.Combine(_root.Dir("cache"), "v6.pv4");
        File.WriteAllBytes(path, v6);

        var entry = PreviewCacheFile.Read(path);
        using var pixels = entry.Pixels;
        Assert.Null(entry.Exif);
        Assert.Equal(6, entry.Orientation);
        using var reference = LegacyWpfPreviewCache.ToPixels(LegacyWpfPreviewCache.Read(v6).Bitmap, PixelLayout.Bgr32);
        AssertSamePixels(reference, pixels);
    }

    [Fact(DisplayName = "WP-04: the WPF codec turns a read entry into a frozen Bgr32 BitmapSource; the pixels codec hands back the PixelBuffer itself")]
    public void ReadAsDecodedImage_PlatformImageFollowsCodec()
    {
        var path = LegacyWpfPreviewCache.FixturePath("v7-exif-bgr32-96x64.pv4");

        var wpf = PreviewCacheFile.ReadAsDecodedImage(path, WpfBitmapSourceCodec.Instance);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(wpf.PlatformImage);
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(PixelFormats.Bgr32, bitmap.Format);
        Assert.True(wpf.Downscaled);
        Assert.Equal(96L * 64 * 4, wpf.EstimatedBytes);

        var native = PreviewCacheFile.ReadAsDecodedImage(path, PixelBufferImageCodec.Instance);
        using var pixels = Assert.IsType<PixelBuffer>(native.PlatformImage);
        PixelAssert.Equal(bitmap, pixels);
        Assert.Equal((6, 6000, 4000), (native.Orientation, native.OriginalWidth, native.OriginalHeight));
    }

    [Theory(DisplayName = "WP-04: committed pre-WP-04 PNG thumbnails decode with WIC to the pixels the WPF decoder produced")]
    [InlineData("thumb-bgr32-48x32.png", PixelLayout.Bgr32)]
    [InlineData("thumb-bgra32-48x32.png", PixelLayout.Pbgra32)]
    public void CommittedOldPngThumbnail_ReadsWithNewCode(string name, PixelLayout expectedLayout)
    {
        var path = LegacyWpfPreviewCache.FixturePath(name);

        var image = ThumbnailCache.DecodeDiskThumbnail(path, PixelBufferImageCodec.Instance);
        using var pixels = Assert.IsType<PixelBuffer>(image.PlatformImage);
        Assert.Equal(expectedLayout, pixels.Layout);
        Assert.False(image.Downscaled);
        Assert.Equal((48, 32), (image.OriginalWidth, image.OriginalHeight));

        using var reference = LegacyWpfPreviewCache.ToPixels(LegacyWpfPreviewCache.DecodeWpf(File.ReadAllBytes(path)), expectedLayout);
        AssertSamePixels(reference, pixels);
    }

    [Theory(DisplayName = "WP-04: PNGs the new code writes match the old PngBitmapEncoder (opaque: byte for byte; alpha: same pixels after decode)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewPngWrite_MatchesOldPngWrite(bool alpha)
    {
        const int width = 48, height = 32;
        var bitmap = LegacyWpfPreviewCache.PatternBitmap(width, height, alpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, alpha: false);
        var expected = LegacyWpfPreviewCache.WritePng(bitmap);
        var path = Path.Combine(_root.Dir("thumbs"), "t.png");

        await DiskCacheStore.WriteAtomicallyAsync(new WpfDecodedImage(bitmap), WpfBitmapSourceCodec.Instance, path);

        var actual = File.ReadAllBytes(path);
        if (!alpha) Assert.Equal(expected, actual);
        var layout = alpha ? PixelLayout.Pbgra32 : PixelLayout.Bgr32;
        using var oldPixels = LegacyWpfPreviewCache.ToPixels(LegacyWpfPreviewCache.DecodeWpf(expected), layout);
        using var newPixels = LegacyWpfPreviewCache.ToPixels(LegacyWpfPreviewCache.DecodeWpf(actual), layout);
        AssertSamePixels(oldPixels, newPixels);
    }

    [Fact(DisplayName = "WP-04: a translucent PNG round-trips through the WIC writer and reader within one premultiply step")]
    public async Task TranslucentPng_RoundTripsThroughWic()
    {
        const int width = 40, height = 24;
        var bitmap = LegacyWpfPreviewCache.PatternBitmap(width, height, PixelFormats.Bgra32, alpha: true);
        var path = Path.Combine(_root.Dir("thumbs"), "alpha.png");
        await DiskCacheStore.WriteAtomicallyAsync(new WpfDecodedImage(bitmap), WpfBitmapSourceCodec.Instance, path);

        var image = ThumbnailCache.DecodeDiskThumbnail(path, PixelBufferImageCodec.Instance);
        using var pixels = Assert.IsType<PixelBuffer>(image.PlatformImage);
        Assert.Equal(PixelLayout.Pbgra32, pixels.Layout);
        Assert.False(PixelOps.IsFullyOpaque(pixels));
        using var reference = LegacyWpfPreviewCache.ToPixels(bitmap, PixelLayout.Pbgra32);
        Assert.True(PixelAssert.MeanAbsoluteError(reference, pixels) <= 1.0);
    }

    /// <summary>Cùng bộ giải mã WIC bên dưới: kỳ vọng giống hệt; ngưỡng thẻ WP-04 (lệch trung bình &lt;= 1/255) là giới hạn trên.</summary>
    private static void AssertSamePixels(PixelBuffer expected, PixelBuffer actual)
    {
        Assert.Equal((expected.Width, expected.Height, expected.Layout), (actual.Width, actual.Height, actual.Layout));
        Assert.True(PixelAssert.MeanAbsoluteError(expected, actual) <= 1.0, "mean channel error <= 1/255");
        PixelAssert.Equal(expected, actual, compareUnusedByte: false);
    }
}
