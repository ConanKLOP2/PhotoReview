using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>perf(preload): direction-aware order and burst lead (PreloadOrderService, NavigationPace).</summary>
public sealed class PreloadOrderDirectionTests
{
    [Fact(DisplayName = "Direction +1 with no lead is exactly the original preload order")]
    public void ForwardNoLead_MatchesOriginalOrder()
    {
        foreach (var fullFolder in new[] { false, true })
            Assert.Equal(PreloadOrderService.Build(40, 100, fullFolder).ToArray(),
                PreloadOrderService.Build(40, 100, fullFolder, direction: 1, lead: 0).ToArray());
    }

    [Fact(DisplayName = "After Prev, the 32-image window is behind the user and only a small window stays ahead")]
    public void Backward_PrioritizesLowerIndices()
    {
        var order = PreloadOrderService.Build(center: 50, count: 100, fullFolder: false, direction: -1, lead: 0).ToArray();

        Assert.Equal(Enumerable.Range(18, 32).Reverse(), order.Take(32));
        Assert.Equal(Enumerable.Range(51, PreloadOrderService.BackwardLookahead), order.Skip(32));
    }

    [Fact(DisplayName = "A burst lead starts the travel window lead+1 ahead, then the skipped images, then the small window behind")]
    public void Lead_ShiftsTravelWindow()
    {
        var order = PreloadOrderService.Build(center: 50, count: 200, fullFolder: false, direction: 1, lead: 10).ToArray();

        Assert.Equal(61, order[0]);
        Assert.Equal(Enumerable.Range(61, 32), order.Take(32));
        Assert.Equal(Enumerable.Range(51, 10), order.Skip(32).Take(10));
        Assert.Equal(Enumerable.Range(42, 8).Reverse(), order.Skip(42));
    }

    [Theory(DisplayName = "Full-folder order visits every other image exactly once for any direction and lead")]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, 12)]
    [InlineData(-1, 24)]
    public void FullFolder_CoversEveryImageOnce(int direction, int lead)
    {
        var order = PreloadOrderService.Build(center: 30, count: 90, fullFolder: true, direction, lead).ToArray();

        Assert.Equal(89, order.Length);
        Assert.Equal(89, order.Distinct().Count());
        Assert.DoesNotContain(30, order);
        Assert.All(order, i => Assert.InRange(i, 0, 89));
    }
}

public sealed class NavigationPaceTests
{
    private long _now = 1_000_000;

    private NavigationPace Create() => new(() => _now);

    private void Advance(double ms) => _now += (long)(ms * Stopwatch.Frequency / 1000);

    private void Steps(NavigationPace pace, int from, int count, int step, double intervalMs)
    {
        pace.Record(from);
        for (var i = 1; i <= count; i++)
        {
            Advance(intervalMs);
            pace.Record(from + i * step);
        }
    }

    [Fact(DisplayName = "Direction follows the last move and a repeated index is not a new key")]
    public void Direction_FollowsLastMove()
    {
        var pace = Create();
        Steps(pace, 10, 3, +1, 600);
        Assert.Equal(1, pace.Direction);

        Advance(600);
        pace.Record(12);
        Assert.Equal(-1, pace.Direction);

        pace.Record(12); // post-present kick for the same navigation: no-op
        Assert.Equal(-1, pace.Direction);
    }

    [Fact(DisplayName = "Lead is about keyRate x decodeTime once a burst is established, and zero before")]
    public void Lead_IsRateTimesDecode()
    {
        var pace = Create();
        pace.Record(0);
        Advance(33);
        pace.Record(1);
        Assert.Equal(0, pace.GetLead(350)); // one interval is a double-tap, not a burst

        Advance(33);
        pace.Record(2);
        Assert.Equal(11, pace.GetLead(350)); // ceil(350 / 33)
        Assert.Equal(0, pace.GetLead(20));   // decode faster than keys: nothing to skip
        Assert.Equal(NavigationPace.MaxLead, pace.GetLead(10_000));
    }

    [Fact(DisplayName = "The lead drops to zero once keys stop arriving")]
    public void Lead_ExpiresWhenBurstStops()
    {
        var pace = Create();
        Steps(pace, 0, 10, +1, 33);
        Assert.True(pace.GetLead(350) > 0);

        Advance(NavigationPace.BurstIdleFloorMs + 1);
        Assert.Equal(0, pace.GetLead(350));
    }

    [Fact(DisplayName = "A jump or a pause restarts the key-rate estimate")]
    public void JumpOrPause_ResetsEstimate()
    {
        var pace = Create();
        Steps(pace, 0, 10, +1, 33);
        Advance(33);
        pace.Record(500); // Home/End-like jump
        Assert.Equal(0, pace.GetLead(350));

        Steps(pace, 500, 1, +1, 33);
        Assert.Equal(0, pace.GetLead(350)); // needs a fresh run of short steps

        var paused = Create();
        Steps(paused, 0, 10, +1, 33);
        Advance(NavigationPace.MaxStepIntervalMs + 1);
        paused.Record(11);
        Assert.Equal(0, paused.GetLead(350));
    }

