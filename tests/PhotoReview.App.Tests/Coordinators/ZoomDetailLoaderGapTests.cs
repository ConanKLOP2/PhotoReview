using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// RV-T24: <see cref="ZoomDetailLoader"/> state hand-over. A <c>Reset</c> during an in-flight decode followed by a new target
/// must not let the old load's <c>finally</c> clear the new load's slot, and a failed decode blocks retries only until the
/// next navigation. The loader is driven directly; decodes are blocked on gates the test releases (no timing).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ZoomDetailLoaderGapTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview_ZoomGap_" + Guid.NewGuid().ToString("N"));
    private readonly GenerationClock _clock = new();
    private readonly GatedDecoder _decoder = new();
    private readonly PreviewImageService _service;
    private readonly List<object> _shown = [];
    private readonly ZoomDetailLoader _loader;

    public ZoomDetailLoaderGapTests()
    {
        Directory.CreateDirectory(_tempDir);
        _service = new PreviewImageService(new ReviewMetrics(), () => false, () => new DecodeBox(1920, 1080),
            capacityBytes: 512L * 1024 * 1024, disableDiskCacheOverride: true,
            decoder: _decoder, currentBackend: () => DecoderBackend.Wpf);
        _loader = new ZoomDetailLoader(_service, _clock, (image, _, _) => _shown.Add(image));
    }

    public void Dispose()
    {
        _service.ShutdownPersistWorkersAsync().GetAwaiter().GetResult();
        try { Directory.Delete(_tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private (long Token, string Path) Present(string name)
    {
        var path = Path.Combine(_tempDir, name);
        if (!File.Exists(path)) File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xD9]);
        var token = _clock.NextNavigation();
        var key = ImageCacheKey.Create(path, false, new DecodeBox(600, 0));
        _loader.OnPreviewPresented(token, path, key, new FakeImage(600, 400, 6000, 4000, downscaled: true));
        return (token, path);
    }

    [Fact]
    public async Task Reset_DuringInFlightDecodeThenNewTarget_OldLoadFinishingLateDoesNotClearTheNewLoadsSlot()
    {
        using var gateA = new SemaphoreSlim(0);
        using var gateB = new SemaphoreSlim(0);
        _decoder.GateByName["a.jpg"] = gateA;
        _decoder.GateByName["b.jpg"] = gateB;
        _loader.SetZoom(1.0);
        Present("a.jpg");
        var loadA = _loader.PendingLoad;
        Assert.NotNull(loadA);

        Present("b.jpg"); // Reset() + a new target while A's decode is still running; the zoom is still 1.0, so B starts at once
        var loadB = _loader.PendingLoad;
        Assert.NotNull(loadB);
        Assert.NotSame(loadA, loadB);

        gateA.Release(); // the superseded load ends now, before B has finished
        await loadA!;

        Assert.Same(loadB, _loader.PendingLoad); // A's finally must not have dropped B's task
        _loader.SetZoom(2.0); // must not start a second decode of B (its slot is still owned)
        Assert.Same(loadB, _loader.PendingLoad);

        gateB.Release();
        await loadB!;
        Assert.True(_loader.IsShowingOriginal);
        Assert.Null(_loader.PendingLoad);
        Assert.Equal(1, _decoder.OriginalDecodes("b.jpg"));
    }

    [Fact]
    public async Task FailedDecode_BlocksRetriesOnTheSameNavigationButTheNextNavigationDecodesAgain()
    {
        using var gate = new SemaphoreSlim(0);
        _decoder.FailNames.Add("a.jpg");
        _decoder.GateByName["a.jpg"] = gate;
        _loader.SetZoom(1.0);
        Present("a.jpg");
        var load = _loader.PendingLoad;
        Assert.NotNull(load);
        gate.Release();
        await load!;
        Assert.Equal(1, _decoder.OriginalDecodes("a.jpg"));

        _loader.SetZoom(2.0);
        _loader.SetZoom(3.0);
        Assert.Null(_loader.PendingLoad);
        Assert.Equal(1, _decoder.OriginalDecodes("a.jpg")); // no retry on this navigation

        Present("c.jpg"); // next navigation (new token): the failure latch no longer applies
        var next = _loader.PendingLoad;
        Assert.NotNull(next);
        await next!;
        Assert.Equal(1, _decoder.OriginalDecodes("c.jpg"));
        Assert.True(_loader.IsShowingOriginal);
    }

    private sealed class GatedDecoder : IImageDecoder
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SemaphoreSlim> GateByName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FailNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int OriginalDecodes(string name)
        {
            lock (_gate) return _counts.GetValueOrDefault(name);
        }

        public IDecodedImage Decode(DecodeRequest request)
        {
            var name = Path.GetFileName(request.Path);
            if (!request.Box.IsUnbounded) return new FakeImage(600, 400, 6000, 4000, downscaled: true);
            lock (_gate) _counts[name] = _counts.GetValueOrDefault(name) + 1;
            if (GateByName.TryGetValue(name, out var gate)) gate.Wait();
            if (FailNames.Contains(name)) throw new InvalidOperationException("original decode failed");
            return new FakeImage(6000, 4000, 6000, 4000, downscaled: false);
        }

        public ImageInfo ReadInfo(string path) => new(6000, 4000);
    }

    private sealed class FakeImage(int width, int height, int originalWidth, int originalHeight, bool downscaled) : IDecodedImage
    {
        public int PixelWidth => width;
        public int PixelHeight => height;
        public bool Downscaled => downscaled;
        public int Orientation => 1;
        public long EstimatedBytes => (long)width * height * 4;
        public object PlatformImage { get; } = new object();
        public int OriginalWidth => originalWidth;
        public int OriginalHeight => originalHeight;
    }
}
