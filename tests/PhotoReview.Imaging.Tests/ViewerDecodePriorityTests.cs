using System.Collections.Concurrent;
using System.IO;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using PhotoReview.TestSupport;

namespace PhotoReview.Imaging.Tests;

/// <summary>perf(preload): the viewer's own decode lane (PreviewImageService.GetViewerPreviewAsync).</summary>
[Trait("Category", "Slow")]
#pragma warning disable CA1001 // _root (TempRoot) is disposed in DisposeAsync via IAsyncLifetime, which CA1001 does not recognize
public sealed class ViewerDecodePriorityTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TempRoot _root = new("viewer-priority");
    private readonly GatedDecoder _decoder = new();
    private readonly PreviewImageService _service;
    private readonly string[] _paths;

    public ViewerDecodePriorityTests()
    {
        var folder = _root.Dir("images");
        _paths = Enumerable.Range(0, 4).Select(i =>
        {
            var path = Path.Combine(folder, $"v{i}.jpg");
            File.WriteAllBytes(path, [1, 2, 3, (byte)i]);
            return path;
        }).ToArray();
        _service = new PreviewImageService(new ReviewMetrics(), () => false, () => 256, capacityBytes: 64L * 1024 * 1024,
            diskCacheDirectory: _root.Dir("cache"), disableDiskCacheOverride: true, decoder: _decoder);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _decoder.ReleaseAll();
        await _service.ShutdownPersistWorkersAsync();
        _root.Dispose();
    }

    private ImageCacheKey Key(int i) => _service.GetCurrentCacheKey(_paths[i]);

    [Fact(DisplayName = "The viewer decode runs on a dedicated above-normal-priority thread, not a thread-pool/preload thread")]
    public async Task ViewerDecode_RunsOnDedicatedPriorityThread()
    {
        _decoder.ReleaseAll();
        await _service.GetViewerPreviewAsync(_paths[0], Key(0), CancellationToken.None).WaitAsync(Timeout);

        var call = Assert.Single(_decoder.Calls);
        Assert.False(call.PoolThread);
        Assert.Equal(ThreadPriority.AboveNormal, call.Priority);
    }

    [Fact(DisplayName = "A superseded viewer decode still waiting for a viewer slot is dropped without decoding")]
    public async Task SupersededViewerDecode_WaitingForSlot_IsNeverStarted()
    {
        var a = _service.GetViewerPreviewAsync(_paths[0], Key(0), CancellationToken.None);
        var b = _service.GetViewerPreviewAsync(_paths[1], Key(1), CancellationToken.None);
        await _decoder.WaitForCallsAsync(PreviewImageService.ViewerDecodeSlots); // both viewer slots busy
        using var superseded = new CancellationTokenSource();
        var c = _service.GetViewerPreviewAsync(_paths[2], Key(2), superseded.Token);

        superseded.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.WaitAsync(Timeout));
        _decoder.ReleaseAll();
        await Task.WhenAll(a, b).WaitAsync(Timeout);
        Assert.DoesNotContain(_decoder.Calls, call => call.Path == _paths[2]);
        Assert.False(_service.HasInflightPreview(Key(2)));
    }

    [Fact(DisplayName = "A viewer decode superseded during its burst start delay is never started")]
    public async Task SupersededViewerDecode_DuringStartDelay_IsNeverStarted()
    {
        _decoder.ReleaseAll();
        using var superseded = new CancellationTokenSource();
        var pending = _service.GetViewerPreviewAsync(_paths[0], Key(0), superseded.Token, TimeSpan.FromSeconds(30));

        superseded.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Timeout));
        Assert.Empty(_decoder.Calls);
    }

    [Fact(DisplayName = "A preload that joined a viewer decode which is then dropped decodes the image itself")]
    public async Task PreloadJoiningDroppedViewerDecode_StartsItsOwn()
    {
        _decoder.ReleaseAll();
        using var superseded = new CancellationTokenSource();
        var viewer = _service.GetViewerPreviewAsync(_paths[0], Key(0), superseded.Token, TimeSpan.FromSeconds(30));
        Assert.True(_service.HasInflightPreview(Key(0)));
        var preload = _service.GetPreviewAsync(_paths[0], Key(0)); // joins the pending viewer decode

        superseded.Cancel();

        var image = await preload.WaitAsync(Timeout);
        Assert.NotNull(image);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => viewer.WaitAsync(Timeout));
        Assert.Single(_decoder.Calls, call => call.Path == _paths[0]);
    }

    [Fact(DisplayName = "A viewer decode that already started finishes and is kept in the cache after being superseded")]
    public async Task StartedViewerDecode_IsKeptAfterSupersede()
    {
        using var superseded = new CancellationTokenSource();
        var viewer = _service.GetViewerPreviewAsync(_paths[0], Key(0), superseded.Token);
        await _decoder.WaitForCallsAsync(1);
        Assert.Equal(1, _service.ActiveViewerDecodes);

        superseded.Cancel();
        _decoder.ReleaseAll();

        Assert.NotNull(await viewer.WaitAsync(Timeout));
        Assert.True(_service.TryGetCachedPreview(Key(0), out _));
        Assert.Equal(0, _service.ActiveViewerDecodes);
    }

    private sealed record DecodeCall(string Path, bool PoolThread, ThreadPriority Priority);

    private sealed class GatedDecoder : IImageDecoder
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<DecodeCall> _calls = new();

        public IReadOnlyCollection<DecodeCall> Calls => _calls;

        public void ReleaseAll() => _gate.TrySetResult();

        public async Task WaitForCallsAsync(int count)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (_calls.Count < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {count} decode(s).");
                await Task.Delay(5);
            }
        }

        public IDecodedImage Decode(DecodeRequest request)
        {
            var thread = Thread.CurrentThread;
            _calls.Enqueue(new DecodeCall(request.Path, thread.IsThreadPoolThread, thread.Priority));
            _gate.Task.Wait(Timeout);
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