    [Fact(DisplayName = "Viewer start delay is zero when calm and a bounded fraction above one interval during a burst")]
    public void ViewerStartDelay()
    {
        var pace = Create();
        Assert.Equal(TimeSpan.Zero, pace.GetViewerStartDelay(350));

        Steps(pace, 0, 10, +1, 33);
        Assert.Equal(TimeSpan.FromMilliseconds(33 * 1.5), pace.GetViewerStartDelay(350));

        var slow = Create();
        Steps(slow, 0, 10, +1, 200);
        Assert.Equal(TimeSpan.FromMilliseconds(NavigationPace.MaxViewerStartDelayMs), slow.GetViewerStartDelay(350));
    }
}

/// <summary>perf(preload): scheduler behaviour under navigation (direction, lead, wake, superseded skip, viewer priority).</summary>
[Trait("Category", "Slow")]
public sealed class BurstPreloadSchedulerTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview-BurstPreload", Guid.NewGuid().ToString("N"));
    private readonly string[] _files;
    private readonly CatalogEntry[] _entries;
    private readonly ReviewMetrics _metrics = new();
    private long _now = 1_000_000;

    public BurstPreloadSchedulerTests()
    {
        Directory.CreateDirectory(_root);
        _files = Enumerable.Range(0, 100).Select(i =>
        {
            var path = Path.Combine(_root, $"image-{i:D3}.jpg");
            File.WriteAllBytes(path, [1, 2, 3, (byte)i]);
            return path;
        }).ToArray();
        _entries = Array.ConvertAll(_files, f => new CatalogEntry(f));
    }

    private void Advance(double ms) => _now += (long)(ms * Stopwatch.Frequency / 1000);

    private PreloadScheduler Create(GatedTarget target, int workers) =>
        // Windowed (not whole-folder) preload: the tests reason about the 32/8 windows only.
        new(target, _metrics, () => _entries, () => 1, new PreloadOptions(WorkerCount: workers, FullFolderThresholdBytes: 0),
            new FakeMemoryProbe(true), ImmediateUiScheduler.Instance, pace: new NavigationPace(() => _now));

    private int IndexOf(string path) => Array.FindIndex(_files, f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));

    [Fact(DisplayName = "After Prev presses, preload starts with the image before the current one")]
    public async Task AfterPrev_PreloadsBackwardFirst()
    {
        using var target = new GatedTarget(blockAll: true);
        using var scheduler = Create(target, workers: 1);

        scheduler.NotifyNavigation(50);
        Advance(600);
        scheduler.NotifyNavigation(49);
        _ = scheduler.PreloadAroundAsync(49);

        Assert.Equal(48, IndexOf(await target.NextStartAsync()));
        target.ReleaseAll();
    }

    [Fact(DisplayName = "During a burst preload starts about keyRate x decodeTime ahead instead of at the next image")]
    public async Task Burst_StartsAtLeadOffset()
    {
        _metrics.RecordSourceRead(1, 350); // decode EWMA = 350 ms
        using var target = new GatedTarget(blockAll: true);
        using var scheduler = Create(target, workers: 1);

        scheduler.NotifyNavigation(10);
        Advance(33);
        scheduler.NotifyNavigation(11);
        Advance(33);
        scheduler.NotifyNavigation(12); // burst established: lead = ceil(350/33) = 11, scheduler self-starts

        Assert.Equal(12 + 11 + 1, IndexOf(await target.NextStartAsync()));
        target.ReleaseAll();
    }

    [Fact(DisplayName = "Images a burst has carried the user past (or will pass before decoding) are never started")]
    public async Task Burst_SkipsPassedImages()
    {
        _metrics.RecordSourceRead(1, 350);
        using var target = new GatedTarget(blockAll: true);
        using var scheduler = Create(target, workers: 2);

        scheduler.NotifyNavigation(10);
        for (var i = 11; i <= 12; i++) { Advance(33); scheduler.NotifyNavigation(i); }
        var first = IndexOf(await target.NextStartAsync());
        var second = IndexOf(await target.NextStartAsync());
        Assert.Equal([24, 25], new[] { first, second });

        for (var i = 13; i <= 30; i++) { Advance(33); scheduler.NotifyNavigation(i); }
        target.Release(_files[24]);

        // Center 30 + lead 11: the next start is 42, not 26..41 (passed before a decode could finish).
        Assert.Equal(42, IndexOf(await target.NextStartAsync()));
        target.ReleaseAll();
    }

    [Fact(DisplayName = "A navigation wakes a scheduler waiting on one long decode to fill its idle workers")]
    public async Task Navigation_WakesWaitingScheduler()
    {
        using var target = new GatedTarget(blockAll: true);
        for (var i = 2; i <= 32; i++) target.MarkCached(_files[i]);
        using var scheduler = Create(target, workers: 2);

        _ = scheduler.PreloadAroundAsync(0);
        Assert.Equal(1, IndexOf(await target.NextStartAsync())); // the only uncached image; 1 worker idle

        scheduler.NotifyNavigation(60); // jump: new neighbours need decoding while image 1 is still decoding

        Assert.Equal(61, IndexOf(await target.NextStartAsync()));
        Assert.False(target.IsReleased(_files[1]));
        target.ReleaseAll();
    }

    [Fact(DisplayName = "A queued preload item the user reached while it waited for a slot is dropped, not decoded")]
    public async Task QueuedItemReachedByUser_IsDropped()
    {
        using var target = new GatedTarget(blockAll: true, ignoreCancellation: true);
        using var scheduler = Create(target, workers: 1);

        var first = scheduler.PreloadAroundAsync(0);
        Assert.Equal(1, IndexOf(await target.NextStartAsync())); // holds the only slot, ignores cancellation
        scheduler.Cancel();

        // New lifetime: its first worker (image 6) is created synchronously and waits for the held slot.
        _ = scheduler.PreloadAroundAsync(5);
        scheduler.NotifyNavigation(6);  // the user is now ON image 6 (the viewer decodes it)
        target.Release(_files[1]);
        await first.WaitAsync(Timeout);

        Assert.Equal(7, IndexOf(await target.NextStartAsync()));
        Assert.DoesNotContain(_files[6], target.Started);
        target.ReleaseAll();
    }

    [Fact(DisplayName = "While the viewer is decoding, preload starts at most its reduced worker count")]
    public async Task ViewerDecoding_CapsPreloadStarts()
    {
        var cap = Math.Min(8, Math.Max(2, Environment.ProcessorCount / 3));
        using var target = new GatedTarget(blockAll: true) { ActiveViewerDecodes = 1 };
        using var scheduler = Create(target, workers: 8);

        _ = scheduler.PreloadAroundAsync(0);
        for (var i = 0; i < cap; i++) await target.NextStartAsync();
        await Task.Delay(100);
        Assert.Equal(cap, target.Started.Count);

        target.ActiveViewerDecodes = 0;
        scheduler.NotifyNavigation(0); // wakes the loop; the viewer is idle again
        for (var i = cap; i < 8; i++) await target.NextStartAsync();
        Assert.Equal(8, target.Started.Count);
        target.ReleaseAll();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class GatedTarget(bool blockAll, bool ignoreCancellation = false) : IPreloadTarget, IDisposable
    {
        private readonly ConcurrentDictionary<string, byte> _cached = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _startedSignal = new(0);
        private readonly ConcurrentQueue<string> _startOrder = new();
        private volatile bool _releaseAll;

        public ConcurrentQueue<string> Started { get; } = new();
        private volatile int _activeViewerDecodes;
        public int ActiveViewerDecodes { get => _activeViewerDecodes; set => _activeViewerDecodes = value; }
        public int CacheCount => _cached.Count;
        public long CacheBytes => 0;

        public void MarkCached(string path) => _cached.TryAdd(path, 0);
        public bool TryGetCachedPreview(string path) => _cached.ContainsKey(path);
        public bool TryGetCachedPreview(ImageCacheKey key) => _cached.Keys.Any(p => string.Equals(Path.GetFullPath(p).ToUpperInvariant(), key.Path, StringComparison.Ordinal));
        public ImageCacheKey GetCurrentCacheKey(string path) => ImageCacheKey.Create(path, false, 100);
        public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry) => GetCurrentCacheKey(entry.Path);

        private TaskCompletionSource Gate(string path) =>
            _gates.GetOrAdd(path, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public bool IsReleased(string path) => Gate(path).Task.IsCompleted;

        public void Release(string path) => Gate(path).TrySetResult();

        public void ReleaseAll()
        {
            _releaseAll = true;
            foreach (var gate in _gates.Values) gate.TrySetResult();
        }

        public async Task PreloadAsync(string path, CancellationToken cancellationToken = default)
        {
            Started.Enqueue(path);
            _startOrder.Enqueue(path);
            _startedSignal.Release();
            if (blockAll && !_releaseAll)
            {
                var gate = Gate(path).Task;
                if (ignoreCancellation) await gate;
                else await gate.WaitAsync(cancellationToken);
            }
            _cached.TryAdd(path, 0);
        }

        public async Task<string> NextStartAsync()
        {
            await _startedSignal.WaitAsync(Timeout);
            Assert.True(_startOrder.TryDequeue(out var path), "Timed out waiting for a preload start.");
            return path!;
        }


        public void Dispose()
        {
            ReleaseAll();
            _startedSignal.Dispose();
        }
    }
}
