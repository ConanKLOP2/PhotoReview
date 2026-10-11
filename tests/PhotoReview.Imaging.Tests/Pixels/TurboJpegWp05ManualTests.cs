using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Pixels;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.Imaging.Tests.Pixels;

/// <summary>
/// WP-05, run by hand (Category=Manual, never in CI): fine-scale difference of the integer area filter against WIC Fant on
/// real photos, and TurboJpeg decode time (median of N) for the legacy WPF path against the PixelBuffer path.
/// Env: PHOTOREVIEW_WP05_OUT (result file, required), PHOTOREVIEW_WP05_FILES (';'-separated JPEGs, fine-scale compare),
/// PHOTOREVIEW_WP05_BENCH_FILE (one big JPEG), PHOTOREVIEW_WP05_RUNS (default 12).
/// The bench only touches APIs that also exist on master (the codec constructor is looked up by reflection), so the same
/// file runs against the base checkout.
/// </summary>
[Trait("Category", "Manual")]
public sealed class TurboJpegWp05ManualTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static TurboJpeg.TurboJpegDecoder? NewPixelDecoder()
    {
        var ctor = typeof(TurboJpeg.TurboJpegDecoder).GetConstructor([typeof(IPlatformImageCodec)]);
        return ctor?.Invoke([PixelBufferImageCodec.Instance]) as TurboJpeg.TurboJpegDecoder;
    }

    private static void Append(string text) => File.AppendAllText(Env("PHOTOREVIEW_WP05_OUT")!, text + Environment.NewLine, Encoding.UTF8);

    [Fact]
    public void FineScale_AreaFilter_VsWicFant_RealPhotos()
    {
        if (Env("PHOTOREVIEW_WP05_OUT") is null || Env("PHOTOREVIEW_WP05_FILES") is not { } files) return;
        var pixelDecoder = NewPixelDecoder();
        if (pixelDecoder is null) return;
        var legacy = new TurboJpeg.TurboJpegDecoder(WpfBitmapSourceCodec.Instance);
        Append("# fine-scale: file | source | target width | size | MAE | PSNR dB");
        foreach (var path in files.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var target in new[] { 1920, 1280, 800, 400, 150 })
            {
                var request = new DecodeRequest(path, TargetWidth: target, ApplyOrientation: true);
                var expected = (WpfDecodedImage)legacy.Decode(request);
                var actual = pixelDecoder.Decode(request);
                var buffer = (PixelBuffer)actual.PlatformImage;
                using var reference = PixelAssert.FromBitmapSource(expected.Source, PixelLayout.Bgr32);
                var same = (expected.PixelWidth, expected.PixelHeight) == (buffer.Width, buffer.Height);
                var mae = same ? PixelAssert.MeanAbsoluteError(reference, buffer) : double.NaN;
                var psnr = same ? PixelAssert.Psnr(reference, buffer) : double.NaN;
                Append(string.Create(CultureInfo.InvariantCulture,
                    $"{Path.GetFileName(path)} | {actual.OriginalWidth}x{actual.OriginalHeight} | {target} | {buffer.Width}x{buffer.Height} (wic {expected.PixelWidth}x{expected.PixelHeight}) | {mae:F3} | {psnr:F1}"));
                buffer.Dispose();
            }
        }
    }

    [Fact]
    public void TurboJpeg_DecodeTime_LegacyVsPixelBuffer()
    {
        if (Env("PHOTOREVIEW_WP05_OUT") is null || Env("PHOTOREVIEW_WP05_BENCH_FILE") is not { } file) return;
        var runs = int.TryParse(Env("PHOTOREVIEW_WP05_RUNS"), out var r) ? r : 12;
        var legacy = new TurboJpeg.TurboJpegDecoder(WpfBitmapSourceCodec.Instance);
        var pixel = NewPixelDecoder();
        var bytes = File.ReadAllBytes(file); // in memory: measures decode, not the disk
        Append($"# bench {Path.GetFileName(file)} runs={runs} codecCtor={(pixel is null ? "absent (base)" : "present")}");
        foreach (var target in new[] { 0, 3000, 1920, 800 })
        {
            var request = new DecodeRequest("bench.jpg", TargetWidth: target, ApplyOrientation: true, Bytes: bytes);
            var legacyTimes = new List<double>();
            var pixelTimes = new List<double>();
            for (var i = -2; i < runs; i++) // two warm-up rounds
            {
                legacyTimes.Add(Time(legacy, request, i < 0));
                if (pixel is not null) pixelTimes.Add(Time(pixel, request, i < 0));
            }

            Append("target " + target + ": legacy " + Summary(legacyTimes) + (pixel is null ? "" : " | pixel " + Summary(pixelTimes)));
        }
    }

    private static double Time(TurboJpeg.TurboJpegDecoder decoder, DecodeRequest request, bool warmup)
    {
        var sw = Stopwatch.StartNew();
        var image = decoder.Decode(request);
        sw.Stop();
        (image.PlatformImage as IDisposable)?.Dispose();
        (image as IDisposable)?.Dispose();
        GC.Collect();
        return warmup ? double.NaN : sw.Elapsed.TotalMilliseconds;
    }

    private static string Summary(List<double> times)
    {
        var v = times.Where(t => !double.IsNaN(t)).Order().ToList();
        return string.Create(CultureInfo.InvariantCulture, $"P50 {v[v.Count / 2]:F1} ms (min {v[0]:F1}, max {v[^1]:F1}, n={v.Count})");
    }
}
