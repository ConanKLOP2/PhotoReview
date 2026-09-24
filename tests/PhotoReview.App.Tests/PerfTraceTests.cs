using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using PhotoReview.App;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.App.Tests;

/// <summary>
/// D03: PhotoReviewPerf EventSource + PerfCsvListener. Mutates the PHOTOREVIEW_PERF_TRACE
/// environment variable, so these run in the "GlobalState" collection (no parallel siblings).
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "Slow")]
public sealed class PerfTraceTests : IDisposable
{
    private readonly string? _previousTrace = Environment.GetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE");
    private readonly TempRoot _root = new("perf-trace");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE", _previousTrace);
        _root.Dispose();
    }

    private static string[] ReadLines(string path) => File.ReadAllLines(path);

    [Fact(DisplayName = "No PHOTOREVIEW_PERF_TRACE means no listener and no files")]
    public void NoEnvironmentVariableMeansNoListenerAndNoFiles()
    {
        Environment.SetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE", null);

        var listener = PerfCsvListener.TryStartFromEnvironment();

        Assert.Null(listener);
        Assert.Empty(Directory.GetFiles(_root.Path, "*", SearchOption.AllDirectories));
    }

    [Fact(DisplayName = "Blank PHOTOREVIEW_PERF_TRACE means no listener and no files")]
    public void BlankEnvironmentVariableMeansNoListenerAndNoFiles()
    {
        Environment.SetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE", "   ");

        var listener = PerfCsvListener.TryStartFromEnvironment();

        Assert.Null(listener);
    }

    [Fact(DisplayName = "Setting PHOTOREVIEW_PERF_TRACE writes a CSV with header, commit line, and mapped columns")]
    public void SettingEnvironmentVariableWritesCsvWithMappedColumns()
    {
        var dir = _root.Dir("out");
        Environment.SetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE", dir);

        var listener = PerfCsvListener.TryStartFromEnvironment();
        Assert.NotNull(listener);

        try
        {
            Assert.True(PhotoReviewPerf.Log.IsEnabled());

            PhotoReviewPerf.Log.ShowStart(1, 0, "Preview");
            PhotoReviewPerf.Log.Decode(1, "abc12345", 12.5, 800, true, false);
            PhotoReviewPerf.Log.PreloadPaused(85, 512);
        }
        finally
        {
            listener!.Dispose();
        }

        var file = Directory.GetFiles(dir, "perf-*.csv").Single();
        var lines = ReadLines(file);

        Assert.StartsWith("# commit=", lines[0]);
        Assert.Contains("qpcFrequency=" + Stopwatch.Frequency, lines[0]);
        Assert.Equal("utcTicks,qpcTicks,thread,event,nav,pathId,a,b,c,d,text", lines[1]);
        Assert.StartsWith("# dropped=", lines[^1]);

        var dataLines = lines[2..^1];
        Assert.Equal(3, dataLines.Length);

        var showStart = dataLines.Single(l => l.Contains(",ShowStart,"));
        var showStartCols = showStart.Split(',');
        Assert.Equal("1", showStartCols[4]);   // nav
        Assert.Equal("", showStartCols[5]);    // pathId
        Assert.Equal("0", showStartCols[6]);   // a = index
        Assert.Equal("Preview", showStartCols[10]); // text = mode

        var decode = dataLines.Single(l => l.Contains(",Decode,"));
        var decodeCols = decode.Split(',');
        Assert.Equal("1", decodeCols[4]);          // nav
        Assert.Equal("abc12345", decodeCols[5]);   // pathId
        Assert.Equal("12.5", decodeCols[6]);       // a = ms
        Assert.Equal("800", decodeCols[7]);        // b = targetWidth
        Assert.Equal("1", decodeCols[8]);          // c = downscaled
        Assert.Equal("0", decodeCols[9]);          // d = fallback

        var preloadPaused = dataLines.Single(l => l.Contains(",PreloadPaused,"));
        var preloadCols = preloadPaused.Split(',');
        Assert.Equal("", preloadCols[4]);  // nav (absent)
        Assert.Equal("", preloadCols[5]);  // pathId (absent)
        Assert.Equal("85", preloadCols[6]);  // a = loadPercent
        Assert.Equal("512", preloadCols[7]); // b = availableMb
    }

    [Fact(DisplayName = "PathId is stable and case-insensitive")]
    public void PathIdIsStableAndCaseInsensitive()
    {
        var file = _root.File("Sample.JPG", 1, 2, 3);

        var first = PhotoReviewPerf.PathId(file);
        var second = PhotoReviewPerf.PathId(file);
        var upper = PhotoReviewPerf.PathId(file.ToUpperInvariant());
        var lower = PhotoReviewPerf.PathId(file.ToLowerInvariant());

        Assert.Equal(8, first.Length);
        Assert.Equal(first, second);
        Assert.Equal(first, upper);
        Assert.Equal(first, lower);
    }

    [Fact(DisplayName = "Listener does not block under a 200k-event burst and accounts for every event")]
    public void ListenerDoesNotBlockUnderBurstAndAccountsForEveryEvent()
    {
        var dir = _root.Dir("burst");
        Environment.SetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE", dir);

        var listener = PerfCsvListener.TryStartFromEnvironment();
        Assert.NotNull(listener);

        const int total = 200_000;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < total; i++)
        {
            PhotoReviewPerf.Log.KeyInput(0, "x", 0.0);
        }
        listener!.Dispose();
        sw.Stop();

        // TC09: AUDIT - Timing assertion; consider using clock fake or deterministic test instead
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Took {sw.Elapsed}");

        var file = Directory.GetFiles(dir, "perf-*.csv").Single();
        var lines = ReadLines(file);
        var droppedLine = lines[^1];
        Assert.StartsWith("# dropped=", droppedLine);
        var dropped = long.Parse(droppedLine["# dropped=".Length..], CultureInfo.InvariantCulture);

        var dataLines = lines[2..^1].Length;
        Assert.Equal(total, dataLines + dropped);
    }

    // ---- D04: instrumentation points in PreviewImageService ----

    /// <summary>In-memory listener for PhotoReview-Perf; the class runs in the non-parallel
    /// "GlobalState" collection, so no other test emits events while one is attached.</summary>
    private sealed class CapturingListener : EventListener
    {
        // Field initializers run before the base EventListener constructor, which may call
        // OnEventSourceCreated re-entrantly for the already-existing PhotoReview-Perf source.
        private readonly ConcurrentQueue<(string Name, Dictionary<string, object?> Payload)> _events = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "PhotoReview-Perf") EnableEvents(eventSource, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < (eventData.PayloadNames?.Count ?? 0); i++)
                payload[eventData.PayloadNames![i]] = eventData.Payload![i];
            _events.Enqueue((eventData.EventName ?? "", payload));
        }

        public List<Dictionary<string, object?>> Find(string name, string pathId)
            => _events.Where(e => e.Name == name && Equals(e.Payload.GetValueOrDefault("pathId"), pathId))
                .Select(e => e.Payload).ToList();
    }

    private static PreviewImageService CreatePreviewService(string diskCache)
        => new(new ReviewMetrics(), () => false, () => 512, capacityBytes: 64L * 1024 * 1024, diskCacheDirectory: diskCache);

    private static async Task RetireAsync(PreviewImageService service, string diskCache)
    {
        await service.ShutdownPersistWorkersAsync();
        await service.WaitForPruneAsync(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "No listener attached means PhotoReview-Perf is disabled")]
    public void NoListenerMeansEventSourceDisabled()
    {
        Assert.False(PhotoReviewPerf.Log.IsEnabled());
    }

    [Fact(DisplayName = "Source decode emits Decode and Verify under the caller's NavContext")]
    public async Task SourceDecodeEmitsDecodeAndVerifyWithNav()
    {
        var image = _root.File("nav/decode.png", TestImages.PreviewPng);
        var diskCache = _root.Dir("nav-cache");
        var service = CreatePreviewService(diskCache);
        var pathId = PhotoReviewPerf.PathId(image);
        using var listener = new CapturingListener();
        try
        {
            PhotoReviewPerf.NavContext = 4_242_001;
            await service.GetPreviewAsync(image);

            var decode = Assert.Single(listener.Find("Decode", pathId));
            Assert.Equal(4_242_001L, decode["nav"]);
            Assert.Equal(512, decode["targetWidth"]);
            Assert.Equal(true, decode["downscaled"]);
            Assert.Equal(false, decode["fallback"]);
            Assert.True((double)decode["ms"]! >= 0);
            var verify = Assert.Single(listener.Find("Verify", pathId));
            Assert.Equal(4_242_001L, verify["nav"]);
            Assert.Empty(listener.Find("DiskCacheRead", pathId));
            Assert.Empty(listener.Find("JoinEnd", pathId));
        }
        finally
        {
            await RetireAsync(service, diskCache);
        }
    }

    [Fact(DisplayName = "A RAM miss served from the preview disk cache emits DiskCacheRead")]
    public async Task DiskCacheHitEmitsDiskCacheRead()
    {
        var image = _root.File("disk/disk.png", TestImages.PreviewPng);
        var diskCache = _root.Dir("disk-cache");
        var service = CreatePreviewService(diskCache);
        var pathId = PhotoReviewPerf.PathId(image);
        using var listener = new CapturingListener();
        try
        {
            PhotoReviewPerf.NavContext = 4_242_002;
            await service.GetPreviewAsync(image);
            // Drain the persist queue first: ClearCache bumps the epoch, and a persist request that
            // is still queued at that point is (correctly) dropped instead of written.
            await RetireAsync(service, diskCache);
            service.ClearCache();
            Assert.NotEmpty(Directory.GetFiles(diskCache, "*.pv4"));

            PhotoReviewPerf.NavContext = 4_242_003;
            await service.GetPreviewAsync(image);

            var read = Assert.Single(listener.Find("DiskCacheRead", pathId));
            Assert.Equal(4_242_003L, read["nav"]);
            Assert.True((long)read["bytes"]! > 0);
            // The second request never touched the source: still exactly one Decode (from the first).
            var decode = Assert.Single(listener.Find("Decode", pathId));
            Assert.Equal(4_242_002L, decode["nav"]);
            Assert.Equal(2, listener.Find("Verify", pathId).Count);
        }
        finally
        {
            await RetireAsync(service, diskCache);
        }
    }

    [Fact(DisplayName = "Concurrent requests for one key emit one Decode (winner's nav) and JoinEnd for joiners")]
    public async Task ConcurrentRequestsEmitOneDecodeAndJoins()
    {
        // A larger image keeps the shared decode running long enough that the 15 callers issued
        // right after the first (synchronously, in one loop) find it still in flight.
        var image = _root.Combine("join", "large.png");
        Directory.CreateDirectory(Path.GetDirectoryName(image)!);
        WriteLargePng(image, 2400, 1600);
        var diskCache = _root.Dir("join-cache");
        var service = CreatePreviewService(diskCache);
        var pathId = PhotoReviewPerf.PathId(image);
        using var listener = new CapturingListener();
        try
        {
            var calls = new List<Task<IDecodedImage>>();
            for (var i = 0; i < 16; i++)
            {
                PhotoReviewPerf.NavContext = 5_000 + i;
                calls.Add(service.GetPreviewAsync(image));
            }
            await Task.WhenAll(calls);

            var decode = Assert.Single(listener.Find("Decode", pathId));
            Assert.Equal(5_000L, decode["nav"]);
            var joins = listener.Find("JoinEnd", pathId);
            Assert.NotEmpty(joins);
            Assert.All(joins, j => Assert.InRange((long)j["nav"]!, 5_001L, 5_015L));
            Assert.Equal(joins.Count, listener.Find("JoinStart", pathId).Count);
        }
        finally
        {
            await RetireAsync(service, diskCache);
        }
    }

    private static void WriteLargePng(string path, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var random = new Random(1234);
        random.NextBytes(pixels);
        var source = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgr32, null, pixels, stride);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
