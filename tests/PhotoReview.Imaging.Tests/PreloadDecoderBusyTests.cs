using System.Collections.Concurrent;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>A decoder refusing background work because its queue is full is a transient skip: no error, and the path is retried by a later pass.</summary>
[Trait("Category", "HotPath")]
public sealed class PreloadDecoderBusyTests
{
    private const int ImageCount = 40;
    private const int BlockedIndex = PreloadOrderService.ForwardLookahead;
    private const int BusyIndex = 2;
    private const int MarkerIndex = BusyIndex + 1; // examined by the scheduler loop right after the busy index, once per order pass
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "A DecoderBusyException preload is logged as Info, not Error, and retried by the next order pass")]
    public async Task BusyPreload_IsSkippedWithoutErrorAndRetriedOnTheNextPass()
    {
        var log = new RecordingLog();
        var target = new BusyTarget();
        using var scheduler = new PreloadScheduler(target, new ReviewMetrics(), () => target.Entries, () => 1,
            new PreloadOptions(WorkerCount: 4, FullFolderThresholdBytes: 1),
            new FakeMemoryProbe(true), ImmediateUiScheduler.Instance, log) { BusyNoted = _ => target.BusyWasRecorded() };

        var lifetime = scheduler.PreloadAroundAsync(0);
        await target.BlockedEntered.WaitAsync(TimeSpan.FromSeconds(10));
        await target.BusyRecorded(1).WaitAsync(Bound); // the backoff stamped pass 1 before the next pass begins
        Assert.Equal(1, target.PreloadCount(BusyIndex));
        Assert.False(target.IsCached(BusyIndex));

        // Navigations: same lifetime, new order passes. The busy path is cooling down for PreloadBusyBackoff.CooldownPasses of them.
        for (var pass = 2; pass <= 1 + PreloadBusyBackoff.CooldownPasses + 1; pass++)
        {
            Assert.Equal(1, target.PreloadCount(BusyIndex));
            _ = scheduler.PreloadAroundAsync(0);
            await target.WaitForPassesAsync(pass).WaitAsync(Bound);
        }
        target.ReleaseBlocked();
        await lifetime.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, target.PreloadCount(BusyIndex)); // a Failed outcome would never be retried in this lifetime
        Assert.True(target.IsCached(BusyIndex));
        Assert.Empty(log.Errors);
        Assert.Contains(log.Infos, message => message.Contains("busy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(DisplayName = "A busy path is skipped for the next three order passes, then retried once")]
    public async Task BusyPath_IsNotRetriedOnEveryOrderRebuild_ButAfterTheCooldown()
    {
        var target = new BusyTarget(busyAttempts: int.MaxValue);
        using var scheduler = new PreloadScheduler(target, new ReviewMetrics(), () => target.Entries, () => 1,
            new PreloadOptions(WorkerCount: 4, FullFolderThresholdBytes: 1),
            new FakeMemoryProbe(true), ImmediateUiScheduler.Instance, new RecordingLog()) { BusyNoted = _ => target.BusyWasRecorded() };
        _ = scheduler.PreloadAroundAsync(0);
        await target.WaitForPassesAsync(1).WaitAsync(Bound);
        await target.BusyRecorded(1).WaitAsync(Bound);

        var attemptsAfterPass = new List<int> { target.PreloadCount(BusyIndex) };
        for (var pass = 2; pass <= 5; pass++)
        {
            _ = scheduler.PreloadAroundAsync(0);
            await target.WaitForPassesAsync(pass).WaitAsync(Bound);
            if (pass == 5) await target.BusyRecorded(2).WaitAsync(Bound);
            attemptsAfterPass.Add(target.PreloadCount(BusyIndex));
        }

        target.ReleaseBlocked();
        // Passes 2..4 are inside the cooldown (no attempt); pass 5 retries.
        Assert.Equal([1, 1, 1, 1, 2], attemptsAfterPass);
    }

    [Fact(DisplayName = "A path that keeps ending busy is retried at most five times, then left alone")]
    public async Task BusyPath_StopsBeingRetriedAfterTheRetryCap_AndOnlyTheFirstBusyIsLoggedAtInfo()
    {
        var log = new RecordingLog();
        var target = new BusyTarget(busyAttempts: int.MaxValue);
        using var scheduler = new PreloadScheduler(target, new ReviewMetrics(), () => target.Entries, () => 1,
            new PreloadOptions(WorkerCount: 4, FullFolderThresholdBytes: 1),
            new FakeMemoryProbe(true), ImmediateUiScheduler.Instance, log) { BusyNoted = _ => target.BusyWasRecorded() };
        _ = scheduler.PreloadAroundAsync(0);
        await target.WaitForPassesAsync(1).WaitAsync(Bound);
        var expectedBusyAttempts = 1;
        await target.BusyRecorded(expectedBusyAttempts).WaitAsync(Bound);

        const int Passes = 1 + (PreloadBusyBackoff.MaxRetries + 3) * (PreloadBusyBackoff.CooldownPasses + 1);
        for (var pass = 2; pass <= Passes; pass++)
        {
            _ = scheduler.PreloadAroundAsync(0);
            await target.WaitForPassesAsync(pass).WaitAsync(Bound);
            // A retry happens exactly every CooldownPasses + 1 passes until the cap (1 attempt + MaxRetries retries) is used up.
            if ((pass - 1) % (PreloadBusyBackoff.CooldownPasses + 1) == 0 && expectedBusyAttempts < PreloadBusyBackoff.MaxRetries + 1)
                await target.BusyRecorded(++expectedBusyAttempts).WaitAsync(Bound);
        }

        target.ReleaseBlocked();
        Assert.Equal(PreloadBusyBackoff.MaxRetries + 1, target.PreloadCount(BusyIndex));
        Assert.Single(log.Infos, message => message.Contains("decoder busy", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingLog : ILog
    {
        public ConcurrentQueue<string> Infos { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();
        public bool Enabled => true;
        public void Info(string message) => Infos.Enqueue(message);
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) => Errors.Enqueue(message);
    }

    /// <summary>File-free cache: index <see cref="BusyIndex"/> is refused as busy on its first preload only; one index blocks to keep the lifetime open.</summary>
    private sealed class BusyTarget : IPreloadTarget
    {
        private readonly Dictionary<string, int> _indexByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _cached = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, int> _preloads = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly int _busyAttempts;
        private readonly object _markerGate = new();
        private readonly List<(int Threshold, TaskCompletionSource Signal)> _passWaiters = [];
        private readonly List<(int Threshold, TaskCompletionSource Signal)> _busyWaiters = [];
        private int _passes;
        private int _busyThrown;

        /// <param name="busyAttempts">How many preload attempts of <see cref="BusyIndex"/> are refused as busy before it succeeds.</param>
        public BusyTarget(int busyAttempts = 1)
        {
            _busyAttempts = busyAttempts;
            var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Entries = Enumerable.Range(0, ImageCount)
                .Select(i => new CatalogEntry($@"C:\busy-preload\img-{i:D3}.cr2").WithMetadata(1000 + i, written))
                .ToArray();
            for (var i = 0; i < Entries.Length; i++) _indexByPath[Entries[i].Path] = i;
        }

        public CatalogEntry[] Entries { get; }
        public Task BlockedEntered => _entered.Task;
        public int CacheCount => _cached.Count;
        public long CacheBytes => 0;

        public int PreloadCount(int index) => _preloads.GetValueOrDefault(index);
        public bool IsCached(int index) => _cached.ContainsKey(Entries[index].Path);
        public void ReleaseBlocked() => _release.TrySetResult();

        public bool TryGetCachedPreview(string path) => _cached.ContainsKey(path);
        public bool TryGetCachedPreview(ImageCacheKey key) => _cached.ContainsKey(key.Path);
        public ImageCacheKey GetCurrentCacheKey(string path) => ImageCacheKey.Create(Entries[_indexByPath[path]], false, new DecodeBox(100, 100));
        public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry)
        {
            // Only the scheduler loop stats a CatalogEntry, once per examined candidate and order pass: the marker index counts passes.
            if (string.Equals(entry.Path, Entries[MarkerIndex].Path, StringComparison.OrdinalIgnoreCase)) Signal(ref _passes, _passWaiters);
            return ImageCacheKey.Create(entry, false, new DecodeBox(100, 100));
        }

        /// <summary>Completes once the scheduler loop examined the marker index in <paramref name="pass"/> order passes.</summary>
        public Task WaitForPassesAsync(int pass) => Waiter(ref _passes, _passWaiters, pass);

        /// <summary>Completes once <paramref name="count"/> busy refusals were thrown AND recorded by the scheduler's backoff (NoteBusy ran).</summary>
        public Task BusyRecorded(int count) => Waiter(ref _busyThrown, _busyWaiters, count);

        /// <summary>Called by the scheduler's BusyNoted seam, i.e. after the backoff stamped the pass.</summary>
        public void BusyWasRecorded() => Signal(ref _busyThrown, _busyWaiters);

        private void Signal(ref int counter, List<(int Threshold, TaskCompletionSource Signal)> waiters)
        {
            lock (_markerGate)
            {
                var value = ++counter;
                foreach (var waiter in waiters.Where(w => w.Threshold <= value)) waiter.Signal.TrySetResult();
            }
        }

        private Task Waiter(ref int counter, List<(int Threshold, TaskCompletionSource Signal)> waiters, int threshold)
        {
            lock (_markerGate)
            {
                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (counter >= threshold) signal.SetResult(); else waiters.Add((threshold, signal));
                return signal.Task;
            }
        }

        public async Task PreloadAsync(string path, CancellationToken cancellationToken = default)
        {
            var index = _indexByPath[path];
            var attempt = _preloads.AddOrUpdate(index, 1, (_, count) => count + 1);
            if (index == BusyIndex && attempt <= _busyAttempts)
            {
                throw new DecoderBusyException();
            }
            if (index == BlockedIndex)
            {
                _entered.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }
            _cached.TryAdd(GetCurrentCacheKey(path).Path, 0);
        }
    }
}
