using System.IO;
using System.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.LibRaw;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoReview.Imaging.Tests.Raw;

[Trait("Category", "Native")]
public sealed class LibRawDecoderTests
{
    private static readonly string CorpusDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

    [Fact]
    [Trait("Category", "Native")]
    public void AvailabilityProbe_LoadsAndInitializesPinnedRuntime()
    {
        Assert.True(LibRawAvailability.Probe(out var reason), reason);
        Assert.Null(reason);
        Assert.True(LibRawAvailability.Probe(out reason));
        Assert.Null(reason);
    }

    [Fact]
    public void Decode_OfficialCorpusSamples_ReturnsValidRgbBackedBitmap()
    {
        if (!Directory.Exists(CorpusDirectory)) return;
        var files = Directory.GetFiles(CorpusDirectory)
            .Where(path => PhotoReview.Core.Catalog.ImageFileTypes.RawExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedSample = Environment.GetEnvironmentVariable("PHOTOREVIEW_LIBRAW_SAMPLE");
        if (!string.IsNullOrWhiteSpace(selectedSample))
            files = files.Where(path => Path.GetFileName(path).Equals(selectedSample, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length == 0) return;

        var decoder = new LibRawDecoder();
        using var process = Process.GetCurrentProcess();
        var peakPrivateBytes = process.PrivateMemorySize64;
        var measurements = new List<string>();
        foreach (var path in files)
        {
            Console.WriteLine($"LibRaw corpus decoding: {Path.GetFileName(path)}");
            Console.Out.Flush();
            measurements.Add(DecodeCorpusSample(decoder, path));
            peakPrivateBytes = Math.Max(peakPrivateBytes, process.PrivateMemorySize64);

            if (Path.GetFileName(path).Equals("Canon - EOS 350D - RAW (3_2).CR2", StringComparison.OrdinalIgnoreCase))
            {
                var bounded = decoder.Decode(new DecodeRequest(path, new DecodeBox(640, 480)));
                Assert.True(bounded.Downscaled);
                Assert.True(bounded.PixelWidth <= 640);
                Assert.True(bounded.PixelHeight <= 480);
                Assert.Equal(image.OriginalWidth, bounded.OriginalWidth);
                Assert.Equal(image.OriginalHeight, bounded.OriginalHeight);
            }
        }

        Console.WriteLine($"LibRaw corpus: {files.Length}/{files.Length} decoded; peak private bytes={peakPrivateBytes}; "
            + string.Join(" | ", measurements));
    }

    private static string DecodeCorpusSample(LibRawDecoder decoder, string path)
    {
        var stopwatch = Stopwatch.StartNew();
        var image = decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded));
        stopwatch.Stop();
        var info = decoder.ReadInfo(path);

        Assert.True(image.PixelWidth > 0);
        Assert.True(image.PixelHeight > 0);
        Assert.Equal(DecoderBackend.LibRaw, image.ActualBackend);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.PlatformImage);
        Assert.Equal(PixelFormats.Bgr32, bitmap.Format);
        Assert.Equal(96, bitmap.DpiX);
        Assert.Equal(96, bitmap.DpiY);
        Assert.Equal(image.PixelWidth, info.PixelWidth);
        Assert.Equal(image.PixelHeight, info.PixelHeight);

        return $"{Path.GetFileName(path)} {image.PixelWidth}x{image.PixelHeight} {stopwatch.Elapsed.TotalMilliseconds:F0} ms";
    }

    [Fact]
    public void Decode_RejectsRequestThatWouldLeaveOrientationUnapplied()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            new LibRawDecoder().Decode(new DecodeRequest("unused.cr2", DecodeBox.Unbounded, applyOrientation: false)));

        Assert.Contains("always applies", error.Message, StringComparison.Ordinal);
    }
}
