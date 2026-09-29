using System.IO;
using System.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.LibRaw;
using PhotoReview.Imaging.Decoding.Wic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoReview.Imaging.Tests.Raw;

[Trait("Category", "Native")]
public sealed class LibRawDecoderTests
{
    private const string StrictCorpusEnvironmentVariable = "PHOTOREVIEW_LIBRAW_STRICT_CORPUS";
    private static readonly string[] RequiredRawExtensions = [".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2"];
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
        var strictFullCorpus = IsStrictFullCorpusRequested();
        var selectedSample = Environment.GetEnvironmentVariable("PHOTOREVIEW_LIBRAW_SAMPLE");
        if (!Directory.Exists(CorpusDirectory))
        {
            if (strictFullCorpus || !string.IsNullOrWhiteSpace(selectedSample))
                throw new DirectoryNotFoundException($"LibRaw corpus is required but missing: {CorpusDirectory}");
            return;
        }

        var allRawFiles = Directory.GetFiles(CorpusDirectory)
            .Where(path => PhotoReview.Core.Catalog.ImageFileTypes.RawExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var files = SelectCorpusFiles(allRawFiles, strictFullCorpus, selectedSample);

        if (strictFullCorpus)
        {
            var formats = files.Select(path => Path.GetExtension(path).ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            Assert.Subset(RequiredRawExtensions.ToHashSet(StringComparer.Ordinal), formats);
        }

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
            Assert.Subset(RequiredRawExtensions.ToHashSet(StringComparer.Ordinal), decodedFormats);
        }

        Console.WriteLine($"LibRaw corpus: {files.Length}/{files.Length} decoded; sampled peak private bytes={memorySampler.PeakPrivateBytes}; "
            + string.Join(" | ", measurements));
    }

