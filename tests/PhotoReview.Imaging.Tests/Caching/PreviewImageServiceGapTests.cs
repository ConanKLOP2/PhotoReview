using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>RV-T40 (viewer joiner retries after the creator is cancelled before a slot) and RV-T41 (ClearCache during an in-flight decode).</summary>
[Trait("Category", "HotPath")]
public sealed class PreviewImageServiceGapTests : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TempRoot _root = new("preview-gap");
    private readonly List<PreviewImageService> _services = [];

    public void Dispose() { } // _root is disposed in DisposeAsync, after the persist workers stop

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_services.Select(s => s.ShutdownPersistWorkersAsync()));
        await Task.WhenAll(_services.Select(s => s.WaitForPruneAsync(TimeSpan.FromSeconds(5))));
        _root.Dispose();
    }

    private static BitmapSource Bitmap()
    {
        var pixels = new byte[8 * 6 * 4];
        Array.Fill(pixels, (byte)90);
        var bitmap = BitmapSource.Create(8, 6, 96, 96, PixelFormats.Bgr32, null, pixels, 8 * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Decodes of paths with a registered gate block (on the decoding thread) until the gate is released.</summary>
    private sealed class GatedDecoder : IImageDecoder
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
        private readonly ConcurrentDictionary<string, int> _calls = new();

        public void Gate(string path) => _gates[path] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started(string path) => _started.GetOrAdd(path, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        public void Release(string path) { if (_gates.TryGetValue(path, out var gate)) gate.TrySetResult(); }
        public int Calls(string path) => _calls.GetValueOrDefault(path);

        public IDecodedImage Decode(DecodeRequest request)
        {
            _calls.AddOrUpdate(request.Path, 1, (_, n) => n + 1);
            _started.GetOrAdd(request.Path, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            if (_gates.TryGetValue(request.Path, out var gate)) gate.Task.Wait(Timeout);
            return new WpfDecodedImage(Bitmap(), downscaled: true, originalWidth: 800, originalHeight: 600);
        }

        public ImageInfo ReadInfo(string path) => new(800, 600);
    }

    private PreviewImageService CreateService(GatedDecoder decoder, ReviewMetrics metrics, string? diskDirectory)
    {
        var service = new PreviewImageService(metrics, () => false, () => 256, capacityBytes: 64L * 1024 * 1024,
            diskCacheDirectory: diskDirectory ?? _root.Dir("unused-cache"), disableDiskCacheOverride: diskDirectory is null, decoder: decoder);
        _services.Add(service);
        return service;
    }

    [Fact(DisplayName = "A viewer joiner whose creator is cancelled before it got a decode slot retries and gets an image; the creator gets OperationCanceled; no in-flight entry is left")]
    public async Task ViewerJoiner_CreatorCancelledBeforeSlot_JoinerRetriesAndSucceeds()
    {
        var decoder = new GatedDecoder();
        var metrics = new ReviewMetrics();
        var service = CreateService(decoder, metrics, diskDirectory: null);
        var a = _root.File("a.jpg", 1, 2, 3, 4);
        var b = _root.File("b.jpg", 1, 2, 3, 4);
        var c = _root.File("c.jpg", 1, 2, 3, 4);
        decoder.Gate(a);
        decoder.Gate(b);
        var keyA = service.GetCurrentCacheKey(a);
        var keyB = service.GetCurrentCacheKey(b);
        var keyC = service.GetCurrentCacheKey(c);
        // Two blocked viewer decodes occupy every viewer slot, so c's decode cannot start.
        var blockerA = service.GetViewerPreviewAsync(a, keyA, CancellationToken.None);
        var blockerB = service.GetViewerPreviewAsync(b, keyB, CancellationToken.None);
        await decoder.Started(a).WaitAsync(Timeout);
        await decoder.Started(b).WaitAsync(Timeout);
        Assert.Equal(PreviewImageService.ViewerDecodeSlots, service.ActiveViewerDecodes);

        using var creatorCts = new CancellationTokenSource();
        var creator = service.GetViewerPreviewAsync(c, keyC, creatorCts.Token);
        var joiner = service.GetViewerPreviewAsync(c, keyC, CancellationToken.None);
        Assert.Equal(1, metrics.Snapshot().InflightJoins); // the second navigation joined the first one's Lazy
        creatorCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creator.WaitAsync(Timeout));
        decoder.Release(a); // frees a slot for the joiner's own retry
        Assert.NotNull(await joiner.WaitAsync(Timeout));
        decoder.Release(b);
        await Task.WhenAll(blockerA, blockerB).WaitAsync(Timeout);

        Assert.Equal(1, decoder.Calls(c)); // only the joiner's retry decoded; the cancelled creator never did
        Assert.False(service.HasInflightPreview(keyA));
        Assert.False(service.HasInflightPreview(keyB));
        Assert.False(service.HasInflightPreview(keyC));
    }

    [Fact(DisplayName = "ClearCache during an in-flight decode: the caller still gets the image, but it is neither cached in RAM nor persisted to disk")]
    public async Task ClearCache_DuringInflightDecode_ResultReturnedButNotCachedOrPersisted()
    {
        var decoder = new GatedDecoder();
        var disk = _root.Dir("disk");
        var service = CreateService(decoder, new ReviewMetrics(), disk);
        var stale = _root.File("stale.jpg", 1, 2, 3, 4);
        var fresh = _root.File("fresh.jpg", 1, 2, 3, 4);
        decoder.Gate(stale);
        var staleKey = service.GetCurrentCacheKey(stale);
        var pending = service.GetPreviewAsync(stale, staleKey);
        await decoder.Started(stale).WaitAsync(Timeout);

        service.ClearCache(); // epoch bump while the decode runs
        decoder.Release(stale);
        var image = await pending.WaitAsync(Timeout);

        Assert.NotNull(image);
        Assert.False(service.TryGetCachedPreview(staleKey, out _));
        // Control: a decode that starts after the clear is cached and persisted, so the empty result for the stale one is meaningful.
        var freshKey = service.GetCurrentCacheKey(fresh);
        await service.GetPreviewAsync(fresh, freshKey).WaitAsync(Timeout);
        Assert.True(service.TryGetCachedPreview(freshKey, out _));
        await service.ShutdownPersistWorkersAsync();
        Assert.Single(Directory.GetFiles(disk, "*.pv4"));
    }
}
