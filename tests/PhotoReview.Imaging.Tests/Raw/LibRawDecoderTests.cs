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
        using var memorySampler = new PrivateMemorySampler();
        var measurements = new List<string>();
        foreach (var path in files)
        {
            Console.WriteLine($"LibRaw corpus decoding: {Path.GetFileName(path)}");
            Console.Out.Flush();
            measurements.Add(DecodeCorpusSample(decoder, path));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (Path.GetFileName(path).Equals("Canon - EOS 350D - RAW (3_2).CR2", StringComparison.OrdinalIgnoreCase))
            {
                var bounded = decoder.Decode(new DecodeRequest(path, new DecodeBox(640, 480)));
                var info = decoder.ReadInfo(path);
                Assert.True(bounded.Downscaled);
                Assert.True(bounded.PixelWidth <= 640);
                Assert.True(bounded.PixelHeight <= 480);
                Assert.Equal(info.PixelWidth, bounded.OriginalWidth);
                Assert.Equal(info.PixelHeight, bounded.OriginalHeight);
            }
        }

        if (string.IsNullOrWhiteSpace(selectedSample))
        {
            var decodedFormats = files.Select(path => Path.GetExtension(path).ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var requiredFormats = new[] { ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2" };
            Assert.Subset(requiredFormats.ToHashSet(StringComparer.Ordinal), decodedFormats);
        }

        Console.WriteLine($"LibRaw corpus: {files.Length}/{files.Length} decoded; sampled peak private bytes={memorySampler.PeakPrivateBytes}; "
            + string.Join(" | ", measurements));
    }

    [Fact]
    [Trait("Category", "Native")]
    public void Decode_Repeated200Times_DoesNotContinuouslyGrowPrivateBytes()
    {
        var path = Path.Combine(CorpusDirectory, "Canon - EOS 7D - sRAW2 (sRAW) (3_2).CR2");
        if (!File.Exists(path)) return;

        var decoder = new LibRawDecoder();
        using var memorySampler = new PrivateMemorySampler();
        long afterWarmup = 0;
        for (var index = 0; index < 200; index++)
        {
            _ = DecodeCorpusSample(decoder, path);
            if ((index + 1) % 10 != 0) continue;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (index == 19) afterWarmup = memorySampler.CurrentPrivateBytes;
        }

        var finalPrivateBytes = memorySampler.CurrentPrivateBytes;
        var growth = finalPrivateBytes - afterWarmup;
        Console.WriteLine($"LibRaw 200 decodes: after warmup={afterWarmup}; final={finalPrivateBytes}; "
            + $"growth={growth}; peak={memorySampler.PeakPrivateBytes}");
        Assert.InRange(growth, long.MinValue, 32L * 1024 * 1024);
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

    private sealed class PrivateMemorySampler : IDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly Timer _timer;
        private long _currentPrivateBytes;
        private long _peakPrivateBytes;

        internal PrivateMemorySampler()
        {
            Sample();
            _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
        }

        internal long CurrentPrivateBytes => Interlocked.Read(ref _currentPrivateBytes);
        internal long PeakPrivateBytes => Interlocked.Read(ref _peakPrivateBytes);

        private void Sample()
        {
            _process.Refresh();
            var current = _process.PrivateMemorySize64;
            Interlocked.Exchange(ref _currentPrivateBytes, current);
            while (true)
            {
                var peak = Interlocked.Read(ref _peakPrivateBytes);
                if (current <= peak || Interlocked.CompareExchange(ref _peakPrivateBytes, current, peak) == peak) break;
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
            _process.Dispose();
        }
    }

    [Fact]
    public void Decode_RejectsRequestThatWouldLeaveOrientationUnapplied()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            new LibRawDecoder().Decode(new DecodeRequest("unused.cr2", DecodeBox.Unbounded, applyOrientation: false)));

        Assert.Contains("always applies", error.Message, StringComparison.Ordinal);
    }
}
