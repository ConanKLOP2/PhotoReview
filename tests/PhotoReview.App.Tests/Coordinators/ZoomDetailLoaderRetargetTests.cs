using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// FEAT-RENAME PR2: <see cref="ZoomDetailLoader.RetargetPath"/> re-arms the zoom-detail target under the renamed path and the
/// navigation generation the rename's StopForAction bumped. Decodes block on gates the test releases (no timing).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ZoomDetailLoaderRetargetTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview_ZoomRetarget_" + Guid.NewGuid().ToString("N"));
    private readonly GenerationClock _clock = new();
    private readonly RecordingDecoder _decoder = new();
    private readonly PreviewImageService _service;
    private readonly List<object> _shown = [];
    private readonly ZoomDetailLoader _loader;

    public ZoomDetailLoaderRetargetTests()
    {
        Directory.CreateDirectory(_tempDir);
        _service = new PreviewImageService(new ReviewMetrics(), () => false, () => new DecodeBox(1920, 1080), WpfBitmapSourceCodec.Instance,
            capacityBytes: 512L * 1024 * 1024, disableDiskCacheOverride: true,
            decoder: _decoder, currentBackend: () => DecoderBackend.Wpf);
        _loader = new ZoomDetailLoader(_service, _clock, (image, _, _) => _shown.Add(image));
    }

    public void Dispose()
    {
        _service.ShutdownPersistWorkersAsync().GetAwaiter().GetResult();
        try { Directory.Delete(_tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly DateTime Stamp = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);

    // Same bytes and write time under every name: a rename changes neither (the decode checks the source identity).
    private string Make(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xD9]);
        File.SetLastWriteTimeUtc(path, Stamp);
        return path;
    }

    private (long Token, string Path) Present(string name)
    {
        var path = Make(name);
        var token = _clock.NextNavigation();
        var key = ImageCacheKey.Create(path, false, new DecodeBox(600, 0));
        _loader.OnPreviewPresented(token, path, key, new FakeImage(600, 400, 6000, 4000, downscaled: true));
        return (token, path);
    }

    [Fact]
    public void StopForAction_WithoutRetarget_ZoomDetailIsDeadAndNothingDecodes()
    {
        Present("a.jpg");
        _clock.StopForAction();

        _loader.SetZoom(1.0);

        Assert.Null(_loader.PendingLoad);
        Assert.Empty(_decoder.OriginalPaths);
    }

    [Fact]
    public async Task RetargetPath_AfterStopForActionAndZoom_DecodesTheOriginalViaTheNewPathOnly()
    {
        var (_, oldPath) = Present("a.jpg");
        var newPath = Make("b.jpg");
        using var gate = new SemaphoreSlim(0);
        _decoder.GateByName["b.jpg"] = gate; // keeps the load in flight so PendingLoad is observable
        _clock.StopForAction();

        _loader.RetargetPath(_clock.CurrentNavigation, newPath);
        _loader.SetZoom(1.0);
        var load = _loader.PendingLoad;
        Assert.NotNull(load);
        gate.Release();
        await load!;

        Assert.Equal([newPath], _decoder.OriginalPaths);
        Assert.DoesNotContain(oldPath, _decoder.OriginalPaths);
        Assert.True(_loader.IsShowingOriginal);
        Assert.NotNull(_loader.HeldOriginal);
    }

    [Fact]
    public void RetargetPath_WithoutTarget_IsANoOp()
    {
        var newPath = Make("b.jpg");

        _loader.RetargetPath(_clock.CurrentNavigation, newPath);
        _loader.SetZoom(1.0);

        Assert.Null(_loader.PendingLoad);
        Assert.Empty(_decoder.OriginalPaths);
        Assert.Null(_loader.HeldOriginal);
    }

    [Fact]
    public void RetargetPath_AtFit_DoesNotDecodeUntilTheViewerZooms()
    {
        Present("a.jpg");
        var newPath = Make("b.jpg");
        _clock.StopForAction();

        _loader.RetargetPath(_clock.CurrentNavigation, newPath);

        Assert.Null(_loader.PendingLoad);
        Assert.Empty(_decoder.OriginalPaths);
        Assert.False(_loader.IsShowingOriginal);
    }

    [Fact]
    public async Task RetargetPath_WhileZoomedWithoutOriginal_RestartsTheDecodeUnderTheNewPath()
    {
        using var gateOld = new SemaphoreSlim(0);
        _decoder.GateByName["a.jpg"] = gateOld;
        _loader.SetZoom(1.0);
        Present("a.jpg");
        var newPath = Make("b.jpg");
        var oldLoad = _loader.PendingLoad;
        Assert.NotNull(oldLoad);
        _clock.StopForAction();

        _loader.RetargetPath(_clock.CurrentNavigation, newPath);
        var newLoad = _loader.PendingLoad;

        Assert.NotNull(newLoad);
        Assert.NotSame(oldLoad, newLoad);
        gateOld.Release();
        await oldLoad!;
        await newLoad!;
        Assert.Contains(newPath, _decoder.OriginalPaths);
        Assert.True(_loader.IsShowingOriginal);
    }

    [Fact]
    public async Task RetargetPath_DecodeInFlightForTheOldPath_ItsPixelsAreNeverShown()
    {
        using var gateOld = new SemaphoreSlim(0);
        using var oldStarted = new SemaphoreSlim(0);
        _decoder.GateByName["a.jpg"] = gateOld;
        _decoder.StartedByName["a.jpg"] = oldStarted;
        _loader.SetZoom(1.0);
        var (_, oldPath) = Present("a.jpg");
        var oldLoad = _loader.PendingLoad;
        Assert.NotNull(oldLoad);
        await oldStarted.WaitAsync(); // the decode of the old path really runs now
        var newPath = Make("b.jpg");
        _clock.StopForAction();

        _loader.RetargetPath(_clock.CurrentNavigation, newPath);
        gateOld.Release(); // the old decode ends AFTER the retarget
        await oldLoad!;

        var newLoad = _loader.PendingLoad;
        if (newLoad is not null) await newLoad;
        var oldPixels = _decoder.ResultFor(oldPath);
        Assert.NotNull(oldPixels);
        Assert.DoesNotContain(oldPixels!.PlatformImage, _shown);
        Assert.Equal([oldPath, newPath], _decoder.OriginalPaths);
        var held = _loader.HeldOriginal;
        Assert.NotNull(held);
        Assert.Same(_decoder.ResultFor(newPath)!.PlatformImage, held!.PlatformImage);
    }

    [Fact]
    public async Task RetargetPath_HeldOriginal_StaysHeldAndShownWithoutANewDecode()
    {
        using var gate = new SemaphoreSlim(0);
        _decoder.GateByName["a.jpg"] = gate;
        _loader.SetZoom(1.0);
        Present("a.jpg");
        var load = _loader.PendingLoad;
        Assert.NotNull(load);
        gate.Release();
        await load!;
        Assert.True(_loader.IsShowingOriginal);
        var held = _loader.HeldOriginal;
        var showCount = _shown.Count;
        var newPath = Make("b.jpg");
        _clock.StopForAction();

        _loader.RetargetPath(_clock.CurrentNavigation, newPath);

        Assert.Same(held, _loader.HeldOriginal);
        Assert.True(_loader.IsShowingOriginal);
        Assert.Equal(showCount, _shown.Count); // the original is not shown a second time
        Assert.Single(_decoder.OriginalPaths);
        Assert.Null(_loader.PendingLoad);
    }

    [Fact]
    public async Task RetargetPath_HeldOriginal_ZoomOutAndInAgainStillWorksWithoutDecoding()
    {
        using var gate = new SemaphoreSlim(0);
        _decoder.GateByName["a.jpg"] = gate;
        _loader.SetZoom(1.0);
        Present("a.jpg");
        var load = _loader.PendingLoad;
        Assert.NotNull(load);
        gate.Release();
        await load!;
        var held = _loader.HeldOriginal!;
        var newPath = Make("b.jpg");
        _clock.StopForAction();
        _loader.RetargetPath(_clock.CurrentNavigation, newPath);

        _loader.SetZoom(null); // Fit: back to the preview (the loader's target is alive again)
        Assert.False(_loader.IsShowingOriginal);
        _loader.SetZoom(1.0);

        Assert.True(_loader.IsShowingOriginal);
        Assert.Same(held, _loader.HeldOriginal);
        Assert.Single(_decoder.OriginalPaths);
    }

    [Fact]
    public async Task RetargetPath_AfterFailedDecode_FailureStaysLatchedForTheSamePixels()
    {
        using var gate = new SemaphoreSlim(0);
        _decoder.GateByName["a.jpg"] = gate;
        _decoder.FailNames.Add("a.jpg");
        _loader.SetZoom(1.0);
        Present("a.jpg");
        var load = _loader.PendingLoad;
        Assert.NotNull(load);
        gate.Release();
        await load!;
        Assert.Single(_decoder.OriginalPaths);
        var newPath = Make("b.jpg");
        _clock.StopForAction();

        _loader.RetargetPath(_clock.CurrentNavigation, newPath);
        _loader.SetZoom(2.0);

        Assert.Null(_loader.PendingLoad);
        Assert.Single(_decoder.OriginalPaths); // no retry loop on every zoom step
    }

    [Fact]
    public async Task RetargetPath_NewPresentAfterwards_ResetsTargetNormally()
    {
        Present("a.jpg");
        var newPath = Make("b.jpg");
        _clock.StopForAction();
        _loader.RetargetPath(_clock.CurrentNavigation, newPath);

        using var gate = new SemaphoreSlim(0);
        _decoder.GateByName["c.jpg"] = gate;
        var (_, other) = Present("c.jpg");
        _loader.SetZoom(1.0);
        var load = _loader.PendingLoad;
        Assert.NotNull(load);
        gate.Release();
        await load!;

        Assert.Equal([other], _decoder.OriginalPaths);
    }

    private sealed class RecordingDecoder : IImageDecoder
    {
        private readonly object _gate = new();
        private readonly List<string> _paths = [];
        private readonly Dictionary<string, FakeImage> _results = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SemaphoreSlim> GateByName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SemaphoreSlim> StartedByName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FailNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> OriginalPaths
        {
            get { lock (_gate) return [.. _paths]; }
        }

        public FakeImage? ResultFor(string path)
        {
            lock (_gate) return _results.GetValueOrDefault(path);
        }

        public IDecodedImage Decode(DecodeRequest request)
        {
            var name = Path.GetFileName(request.Path);
            if (!request.Box.IsUnbounded) return new FakeImage(600, 400, 6000, 4000, downscaled: true);
            lock (_gate) _paths.Add(request.Path);
            if (StartedByName.TryGetValue(name, out var started)) started.Release();
            if (GateByName.TryGetValue(name, out var gate)) gate.Wait();
            if (FailNames.Contains(name)) throw new InvalidOperationException("original decode failed");
            var image = new FakeImage(6000, 4000, 6000, 4000, downscaled: false);
            lock (_gate) _results[request.Path] = image;
            return image;
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
