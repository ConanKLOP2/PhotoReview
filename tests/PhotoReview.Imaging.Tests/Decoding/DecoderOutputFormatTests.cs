using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.Imaging.TurboJpeg;
using PhotoReview.Imaging.TurboJpeg.Native;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// Decoders must hand the UI thread render-native, fully materialized bitmaps:
/// Bgr32 for opaque JPEG, premultiplied Pbgra32 for alpha, and never a lazy TransformedBitmap.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class DecoderOutputFormatTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "PhotoReview-DecoderOutputFormatTests-" + Guid.NewGuid().ToString("N"));

    public DecoderOutputFormatTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData(0, (ushort)1)]
    [InlineData(40, (ushort)1)]
    [InlineData(0, (ushort)6)]
    [InlineData(40, (ushort)6)]
    public void WicDirectJpegOutputsMaterializedBgr32(int targetWidth, ushort orientation)
    {
        var path = FixtureGenerator.GenerateJpegWithOrientation(
            Path.Combine(_tempDir, $"wic-{targetWidth}-{orientation}.jpg"), 64, 48, orientation);

        var decoded = new WicDirectDecoder().Decode(new DecodeRequest(path, targetWidth));

        AssertMaterialized(decoded, PixelFormats.Bgr32);
    }

    [Fact]
    public void WicDirectPngWithAlphaOutputsPremultipliedPbgra32()
    {
        var path = Path.Combine(_tempDir, "alpha.png");
        const int width = 16;
        const int height = 8;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200;     // B
            pixels[i + 1] = 100; // G
            pixels[i + 2] = 40;  // R
            pixels[i + 3] = 128; // A (half transparent)
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        FixtureGenerator.SavePng(source, path, is32Bit: true);

        var decoded = new WicDirectDecoder().Decode(new DecodeRequest(path, 0));
        var bitmap = AssertMaterialized(decoded, PixelFormats.Pbgra32);

        var first = new byte[4];
        bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), first, 4, 0);
        Assert.Equal(128, first[3]);
        Assert.InRange(first[0], 99, 101); // 200 * 128/255, premultiplied
        Assert.InRange(first[1], 49, 51);
        Assert.InRange(first[2], 19, 21);
    }

    [Theory]
    [InlineData(0, (ushort)1)]
    [InlineData(40, (ushort)1)]
    [InlineData(0, (ushort)6)]
    [InlineData(40, (ushort)6)]
    [InlineData(40, (ushort)5)]
    public void TurboJpegOutputsMaterializedBgr32(int targetWidth, ushort orientation)
    {
        var path = FixtureGenerator.GenerateJpegWithOrientation(
            Path.Combine(_tempDir, $"turbo-{targetWidth}-{orientation}.jpg"), 64, 48, orientation);

        var decoded = new TurboJpegDecoder().Decode(new DecodeRequest(path, targetWidth));

        // Scale + rotation run on the worker; the result is Bgr32 (opaque) or Pbgra32 if WIC's
        // scaler chose a premultiplied intermediate - both render without conversion.
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(decoded.PlatformImage);
        Assert.True(bitmap.Format == PixelFormats.Bgr32 || bitmap.Format == PixelFormats.Pbgra32,
            $"Unexpected format {bitmap.Format}");
        AssertMaterialized(decoded, bitmap.Format);

        bool transposed = orientation is >= 5 and <= 8;
        int expectedWidth = targetWidth > 0 ? targetWidth : (transposed ? 48 : 64);
        Assert.Equal(expectedWidth, decoded.PixelWidth);
    }

    [Fact]
    public void WpfOrientedDecodeIsMaterialized()
    {
        var path = FixtureGenerator.GenerateJpegWithOrientation(
            Path.Combine(_tempDir, "wpf-6.jpg"), 64, 48, 6);

        var decoded = new WpfBitmapImageDecoder().Decode(new DecodeRequest(path, 40));

        var bitmap = Assert.IsAssignableFrom<BitmapSource>(decoded.PlatformImage);
        Assert.IsNotType<TransformedBitmap>(bitmap);
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(40, decoded.PixelWidth);
        Assert.Equal((long)bitmap.PixelWidth * bitmap.PixelHeight * 4, decoded.EstimatedBytes);
    }

    [Fact]
    public void MaterializeCopiesPixelsEagerly()
    {
        var writable = new WriteableBitmap(4, 2, 96, 96, PixelFormats.Bgr32, null);
        var red = new byte[4 * 2 * 4];
        for (var i = 0; i < red.Length; i += 4)
        {
            red[i + 2] = 255;
        }

        writable.WritePixels(new Int32Rect(0, 0, 4, 2), red, 16, 0);
        var transformed = new TransformedBitmap(writable, new RotateTransform(90));

        var materialized = WpfImageAdapter.Materialize(transformed);

        // Mutate the source after materializing: a lazy pipeline would observe the change.
        writable.WritePixels(new Int32Rect(0, 0, 4, 2), new byte[4 * 2 * 4], 16, 0);

        Assert.True(materialized.IsFrozen);
        Assert.IsNotType<TransformedBitmap>(materialized);
        Assert.Equal(2, materialized.PixelWidth);
        Assert.Equal(4, materialized.PixelHeight);
        Assert.Equal(PixelFormats.Bgr32, materialized.Format);
        var pixel = new byte[4];
        materialized.CopyPixels(new Int32Rect(1, 3, 1, 1), pixel, 4, 0);
        Assert.Equal(255, pixel[2]);
    }

    [Theory]
    [InlineData(6000, 4000, 2190, 1460, 3, 8)] // 2250x1500: smallest eighth still >= target
    [InlineData(6000, 4000, 3000, 2000, 1, 2)] // exact half
    [InlineData(6000, 4000, 2251, 1501, 1, 2)] // just above 3/8
    [InlineData(6000, 4000, 1500, 1000, 1, 4)]
    [InlineData(6000, 4000, 700, 466, 1, 8)]
    [InlineData(6000, 4000, 5999, 3999, 1, 1)] // nothing smaller covers the target
    [InlineData(4000, 6000, 1460, 2190, 3, 8)] // portrait
    public void TurboJpegSelectsSmallestScalingFactorCoveringTarget(
        int origW, int origH, int targetW, int targetH, int expectedNum, int expectedDenom)
    {
        var factor = TurboJpegDecoder.SelectScalingFactor(origW, origH, targetW, targetH);

        Assert.Equal(new TjScalingFactor(expectedNum, expectedDenom), factor);
        long scaledW = ((long)origW * factor.Num + factor.Denom - 1) / factor.Denom;
        long scaledH = ((long)origH * factor.Num + factor.Denom - 1) / factor.Denom;
        Assert.True(scaledW >= targetW && scaledH >= targetH, "Scaled decode must never undershoot the target.");
    }

    private static BitmapSource AssertMaterialized(IDecodedImage decoded, PixelFormat expectedFormat)
    {
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(decoded.PlatformImage);
        Assert.IsNotType<TransformedBitmap>(bitmap);
        Assert.IsNotType<FormatConvertedBitmap>(bitmap);
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(expectedFormat, bitmap.Format);
        Assert.Equal((long)bitmap.PixelWidth * bitmap.PixelHeight * 4, decoded.EstimatedBytes);
        return bitmap;
    }
}
