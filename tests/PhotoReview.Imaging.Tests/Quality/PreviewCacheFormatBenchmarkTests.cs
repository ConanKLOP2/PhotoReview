using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Tests.Fixtures;
using Xunit;

namespace PhotoReview.Imaging.Tests.Quality;

/// <summary>
/// perf(cache) format decision: measures JPEG (q=92/95, 4:4:4) vs. raw Bgr32 for the preview
/// disk-cache payload on a synthetic "photo-like" 2190x1460 image (a downscaled decode of a
/// synthetic ~24 MP gradient+noise JPEG, matching the real pipeline: encode a large source once,
/// then decode-with-downscale like <c>WpfBitmapImageDecoder</c> does for a real photo).
/// Manual only (excluded by the "Category!=Manual" test gate filter): prints a table to the
/// console; run with
/// `dotnet test --filter "FullyQualifiedName~PreviewCacheFormatBenchmarkTests"` to see it.
/// Not a correctness test -- it only asserts every candidate produced valid, non-empty output,
/// so the numbers below can be pasted verbatim into a PR description.
/// </summary>
[Trait("Category", "Manual")]
public sealed class PreviewCacheFormatBenchmarkTests
{
    private const int SourceWidth = 5760;
    private const int SourceHeight = 3840;
    private const int PreviewWidth = 2190;
    private const int PreviewHeight = 1460;
    private const int Iterations = 5;

    [Fact(DisplayName = "Measure JPEG q92/q95 vs raw Bgr32 for the preview disk-cache payload")]
    public void MeasureCandidateFormats()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview-PreviewFormatBench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Simulate a real photo: encode a large synthetic gradient+noise JPEG once, then
            // decode it back down to preview size the same way the real decode path does
            // (JpegBitmapDecoder honors DecodePixelWidth), instead of directly synthesizing at
            // preview resolution (which would skip the re-compression artifacts a real camera
            // JPEG -> preview decode already carries).
            var sourceJpeg = Path.Combine(tempDir, "source.jpg");
            var sourceBitmap = CreateNoisyGradient(SourceWidth, SourceHeight);
            FixtureGenerator.SaveJpeg(sourceBitmap, sourceJpeg, quality: 92);

            var preview = DecodeDownscaled(sourceJpeg, PreviewWidth);
            var previewBgr32 = ToBgr32(preview);

            Console.WriteLine($"Preview size: {previewBgr32.PixelWidth}x{previewBgr32.PixelHeight}");
            Console.WriteLine($"{"Candidate",-16} {"EncodeMs",10} {"FileBytes",12} {"DecodeMs",10} {"PSNR(dB)",10}");

            var results = new List<(double EncodeMs, long Bytes, double DecodeMs, double Psnr)>();
            foreach (var quality in new[] { 92, 95 })
            {
                var result = MeasureJpeg(previewBgr32, quality, tempDir);
                Print($"Jpeg q{quality}", result);
                results.Add(result);
            }

            var rawResult = MeasureRawBgr32(previewBgr32, tempDir);
            Print("RawBgr32", rawResult);
            results.Add(rawResult);

            // Diagnostic benchmark (see console output for the decision table); it only asserts every candidate produced output.
            Assert.All(results, r => Assert.True(r.Bytes > 0 && r.Psnr > 0, $"invalid candidate result: {r}"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static void Print(string name, (double EncodeMs, long Bytes, double DecodeMs, double Psnr) r)
    {
        var psnrText = double.IsPositiveInfinity(r.Psnr) ? "lossless" : r.Psnr.ToString("F2", CultureInfo.InvariantCulture);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name,-16} {r.EncodeMs,10:F2} {r.Bytes,12:N0} {r.DecodeMs,10:F2} {psnrText,10}"));
    }

    private static (double EncodeMs, long Bytes, double DecodeMs, double Psnr) MeasureJpeg(BitmapSource source, int quality, string tempDir)
    {
        var path = Path.Combine(tempDir, $"preview-q{quality}.jpg");
        double encodeMs = 0, decodeMs = 0;
        long bytes = 0;
        BitmapSource? decoded = null;
        for (var i = 0; i < Iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            FixtureGenerator.SaveJpeg(source, path, quality);
            sw.Stop();
            encodeMs += sw.Elapsed.TotalMilliseconds;
            bytes = new FileInfo(path).Length;

            sw.Restart();
            var bitmap = new BitmapImage();
            using (var stream = File.OpenRead(path))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            sw.Stop();
            decodeMs += sw.Elapsed.TotalMilliseconds;
            decoded = bitmap;
        }

        var psnr = PixelMetrics.Psnr(ImageCompare.ToBgra32(source), ImageCompare.ToBgra32(ToBgr32(decoded!)));
        return (encodeMs / Iterations, bytes, decodeMs / Iterations, psnr);
    }

    private static (double EncodeMs, long Bytes, double DecodeMs, double Psnr) MeasureRawBgr32(BitmapSource source, string tempDir)
    {
        var path = Path.Combine(tempDir, "preview-raw.bin");
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        double encodeMs = 0, decodeMs = 0;
        long bytes = 0;
        byte[]? readBack = null;
        for (var i = 0; i < Iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(BitConverter.GetBytes(width));
                fs.Write(BitConverter.GetBytes(height));
                fs.Write(pixels);
            }
            sw.Stop();
            encodeMs += sw.Elapsed.TotalMilliseconds;
            bytes = new FileInfo(path).Length;

            sw.Restart();
            var raw = File.ReadAllBytes(path);
            readBack = raw[8..];
            sw.Stop();
            decodeMs += sw.Elapsed.TotalMilliseconds;
        }

        var decodedBitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, readBack!, stride);
        decodedBitmap.Freeze();
        var psnr = PixelMetrics.Psnr(ImageCompare.ToBgra32(source), ImageCompare.ToBgra32(decodedBitmap));
        return (encodeMs / Iterations, bytes, decodeMs / Iterations, psnr);
    }

    private static BitmapSource ToBgr32(BitmapSource source) =>
        source.Format == PixelFormats.Bgr32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);

    private static BitmapImage DecodeDownscaled(string path, int targetWidth)
    {
        var bitmap = new BitmapImage();
        using var stream = File.OpenRead(path);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = targetWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Gradient checkerboard plus deterministic per-pixel noise: a pure gradient compresses far
    /// better than a real photo, so noise gives JPEG something closer to real photographic
    /// entropy to work with (a fixed seed keeps the benchmark reproducible).
    /// </summary>
    private static BitmapSource CreateNoisyGradient(int width, int height)
    {
        var bitmap = FixtureGenerator.CreateGradientCheckerboard(width, height);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var rng = new Random(12345);
        var noise = new byte[pixels.Length];
        rng.NextBytes(noise);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = ClampAdd(pixels[i], noise[i]);
            pixels[i + 1] = ClampAdd(pixels[i + 1], noise[i + 1]);
            pixels[i + 2] = ClampAdd(pixels[i + 2], noise[i + 2]);
            pixels[i + 3] = 255;
        }

        var noisy = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        noisy.Freeze();
        return noisy;
    }

    /// <summary>Adds signed noise in [-16, 15] (from a random byte) to a channel, clamped to [0, 255].</summary>
    private static byte ClampAdd(byte channel, byte noiseByte)
    {
        var delta = (noiseByte % 32) - 16;
        return (byte)Math.Clamp(channel + delta, 0, 255);
    }
}
