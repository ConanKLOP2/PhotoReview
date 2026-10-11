using System.IO;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Decoding;
using PhotoReview.TestSupport;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// A preload's in-flight decode can be refused by the RAW full-decode gate (<see cref="DecoderBusyException"/>). A viewer
/// that joined that shared decode must not inherit the refusal: it retries with its own decode. The creator (the
/// preload) still sees the busy signal so the PreloadScheduler can handle it.
/// </summary>
public sealed class PreviewImageServiceBusyJoinTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TempRoot _root = new("preview-busy-join");
    private readonly BusyOnFirstCallDecoder _decoder = new();
    private readonly PreviewImageService _service;
    private readonly ReviewMetrics _metrics = new();
    private readonly string _path;

    public PreviewImageServiceBusyJoinTests()
    {
        _path = _root.File(Path.Combine("images", "a.jpg"), 1, 2, 3, 4);
        _service = new PreviewImageService(_metrics, () => false, () => 256, WpfBitmapSourceCodec.Instance, capacityBytes: 64L * 1024 * 1024,
            diskCacheDirectory: _root.Dir("cache"), disableDiskCacheOverride: true, decoder: _decoder);
    }

    public void Dispose()
    {
        _decoder.Release();
        _service.ShutdownPersistWorkersAsync().GetAwaiter().GetResult();
        _root.Dispose();
    }

    [Fact(DisplayName = "A viewer that joined a preload's decode which then throws DecoderBusy succeeds with its own decode")]
    public async Task GetViewerPreview_JoinedPreloadThrowsBusy_ViewerRetriesAndSucceeds()
    {
        var key = _service.GetCurrentCacheKey(_path);
        var preload = _service.GetPreviewAsync(_path, key);
        await _decoder.FirstCallStarted.WaitAsync(Timeout);
        var viewer = _service.GetViewerPreviewAsync(_path, key, CancellationToken.None);
        Assert.Equal(1, _metrics.Snapshot().InflightJoins); // the viewer joined the preload's Lazy synchronously

        _decoder.Release();

        Assert.NotNull(await viewer.WaitAsync(Timeout));
        await Assert.ThrowsAsync<DecoderBusyException>(() => preload.WaitAsync(Timeout));
        Assert.Equal(2, _decoder.Calls);
    }

    [Fact(DisplayName = "Two viewers that joined a busy-refused preload both succeed with a single retry decode")]
    public async Task GetViewerPreview_TwoJoinersOfBusyPreload_BothSucceed()
    {
        var key = _service.GetCurrentCacheKey(_path);
        var preload = _service.GetPreviewAsync(_path, key);
        await _decoder.FirstCallStarted.WaitAsync(Timeout);
        var viewer1 = _service.GetViewerPreviewAsync(_path, key, CancellationToken.None);
        var viewer2 = _service.GetViewerPreviewAsync(_path, key, CancellationToken.None);
        Assert.Equal(2, _metrics.Snapshot().InflightJoins);

        _decoder.Release();

        var images = await Task.WhenAll(viewer1, viewer2).WaitAsync(Timeout);
        Assert.All(images, Assert.NotNull);
        await Assert.ThrowsAsync<DecoderBusyException>(() => preload.WaitAsync(Timeout));
        Assert.Equal(2, _decoder.Calls); // one refused preload decode + one shared retry
    }

    [Fact(DisplayName = "A preload (non-viewer) joiner of a busy-refused preload also retries instead of inheriting the refusal")]
    public async Task GetPreview_JoinerOfBusyCreator_Retries()
    {
        var key = _service.GetCurrentCacheKey(_path);
        var creator = _service.GetPreviewAsync(_path, key);
        await _decoder.FirstCallStarted.WaitAsync(Timeout);
        var joiner = _service.GetPreviewAsync(_path, key);
        Assert.Equal(1, _metrics.Snapshot().InflightJoins);

        _decoder.Release();

        Assert.NotNull(await joiner.WaitAsync(Timeout));
        await Assert.ThrowsAsync<DecoderBusyException>(() => creator.WaitAsync(Timeout));
    }

    [Fact(DisplayName = "The creator of a busy-refused decode leaves no in-flight entry behind, so a later request decodes afresh")]
    public async Task GetPreview_CreatorBusy_RemovesInflightEntry()
    {
        var key = _service.GetCurrentCacheKey(_path);
        _decoder.Release();
        await Assert.ThrowsAsync<DecoderBusyException>(() => _service.GetPreviewAsync(_path, key).WaitAsync(Timeout));

        Assert.False(_service.HasInflightPreview(key));
        Assert.NotNull(await _service.GetPreviewAsync(_path, key).WaitAsync(Timeout));
    }

    /// <summary>The first Decode blocks until <see cref="Release"/>, then throws DecoderBusyException; every later call succeeds.</summary>
    private sealed class BusyOnFirstCallDecoder : IImageDecoder
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstCallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task FirstCallStarted => _firstCallStarted.Task;
        public int Calls => Volatile.Read(ref _calls);

        public void Release() => _release.TrySetResult();

        public IDecodedImage Decode(DecodeRequest request)
        {
            if (Interlocked.Increment(ref _calls) != 1) return new FakeImage();
            _firstCallStarted.TrySetResult();
            _release.Task.Wait(Timeout);
            throw new DecoderBusyException("test: the gate refused this preload");
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
