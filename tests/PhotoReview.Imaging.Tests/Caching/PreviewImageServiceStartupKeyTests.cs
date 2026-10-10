using System.IO;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Decoding;
using PhotoReview.TestSupport;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// P-1 startup: the launch file's decode starts before the window exists, keyed with a predicted decode box
/// (<c>GetCurrentCacheKey(path, box)</c>). The presenter later asks with the real viewport box
/// (<c>GetCurrentCacheKey(path)</c>): when the boxes match it must join the early decode (or hit its cache entry), when
/// they differ it must decode on its own and never be handed an image for another box.
/// </summary>
public sealed class PreviewImageServiceStartupKeyTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly DecodeBox Predicted = new(1280, 768);
    private static readonly DecodeBox Other = new(1920, 1152);

    private readonly TempRoot _root = new("preview-startup-key");
    private readonly GatedCountingDecoder _decoder = new();
    private readonly ReviewMetrics _metrics = new();
    private readonly string _path;
    private DecodeBox _viewport = Predicted;
    private bool _original;
    private readonly PreviewImageService _service;

    public PreviewImageServiceStartupKeyTests()
    {
        _path = _root.File(Path.Combine("images", "launch.jpg"), 1, 2, 3, 4);
        _service = new PreviewImageService(_metrics, () => _original, () => _viewport, capacityBytes: 64L * 1024 * 1024,
            diskCacheDirectory: _root.Dir("cache"), disableDiskCacheOverride: true, decoder: _decoder);
    }

    public void Dispose()
    {
        _decoder.Release();
        _service.ShutdownPersistWorkersAsync().GetAwaiter().GetResult();
        _root.Dispose();
    }

    [Fact(DisplayName = "The key built for an explicit box equals the current key when the viewport box is that box")]
    public void GetCurrentCacheKey_ExplicitBoxEqualToViewport_EqualsCurrentKey()
    {
        _viewport = Predicted;

        Assert.Equal(_service.GetCurrentCacheKey(_path), _service.GetCurrentCacheKey(_path, Predicted));
    }

    [Fact(DisplayName = "The key built for an explicit box differs from the current key when the viewport box is another one")]
    public void GetCurrentCacheKey_ExplicitBoxDifferentFromViewport_DiffersFromCurrentKey()
    {
        _viewport = Other;

        var current = _service.GetCurrentCacheKey(_path);
        var predicted = _service.GetCurrentCacheKey(_path, Predicted);

        Assert.NotEqual(current, predicted);
        Assert.Equal(Predicted, predicted.TargetBox);
        Assert.Equal(Other, current.TargetBox);
    }

    [Fact(DisplayName = "In Original loading mode the explicit box is ignored: both keys are unbounded and equal")]
    public void GetCurrentCacheKey_OriginalMode_ExplicitBoxIsIgnored()
    {
        _original = true;
        _viewport = Other;

        var predicted = _service.GetCurrentCacheKey(_path, Predicted);

        Assert.Equal(DecodeBox.Unbounded, predicted.TargetBox);
        Assert.Equal(_service.GetCurrentCacheKey(_path), predicted);
    }

    [Fact(DisplayName = "A viewer asking with the real viewport box joins the early decode started with the matching predicted box")]
    public async Task GetViewerPreviewAsync_ViewportMatchesPredictedBox_JoinsTheEarlyDecode()
    {
        _viewport = Predicted;
        var early = _service.GetViewerPreviewAsync(_path, _service.GetCurrentCacheKey(_path, Predicted), CancellationToken.None);
        await _decoder.FirstCallStarted.WaitAsync(Timeout);

        var presenter = _service.GetViewerPreviewAsync(_path, _service.GetCurrentCacheKey(_path), CancellationToken.None);
        Assert.Equal(1, _metrics.Snapshot().InflightJoins); // joined synchronously, before the early decode finished

        _decoder.Release();
        var images = await Task.WhenAll(early, presenter).WaitAsync(Timeout);

        Assert.Same(images[0], images[1]);
        Assert.Equal(1, _decoder.Calls);
    }

    [Fact(DisplayName = "A viewer whose viewport box differs from the predicted one decodes on its own and is never handed the other box's image")]
    public async Task GetViewerPreviewAsync_ViewportDiffersFromPredictedBox_DoesNotJoin()
    {
        _viewport = Other; // the prediction was wrong
        var early = _service.GetViewerPreviewAsync(_path, _service.GetCurrentCacheKey(_path, Predicted), CancellationToken.None);
        await _decoder.FirstCallStarted.WaitAsync(Timeout);

        var presenter = _service.GetViewerPreviewAsync(_path, _service.GetCurrentCacheKey(_path), CancellationToken.None);
        Assert.Equal(0, _metrics.Snapshot().InflightJoins);

        _decoder.Release();
        var images = await Task.WhenAll(early, presenter).WaitAsync(Timeout);

        Assert.NotSame(images[0], images[1]);
        Assert.Equal(2, _decoder.Calls);
        Assert.True(_service.TryGetCachedPreview(_service.GetCurrentCacheKey(_path, Predicted), out var cachedEarly));
        Assert.Same(images[0], cachedEarly);
        Assert.True(_service.TryGetCachedPreview(_service.GetCurrentCacheKey(_path), out var cachedReal));
        Assert.Same(images[1], cachedReal);
    }

    [Fact(DisplayName = "After the early decode finished, the real-viewport key is a RAM cache hit when the boxes match")]
    public async Task GetViewerPreviewAsync_EarlyDecodeFinished_ViewportKeyHitsTheCache()
    {
        _viewport = Predicted;
        _decoder.Release();
        var early = await _service.GetViewerPreviewAsync(_path, _service.GetCurrentCacheKey(_path, Predicted), CancellationToken.None)
            .WaitAsync(Timeout);

        Assert.True(_service.TryGetCachedPreview(_service.GetCurrentCacheKey(_path), out var cached));
        Assert.Same(early, cached);
        Assert.Equal(1, _decoder.Calls);
    }

    /// <summary>Every Decode counts itself; calls block until <see cref="Release"/> (the first one signals that it started).</summary>
    private sealed class GatedCountingDecoder : IImageDecoder
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstCallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task FirstCallStarted => _firstCallStarted.Task;
        public int Calls => Volatile.Read(ref _calls);

        public void Release() => _release.TrySetResult();

        public IDecodedImage Decode(DecodeRequest request)
        {
            Interlocked.Increment(ref _calls);
            _firstCallStarted.TrySetResult();
            _release.Task.Wait(Timeout);
            return new FakeImage();
        }

        public ImageInfo ReadInfo(string path) => new(1, 1);
    }

    private sealed class FakeImage : IDecodedImage
    {
        public int PixelWidth => 1;
        public int PixelHeight => 1;
        public bool Downscaled => false;
        public int Orientation => 1;
        public long EstimatedBytes => 4;
        public object PlatformImage { get; } = new();
    }
}
