using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.TestSupport;
using PhotoReview.TestSupport.Windows;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// WP-03: <see cref="EmbeddedThumbnailReader"/> reads through WIC COM instead of WPF. The pre-WP-03 WPF implementation is
/// rebuilt in the test (<see cref="LegacyTryRead"/>: <c>BitmapFrame.Thumbnail</c> -> Bgra32 -> <c>ExifOrientation.Apply</c>)
/// and every thumbnail must come out byte-identical (straight vs premultiplied BGRA is the same bytes at A = 255), with the
/// same orientation, sizes, downscaled flag and backend label, over 8 orientations x several thumbnail sizes.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class EmbeddedThumbnailReaderWicParityTests : IDisposable
{
    private readonly TempRoot _root = new("wp03-thumb");

    public void Dispose() => _root.Dispose();

    public static TheoryData<int, int, int> Cases()
    {
        var data = new TheoryData<int, int, int>();
        foreach (var (w, h) in new[] { (16, 8), (15, 7), (1, 1), (1, 6), (40, 30) })
            for (var orientation = 1; orientation <= 8; orientation++)
                data.Add(w, h, orientation);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TryRead_MatchesTheWpfReaderByteForByte(int thumbW, int thumbH, int orientation)
    {
        var path = JpegWithThumbnail(thumbW, thumbH, orientation);

        var legacy = LegacyTryRead(path);
        var wpf = EmbeddedThumbnailReader.TryRead(path, WpfBitmapSourceCodec.Instance);
        var pix = EmbeddedThumbnailReader.TryRead(path, PixelBufferImageCodec.Instance);

        Assert.NotNull(legacy);
        Assert.NotNull(wpf);
        Assert.NotNull(pix);
        var pixels = Assert.IsType<PixelBuffer>(pix.PlatformImage);
        try
        {
            Assert.Equal(PixelLayout.Pbgra32, pixels.Layout);
            PixelAssert.Equal(legacy.Bitmap, pixels);
            var bitmap = Assert.IsAssignableFrom<BitmapSource>(wpf.PlatformImage);
            Assert.True(bitmap.IsFrozen);
            PixelAssert.Equal(bitmap, pixels);
            foreach (var image in new[] { wpf, pix })
            {
                Assert.Equal((legacy.Bitmap.PixelWidth, legacy.Bitmap.PixelHeight), (image.PixelWidth, image.PixelHeight));
                Assert.Equal(legacy.Orientation, image.Orientation);
                Assert.Equal((legacy.OriginalWidth, legacy.OriginalHeight), (image.OriginalWidth, image.OriginalHeight));
                Assert.True(image.Downscaled);
                Assert.Equal(DecoderBackend.Wpf, image.ActualBackend);
                Assert.Null(image.Exif);
            }
        }
        finally
        {
            pixels.Dispose();
        }
    }

    [Fact]
    public void TryRead_OneArgumentBridge_IsTheWpfCodec()
    {
        var path = JpegWithThumbnail(16, 8, 6);

        var image = EmbeddedThumbnailReader.TryRead(path);

        Assert.NotNull(image);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.PlatformImage);
        Assert.Equal(PixelFormats.Pbgra32, bitmap.Format);
        Assert.Equal((8, 16), (image.PixelWidth, image.PixelHeight));
    }

    [Fact]
    public void TryRead_SourcesWithoutAThumbnail_AreNullLikeBefore()
    {
        var png = FixtureGenerator.GeneratePng(_root.Combine("p.png"), 20, 10);
        var plainJpeg = FixtureGenerator.GenerateGradientJpeg(_root.Combine("plain.jpg"), 20, 10);

        foreach (var path in new[] { png, plainJpeg })
        {
            Assert.Null(LegacyTryRead(path));
            Assert.Null(EmbeddedThumbnailReader.TryRead(path, PixelBufferImageCodec.Instance));
        }
    }

    private string JpegWithThumbnail(int thumbW, int thumbH, int orientation)
    {
        var path = _root.Combine($"t{thumbW}x{thumbH}-o{orientation}.jpg");
        using var pattern = PixelAssert.CreatePattern(thumbW, thumbH, PixelLayout.Bgr32, seed: thumbW * 100 + thumbH * 10 + orientation);
        var thumb = new FormatConvertedBitmap(PixelAssert.ToBitmapSource(pattern), PixelFormats.Bgr24, null, 0);
        thumb.Freeze();
        var metadata = new BitmapMetadata("jpg");
        if (orientation != 1) metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(FixtureGenerator.CreateGradientCheckerboard(48, 32), thumb, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private sealed record Legacy(BitmapSource Bitmap, int Orientation, int OriginalWidth, int OriginalHeight);

    /// <summary>The pre-WP-03 reader (WPF managed API), verbatim in behaviour.</summary>
    private static Legacy? LegacyTryRead(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.SequentialScan);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) return null;
        var frame = decoder.Frames[0];
        var thumbnail = frame.Thumbnail;
        if (thumbnail is null) return null;
        var width = thumbnail.PixelWidth;
        var height = thumbnail.PixelHeight;
        var converted = thumbnail.Format == PixelFormats.Bgra32 ? thumbnail : new FormatConvertedBitmap(thumbnail, PixelFormats.Bgra32, null, 0);
        var buffer = new byte[width * 4 * height];
        converted.CopyPixels(buffer, width * 4, 0);
        var materialized = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, buffer, width * 4);
        materialized.Freeze();
        var orientation = ExifOrientation.Read(frame.Metadata as BitmapMetadata);
        var oriented = ExifOrientation.Apply(materialized, orientation);
        var transposed = ExifOrientation.IsTransposed(orientation);
        return new Legacy(oriented, orientation, transposed ? frame.PixelHeight : frame.PixelWidth,
            transposed ? frame.PixelWidth : frame.PixelHeight);
    }
}