    internal static string[] SelectCorpusFiles(IReadOnlyCollection<string> rawFiles, bool strictFullCorpus, string? selectedSample)
    {
        if (strictFullCorpus && !string.IsNullOrWhiteSpace(selectedSample))
            throw new InvalidOperationException("PHOTOREVIEW_LIBRAW_SAMPLE cannot be combined with PHOTOREVIEW_LIBRAW_STRICT_CORPUS; strict mode decodes the complete corpus.");

        if (strictFullCorpus)
        {
            if (rawFiles.Count == 0)
                throw new InvalidOperationException("Strict LibRaw full-corpus mode requires at least one RAW corpus file.");
            return rawFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        if (!string.IsNullOrWhiteSpace(selectedSample))
        {
            var selected = rawFiles.Where(path => Path.GetFileName(path).Equals(selectedSample, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (selected.Length == 0)
                throw new FileNotFoundException($"PHOTOREVIEW_LIBRAW_SAMPLE '{selectedSample}' did not match any RAW corpus file.");
            return selected;
        }

        return rawFiles.GroupBy(Path.GetExtension, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsStrictFullCorpusRequested()
    {
        var value = Environment.GetEnvironmentVariable(StrictCorpusEnvironmentVariable);
        return value == "1" || bool.TryParse(value, out var enabled) && enabled;
    }

    [Fact]
    [Trait("Category", "Native")]
    public void ReadJpegThumbnail_OrfCorpusSamples_FallsBackToDecodablePreview()
    {
        if (!Directory.Exists(CorpusDirectory)) return;
        var files = Directory.GetFiles(CorpusDirectory)
            .Where(path => Path.GetExtension(path).Equals(".orf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0) return;

        Assert.Equal(3, files.Length);
        var fallback = new LibRawTestPreviewFallback();
        var rawDecoder = new RawDecoder(new WpfBitmapImageDecoder(), previewFallback: fallback);
        foreach (var path in files)
        {
            var decoded = rawDecoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded));
            Assert.True(decoded.PixelWidth > 0);
            Assert.True(decoded.PixelHeight > 0);
            Assert.Equal(DecoderBackend.Wpf, decoded.ActualBackend);
            Assert.IsAssignableFrom<BitmapSource>(decoded.PlatformImage);
            Assert.IsAssignableFrom<ISourceReadMetrics>(decoded);
            Assert.True(((ISourceReadMetrics)decoded).SourceBytesRead >= fallback.LastThumbnailLength);
        }
    }

    [Fact]
    [Trait("Category", "Native")]
    public void ReadJpegThumbnail_RepeatedOrfExtraction_DoesNotContinuouslyGrowPrivateBytes()
    {
        var path = Path.Combine(CorpusDirectory, "Olympus - E-P3 - 16bit (4_3).ORF");
        if (!File.Exists(path)) return;

        using var memorySampler = new PrivateMemorySampler();
        long afterWarmup = 0;
        for (var index = 0; index < 100; index++)
        {
            var thumbnail = LibRawDecoder.ReadJpegThumbnail(path);
            Assert.InRange(thumbnail.Length, 4, RawContainerLimits.MaxHeaderBytes * 4);
            if ((index + 1) % 10 != 0) continue;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (index == 19) afterWarmup = memorySampler.CurrentPrivateBytes;
        }

        var growth = memorySampler.CurrentPrivateBytes - afterWarmup;
        Console.WriteLine($"LibRaw thumbnail extraction 100x: after warmup={afterWarmup}; growth={growth}; peak={memorySampler.PeakPrivateBytes}");
        Assert.InRange(growth, long.MinValue, 32L * 1024 * 1024);
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

    [Theory]
    [InlineData("Canon - EOS 350D - RAW (3_2).CR2")]
    [InlineData("Sony - NEX-6 - 12bit 12bit compressed (3_2).ARW")]
    [InlineData("Nikon - D800 - 14bit 14bit compressed (Lossless) (3_2).NEF")]
    public void ReadInfo_PortraitRewrittenCorpusFile_MatchesDecodedOrientedDimensions(string fileName)
    {
        var source = Path.Combine(CorpusDirectory, fileName);
        if (!File.Exists(source)) return; // corpus files are gitignored and optional
        var bytes = File.ReadAllBytes(source);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var info = new PhotoReview.Imaging.Raw.RawContainerReaderRegistry()
            .FindReader(bytes.AsSpan(0, 64), extension)!.Read(new PhotoReview.Imaging.Raw.InMemoryRawHeaderSource(bytes), CancellationToken.None);
        Assert.True(RawOrientationPatcher.TryWriteOrientation(bytes, extension, info, 6), $"{fileName} has no orientation tag to rewrite");
        using var temp = new PhotoReview.TestSupport.TempRoot("libraw-portrait");
        var path = temp.File(fileName, bytes);
        var decoder = new LibRawDecoder();

        var readInfo = decoder.ReadInfo(path);
        var decoded = decoder.Decode(new DecodeRequest(path, new DecodeBox(640, 480)));

        Assert.True(decoded.OriginalHeight > decoded.OriginalWidth, "LibRaw should have flipped the rewritten file to portrait.");
        Assert.Equal(decoded.OriginalWidth, readInfo.Width);
        Assert.Equal(decoded.OriginalHeight, readInfo.Height);
        Assert.Equal(decoded.OriginalWidth, readInfo.PixelWidth);
        Assert.Equal(decoded.OriginalHeight, readInfo.PixelHeight);
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

    private sealed class LibRawTestPreviewFallback : IRawPreviewFallback
    {
        internal int LastThumbnailLength { get; private set; }

        public ReadOnlyMemory<byte> ReadJpegThumbnail(string path, RawFormat format)
        {
            Assert.Equal(RawFormat.Orf, format);
            var bytes = LibRawDecoder.ReadJpegThumbnail(path);
            Assert.InRange(bytes.Length, 4, RawContainerLimits.MaxHeaderBytes * 4);
            Assert.Equal((byte)0xFF, bytes[0]);
            Assert.Equal((byte)0xD8, bytes[1]);
            Assert.Equal((byte)0xFF, bytes[^2]);
            Assert.Equal((byte)0xD9, bytes[^1]);
            LastThumbnailLength = bytes.Length;
            return bytes;
        }
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

public sealed class LibRawCorpusSelectionTests
{
    [Fact]
    public void SelectCorpusFiles_DefaultIsRepresentative_AndStrictIncludesEveryRawFile()
    {
        string[] samples = ["a.cr2", "b.CR2", "c.cr3", "d.nef", "e.arw", "f.dng", "g.raf", "h.orf", "i.rw2"];

        var representative = LibRawDecoderTests.SelectCorpusFiles(samples, strictFullCorpus: false, selectedSample: null);
        Assert.Equal(8, representative.Length);
        Assert.Equal(samples.Length, LibRawDecoderTests.SelectCorpusFiles(samples, strictFullCorpus: true, selectedSample: null).Length);
        Assert.Throws<InvalidOperationException>(() => LibRawDecoderTests.SelectCorpusFiles([], strictFullCorpus: true, selectedSample: null));
        Assert.Throws<InvalidOperationException>(() => LibRawDecoderTests.SelectCorpusFiles(samples, strictFullCorpus: true, selectedSample: "a.cr2"));
        Assert.Throws<FileNotFoundException>(() => LibRawDecoderTests.SelectCorpusFiles(samples, strictFullCorpus: false, selectedSample: "missing.cr2"));
    }
}
