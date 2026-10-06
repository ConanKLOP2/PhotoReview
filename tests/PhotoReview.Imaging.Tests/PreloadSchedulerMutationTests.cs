using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;

namespace PhotoReview.Imaging.Tests;

/// <summary>
/// Mutation-testing follow-ups for <see cref="PreloadScheduler"/> (docs/MUTATION-TESTING.md): worker limits while the viewer
/// decodes, constructor wiring, Cancel/IsIdle/NotifyNavigation, lifetime bookkeeping, the caller-thread window, the
/// whole-folder fill limit, the headroom cache and the failure paths of one candidate. Every interleaving is forced by a gate
/// in the fake target or a seam; the WaitAsync timeouts are only hang guards.
/// </summary>
[Trait("Category", "HotPath")]
public sealed partial class PreloadSchedulerMutationTests : IDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private readonly TempRoot _root = new("preload-mut");
    private readonly List<IDisposable> _disposables = [];
    private readonly List<TaskCompletionSource> _gates = [];

    public void Dispose()
    {
        foreach (var gate in _gates) gate.TrySetResult();
        foreach (var disposable in _disposables) disposable.Dispose();
        _root.Dispose();
    }

    // ---- fakes ----------------------------------------------------------------------------------------------------

    private sealed class Probe(Func<bool> hasHeadroom) : IMemoryProbe
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool HasHeadroom(double maximumLoad, long reserveBytes)
        {
            Interlocked.Increment(ref _calls);
            return hasHeadroom();
        }
        public MemorySnapshot? GetSnapshot() => new(50, 16L * 1024 * 1024 * 1024);
    }

    private sealed class RecordingLog : ILog
    {
        public ConcurrentQueue<string> Errors { get; } = new();
        public ConcurrentQueue<string> Warnings { get; } = new();
        public bool Enabled => false;
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Enqueue(message);
        public void Error(string message, Exception? ex = null) => Errors.Enqueue(message + (ex is null ? "" : " :: " + ex.GetType().Name));
    }

    private sealed class FakeClock
    {
        private long _ticks = 1_000_000;
        public long Now() => _ticks;
        public void AdvanceMs(double milliseconds) => _ticks += (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0);
    }

    private sealed class Target : IPreloadTarget
    {
        private readonly ConcurrentDictionary<ImageCacheKey, byte> _cached = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new(StringComparer.OrdinalIgnoreCase);

        public ConcurrentQueue<string> Started { get; } = new();
        public ConcurrentDictionary<string, int> PreloadThread { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, int> KeyThread { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, int> KeyCalls { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Func<string, CancellationToken, Task>? OnPreload { get; set; }
        public Func<string, Exception?>? KeyFailure { get; set; }
        public int ActiveViewer { get; set; }
        public long Bytes { get; set; }
        public DecodeBox Box { get; set; } = new(100, 100);

        public int ActiveViewerDecodes => ActiveViewer;
        public int CacheCount => _cached.Count;
        public long CacheBytes => Bytes;

        public Task WhenStarted(string path) => StartedSignal(path).Task;
        public int StartedCount(string path) => Started.Count(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        public void MarkCached(string path) => _cached.TryAdd(GetCurrentCacheKey(path), 0);

        private TaskCompletionSource StartedSignal(string path) =>
            _started.GetOrAdd(path, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public bool TryGetCachedPreview(string path) => _cached.ContainsKey(GetCurrentCacheKey(path));
        public bool TryGetCachedPreview(ImageCacheKey key) => _cached.ContainsKey(key);
        public ImageCacheKey GetCurrentCacheKey(string path) => ImageCacheKey.Create(path, false, Box);

        public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry)
        {
            KeyThread[entry.Path] = Environment.CurrentManagedThreadId;
            KeyCalls.AddOrUpdate(entry.Path, 1, (_, n) => n + 1);
            if (KeyFailure?.Invoke(entry.Path) is { } failure) throw failure;
            return GetCurrentCacheKey(entry.Path);
        }

        public async Task PreloadAsync(string path, CancellationToken cancellationToken = default)
        {
            PreloadThread[path] = Environment.CurrentManagedThreadId;
            Started.Enqueue(path);
            StartedSignal(path).TrySetResult();
            // A real decode never completes synchronously: run the rest off the scheduler's thread.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            if (OnPreload is not null) await OnPreload(path, cancellationToken).ConfigureAwait(false);
            _cached.TryAdd(GetCurrentCacheKey(path), 0);
        }
    }

    private CatalogEntry[] Entries(int count, string prefix = "img") =>
        Enumerable.Range(0, count).Select(i => new CatalogEntry(_root.File($"{prefix}-{i}.jpg", 1, 2, 3, (byte)i))).ToArray();

    private TaskCompletionSource NewGate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates.Add(gate);
        return gate;
    }

    private PreloadScheduler Create(Target target, CatalogEntry[] entries, int workers, PreloadWindow window,
        bool wholeFolder = false, ReviewMetrics? metrics = null, NavigationPace? pace = null, ILog? log = null,
        IMemoryProbe? probe = null, Func<int>? snapshotVersion = null, TimeProvider? time = null)
    {
        var scheduler = new PreloadScheduler(target, metrics ?? new ReviewMetrics(), () => entries,
            new PreloadOptions(WorkerCount: workers, FullFolderThresholdBytes: wholeFolder ? 1_000_000_000L : -1) { Window = window },
            probe ?? new FakeMemoryProbe(true), log, pace: pace, snapshotVersion: snapshotVersion, timeProvider: time);
        _disposables.Add(scheduler);
        return scheduler;
    }

    // ---- viewer-busy worker limits --------------------------------------------------------------------------------

    private static int ViewerBusyLimit(int workers) => Math.Min(workers, Math.Max(2, Environment.ProcessorCount / 3));

    private async Task<int> StartsOnFirstPass(int workers, int viewerDecodes, long decodeEwmaMs)
    {
        var entries = Entries(40);
        var gate = NewGate();
        var target = new Target { ActiveViewer = viewerDecodes, OnPreload = (_, token) => gate.Task.WaitAsync(token) };
        var metrics = new ReviewMetrics();
        if (decodeEwmaMs > 0) metrics.RecordSourceRead(1, decodeEwmaMs);
        var scheduler = Create(target, entries, workers, new PreloadWindow(30, 0), metrics: metrics);

        var run = scheduler.PreloadAroundAsync(0);
        var started = target.Started.Count; // the loop's synchronous prefix: every start of the first pass has happened
        gate.TrySetResult();
        await run.WaitAsync(Guard);
        return started;
    }

    [Fact(DisplayName = "While a viewer decode runs, preload starts at most max(2, cores/3) decodes at once")]
    public async Task ViewerDecodeActive_LimitsPreloadStarts()
    {
        Assert.Equal(ViewerBusyLimit(16), await StartsOnFirstPass(workers: 16, viewerDecodes: 1, decodeEwmaMs: 0));
    }

    [Fact(DisplayName = "While a viewer decode runs on a slow link (decode EWMA above 200 ms), preload starts exactly one decode")]
    public async Task ViewerDecodeActive_SlowLink_StartsOne()
    {
        Assert.Equal(1, await StartsOnFirstPass(workers: 16, viewerDecodes: 1, decodeEwmaMs: 201));
    }

    [Fact(DisplayName = "A decode EWMA of exactly 200 ms is not a slow link: the normal viewer-busy limit applies")]
    public async Task ViewerDecodeActive_LinkAtThreshold_IsNotSlow()
    {
        Assert.Equal(ViewerBusyLimit(16), await StartsOnFirstPass(workers: 16, viewerDecodes: 1, decodeEwmaMs: 200));
    }

    [Fact(DisplayName = "With no viewer decode running, preload starts every worker (also on a slow link)")]
    public async Task NoViewerDecode_StartsEveryWorker()
    {
        Assert.Equal(16, await StartsOnFirstPass(workers: 16, viewerDecodes: 0, decodeEwmaMs: 0));
        Assert.Equal(16, await StartsOnFirstPass(workers: 16, viewerDecodes: 0, decodeEwmaMs: 500));
    }

    // ---- constructor wiring ---------------------------------------------------------------------------------------

    [Fact(DisplayName = "An injected NavigationPace supplies the travel direction of the preload order")]
    public async Task InjectedPace_DrivesTheOrderDirection()
    {
        var entries = Entries(20);
        var gate = NewGate();
        var target = new Target { OnPreload = (_, token) => gate.Task.WaitAsync(token) };
        var clock = new FakeClock();
        var pace = new NavigationPace(clock.Now);
        pace.Record(10);
        pace.Record(9); // one step towards lower indices
        var scheduler = Create(target, entries, workers: 1, new PreloadWindow(2, 2), pace: pace);

        var run = scheduler.PreloadAroundAsync(9);

        Assert.Equal(entries[8].Path, Assert.Single(target.Started));
        gate.TrySetResult();
        await run.WaitAsync(Guard);
        Assert.Equal([entries[8].Path, entries[7].Path, entries[10].Path, entries[11].Path], target.Started.ToArray());
    }

    [Fact(DisplayName = "The convenience constructor honours its worker override and window")]
    public async Task ConvenienceCtor_HonoursWorkerOverrideAndWindow()
    {
        var entries = Entries(20);
        var gate = NewGate();
        var target = new Target { OnPreload = (_, token) => gate.Task.WaitAsync(token) };
        var scheduler = new PreloadScheduler(target, new ReviewMetrics(), () => entries, fullFolderRamThresholdBytes: -1,
            memoryLoadLimit: 0.9, memoryProbe: new DelegateMemoryProbe(_ => true), workerCountOverride: 3, window: new PreloadWindow(5, 0));
        _disposables.Add(scheduler);

        var run = scheduler.PreloadAroundAsync(0);
        Assert.Equal(3, target.Started.Count);
        gate.TrySetResult();
        await run.WaitAsync(Guard);

        Assert.Equal(Enumerable.Range(1, 5).Select(i => entries[i].Path), target.Started); // exactly the 5-image window, in order
    }

    [Fact(DisplayName = "The convenience constructor takes its headroom answer from the memory probe (including a delegate probe), else it refuses")]
    public async Task ConvenienceCtor_MemoryProbeOrDelegate()
    {
        var entries = Entries(6);

        var probeTarget = new Target();
        var denied = new PreloadScheduler(probeTarget, new ReviewMetrics(), () => entries, -1, 0.9,
            memoryProbe: new FakeMemoryProbe(false), workerCountOverride: 2, window: new PreloadWindow(3, 0));
        _disposables.Add(denied);
        await denied.PreloadAroundAsync(0).WaitAsync(Guard);
        Assert.Empty(probeTarget.Started); // no headroom: nothing preloaded

        var delegateTarget = new Target();
        var byDelegate = new PreloadScheduler(delegateTarget, new ReviewMetrics(), () => entries, -1, 0.9, memoryProbe: new DelegateMemoryProbe(_ => true),
            workerCountOverride: 2, window: new PreloadWindow(3, 0));
        _disposables.Add(byDelegate);
        await byDelegate.PreloadAroundAsync(0).WaitAsync(Guard);
        Assert.Equal(3, delegateTarget.Started.Count);

        var ex = Assert.Throws<ArgumentNullException>(() => new PreloadScheduler(new Target(), new ReviewMetrics(), () => entries, -1, 0.9));
        Assert.Equal("memoryProbe", ex.ParamName);
    }

    // ---- Cancel / IsIdle / NotifyNavigation -----------------------------------------------------------------------

    [Fact(DisplayName = "Cancel cancels the running lifetime: the decode sees it and the run completes")]
    public async Task Cancel_CancelsRunningWork()
    {
        var entries = Entries(6);
        var gate = NewGate();
        var target = new Target { OnPreload = (_, token) => gate.Task.WaitAsync(token) };
        var scheduler = Create(target, entries, workers: 1, new PreloadWindow(3, 0));
        var run = scheduler.PreloadAroundAsync(0);
        await target.WhenStarted(entries[1].Path).WaitAsync(Guard);

        scheduler.Cancel();

        await run.WaitAsync(Guard); // would hang: the gated decode only ends through the cancelled token
        Assert.Equal([entries[1].Path], target.Started.ToArray());
        scheduler.Dispose();
        scheduler.Cancel(); // after Dispose: a quiet no-op
    }

    [Fact(DisplayName = "IsIdle is true before the first run, false while a decode is in flight, true again once it finished")]
    public async Task IsIdle_TracksTheRunningLifetime()
    {
        var entries = Entries(4);
        var gate = NewGate();
        var target = new Target { OnPreload = (_, token) => gate.Task.WaitAsync(token) };
        var scheduler = Create(target, entries, workers: 1, new PreloadWindow(2, 0));
        Assert.True(scheduler.IsIdle);

        var run = scheduler.PreloadAroundAsync(0);
        await target.WhenStarted(entries[1].Path).WaitAsync(Guard);
        Assert.False(scheduler.IsIdle);

        gate.TrySetResult();
        await run.WaitAsync(Guard);
        Assert.True(scheduler.IsIdle);
    }

    [Fact(DisplayName = "RemovePreloadedKeysForPath forgets only that path's warmed key")]
    public async Task RemovePreloadedKeysForPath_RemovesOnlyThatPath()
    {
        var entries = Entries(5);
        var target = new Target();
        var scheduler = Create(target, entries, workers: 1, new PreloadWindow(3, 0));
        await scheduler.PreloadAroundAsync(0).WaitAsync(Guard);
        var key1 = target.GetCurrentCacheKey(entries[1].Path);
        var key2 = target.GetCurrentCacheKey(entries[2].Path);
        Assert.Equal(3, scheduler.PreloadedKeyCountForTests);

        scheduler.RemovePreloadedKeysForPath(key1.Path);

        Assert.False(scheduler.TryConsumePreloadedKey(key1));
        Assert.True(scheduler.TryConsumePreloadedKey(key2));
        Assert.Equal(1, scheduler.PreloadedKeyCountForTests);
    }

    [Fact(DisplayName = "NotifyNavigation records the step in the pace only when preload is enabled (not with zero workers)")]
    public void NotifyNavigation_RecordsPaceOnlyWithWorkers()
    {
        var clock = new FakeClock();
        var enabledPace = new NavigationPace(clock.Now);
        var disabledPace = new NavigationPace(clock.Now);
        var enabled = Create(new Target(), Entries(8), workers: 2, new PreloadWindow(1, 0), pace: enabledPace);
        var disabled = Create(new Target(), Entries(8, "off"), workers: 0, new PreloadWindow(1, 0), pace: disabledPace);

        enabled.NotifyNavigation(5);
        enabled.NotifyNavigation(4);
        disabled.NotifyNavigation(5);
        disabled.NotifyNavigation(4);

        Assert.Equal(-1, enabledPace.Direction);
        Assert.Equal(1, disabledPace.Direction); // never recorded
    }

    [Fact(DisplayName = "NotifyNavigation moves the preload centre of a running scheduler: the new neighbourhood is preloaded next")]
    public async Task NotifyNavigation_ReprioritizesARunningScheduler()
    {
        var entries = Entries(20);
        var gate = NewGate();
        var target = new Target { OnPreload = (path, token) => string.Equals(path, entries[1].Path, StringComparison.OrdinalIgnoreCase) ? gate.Task.WaitAsync(token) : Task.CompletedTask };
        var scheduler = Create(target, entries, workers: 1, new PreloadWindow(2, 0));
        var run = scheduler.PreloadAroundAsync(0);
        await target.WhenStarted(entries[1].Path).WaitAsync(Guard);

        scheduler.NotifyNavigation(10);
        gate.TrySetResult();
        await run.WaitAsync(Guard);

        Assert.Contains(entries[11].Path, target.Started);
        Assert.Contains(entries[12].Path, target.Started);
        Assert.DoesNotContain(entries[2].Path, target.Started); // the old centre's next image was dropped in favour of the new one
    }

    [Fact(DisplayName = "NotifyNavigation outside a burst does not start the scheduler; inside a burst it does")]
    public async Task NotifyNavigation_StartsTheSchedulerOnlyDuringABurst()
    {
        var entries = Entries(20);
        var quiet = new Target();
        var quietScheduler = Create(quiet, entries, workers: 2, new PreloadWindow(3, 0));
        quietScheduler.NotifyNavigation(5);
        Assert.True(quietScheduler.IsIdle);
        Assert.Empty(quiet.Started);

        var clock = new FakeClock();
        var pace = new NavigationPace(clock.Now);
        var metrics = new ReviewMetrics();
        metrics.RecordSourceRead(1, 200); // a 200 ms decode ...
        pace.Record(0);
        clock.AdvanceMs(50);
        pace.Record(1);
        clock.AdvanceMs(50);
        pace.Record(2); // ... against 50 ms key intervals: a burst
        var burst = new Target();
        var burstScheduler = Create(burst, entries, workers: 2, new PreloadWindow(3, 0), metrics: metrics, pace: pace);
        clock.AdvanceMs(50);

        burstScheduler.NotifyNavigation(3);

        Assert.NotEmpty(burst.Started);
        await burstScheduler.PreloadAroundAsync(3).WaitAsync(Guard);
    }

    [Fact(DisplayName = "GetViewerDecodeDelay is zero with preload disabled, and the pace's start delay during a burst")]
    public void GetViewerDecodeDelay_FollowsThePaceOnlyWhenEnabled()
    {
        var clock = new FakeClock();
        var pace = new NavigationPace(clock.Now);
        var metrics = new ReviewMetrics();
        metrics.RecordSourceRead(1, 200);
        pace.Record(0);
        clock.AdvanceMs(50);
        pace.Record(1);
        clock.AdvanceMs(50);
        pace.Record(2);
        var enabled = Create(new Target(), Entries(4), workers: 2, new PreloadWindow(1, 0), metrics: metrics, pace: pace);
        var disabled = Create(new Target(), Entries(4, "off"), workers: 0, new PreloadWindow(1, 0), metrics: metrics, pace: pace);

        Assert.Equal(TimeSpan.FromMilliseconds(75), enabled.GetViewerDecodeDelay());
        Assert.Equal(TimeSpan.Zero, disabled.GetViewerDecodeDelay());
    }

    // ---- lifetimes ------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "Finished lifetimes are dropped, including the oldest one: after three cancel/restart cycles only two remain")]
    public async Task Lifetimes_AreBoundedIncludingTheFirst()
    {
        var entries = Entries(4);
        var scheduler = Create(new Target(), entries, workers: 1, new PreloadWindow(1, 0));

        await scheduler.PreloadAroundAsync(0).WaitAsync(Guard);
        scheduler.Cancel();
        await scheduler.PreloadAroundAsync(0).WaitAsync(Guard);
        scheduler.Cancel();
        await scheduler.PreloadAroundAsync(0).WaitAsync(Guard);

        Assert.Equal(2, scheduler.TrackedLifetimeCounts.Lifetimes);
    }

    // ---- scheduler pass decisions ---------------------------------------------------------------------------------

    [Fact(DisplayName = "An unexpected failure marks the run as exiting: the next kick starts a new run instead of joining the dying one")]
    public async Task SchedulerFailure_MarksRunExiting()
    {
        var entries = Entries(10);
        var gate = NewGate();
        var target = new Target { OnPreload = (_, token) => gate.Task.WaitAsync(token) };
        var log = new RecordingLog();
        // Headroom is fine until the first decode started; the next probe then throws: an unexpected failure inside the loop.
        var probe = new Probe(() => target.Started.IsEmpty ? true : throw new InvalidOperationException("probe failed"));
        var scheduler = Create(target, entries, workers: 2, new PreloadWindow(5, 0), log: log, probe: probe);

        var first = scheduler.PreloadAroundAsync(0); // fails after starting one decode, then drains it (gated)
        Assert.False(first.IsCompleted);
        var second = scheduler.PreloadAroundAsync(5);

        Assert.NotSame(first, second);
        Assert.Contains(log.Errors, e => e.StartsWith("Preload scheduler failed", StringComparison.Ordinal));
        gate.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(Guard);
    }

    [Fact(DisplayName = "The order is rebuilt only when the navigation, direction/lead or calibration changed, not on every pass")]
    public async Task Order_IsRebuiltOnlyOnChange()
    {
        var entries = Entries(10);
        var target = new Target();
        var scheduler = Create(target, entries, workers: 1, new PreloadWindow(4, 0));

        await scheduler.PreloadAroundAsync(0).WaitAsync(Guard);

        Assert.Equal(4, target.Started.Count);
        // The centre entry is only ever keyed when the order (and its decode box) is rebuilt.
        Assert.Equal(1, target.KeyCalls[entries[0].Path]);
    }

    [Fact(DisplayName = "A navigation that lands while the run pauses for memory does not keep a paused run alive")]
    public async Task PausedRun_WithPendingNavigation_ExitsInsteadOfRepeating()
    {
        var entries = Entries(10);
        var target = new Target();
        PreloadScheduler? scheduler = null;
        var fired = 0;
        var probe = new Probe(() =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0) scheduler!.NotifyNavigation(7); // lands after this pass read its version
            return false;
        });
        scheduler = Create(target, entries, workers: 2, new PreloadWindow(3, 0), probe: probe);

        await scheduler.PreloadAroundAsync(0).WaitAsync(Guard);

        Assert.False(target.KeyCalls.ContainsKey(entries[7].Path)); // no second pass rebuilt the order around the new centre
        Assert.Empty(target.Started);
    }

    [Fact(DisplayName = "Every queued decode re-checks memory headroom: a probe that turns negative after the first start stops the burst at one")]
    public async Task Headroom_IsRecheckedAfterEveryQueuedDecode()
    {
        var entries = Entries(10);
        var gate = NewGate();
        var target = new Target { OnPreload = (_, token) => gate.Task.WaitAsync(token) };
        var probe = new Probe(() => target.Started.IsEmpty);
        var scheduler = Create(target, entries, workers: 4, new PreloadWindow(8, 0), probe: probe);

        var run = scheduler.PreloadAroundAsync(0);
        Assert.Single(target.Started);
        gate.TrySetResult();
        await run.WaitAsync(Guard);

        Assert.Single(target.Started); // paused for memory after the first, never resumed in this run
    }

    [Fact(DisplayName = "The headroom answer is cached while nothing is queued: a pass over already-cached images probes only a few times")]
    public async Task Headroom_IsCachedWhileNothingIsQueued()
    {
        var entries = Entries(14);
        var target = new Target();
        foreach (var entry in entries) target.MarkCached(entry.Path);
        var probe = new Probe(() => true);
        var scheduler = Create(target, entries, workers: 2, new PreloadWindow(13, 0), probe: probe);

        await scheduler.PreloadAroundAsync(0).WaitAsync(Guard);

        Assert.Empty(target.Started);
        Assert.InRange(probe.Calls, 1, 5); // one real probe (a 50 ms recheck could add a couple), not one per image
    }

    // ---- caller-thread window -------------------------------------------------------------------------------------

    [Theory(DisplayName = "Only the preload window is examined on the caller's thread; everything beyond it continues on the thread pool")]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task OnlyTheWindowRunsOnTheCallerThread(int direction)
    {
        var entries = Entries(30);
        var target = new Target();
        var clock = new FakeClock();
        var pace = new NavigationPace(clock.Now);
        if (direction < 0) { pace.Record(11); pace.Record(10); }
        var scheduler = Create(target, entries, workers: 16, new PreloadWindow(3, 2), wholeFolder: true, pace: pace);
        var caller = Environment.CurrentManagedThreadId;

        await scheduler.PreloadAroundAsync(10).WaitAsync(Guard);

        var onCaller = new HashSet<int> { 10 + direction, 10 + 2 * direction, 10 + 3 * direction, 10 - direction, 10 - 2 * direction };
        for (var i = 0; i < entries.Length; i++)
        {
            if (i == 10) continue;
            Assert.True(target.KeyThread.ContainsKey(entries[i].Path), $"index {i} was never examined (whole folder expected)");
            Assert.Equal(onCaller.Contains(i), target.KeyThread[entries[i].Path] == caller);
        }
        for (var i = 0; i < entries.Length; i++)
            if (i != 10 && onCaller.Contains(i)) Assert.Equal(caller, target.PreloadThread[entries[i].Path]);
    }

    [Fact(DisplayName = "The first window decodes (up to the worker count) start on the caller's thread, before the first yield")]
    public async Task FirstWorkersStartOnTheCallerThread()
    {
        var entries = Entries(12);
        var target = new Target();
        var scheduler = Create(target, entries, workers: 3, new PreloadWindow(8, 0));
        var caller = Environment.CurrentManagedThreadId;

        await scheduler.PreloadAroundAsync(0).WaitAsync(Guard);

        Assert.Equal(caller, target.PreloadThread[entries[1].Path]);
        Assert.Equal(caller, target.PreloadThread[entries[2].Path]);
        Assert.Equal(caller, target.PreloadThread[entries[3].Path]);
        Assert.NotEqual(caller, target.PreloadThread[entries[8].Path]); // started after the batch yield
    }

    // ---- whole-folder fill limit ----------------------------------------------------------------------------------

    [Theory(DisplayName = "A whole-folder pass stops adding images beyond the window once the cache holds 90% of the budget (inclusive)")]
    [InlineData(900_000_000L, false)]
    [InlineData(899_999_999L, true)]
    public async Task WholeFolder_StopsAtTheFillLimit(long cacheBytes, bool reachesTheRestOfTheFolder)
    {
        Assert.Equal(900_000_000.0, 1_000_000_000L * PreloadScheduler.WholeFolderCacheFillLimit);
        var entries = Entries(20);
        var target = new Target { Bytes = cacheBytes };
        var scheduler = Create(target, entries, workers: 4, new PreloadWindow(2, 1), wholeFolder: true);

        await scheduler.PreloadAroundAsync(5).WaitAsync(Guard);

        var started = target.Started.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(entries[6].Path, started);
        Assert.Contains(entries[7].Path, started);
        Assert.Contains(entries[4].Path, started);
        Assert.Equal(reachesTheRestOfTheFolder, started.Contains(entries[15].Path));
        Assert.Equal(reachesTheRestOfTheFolder ? 19 : 3, started.Count);
    }

    // ---- one candidate failing ------------------------------------------------------------------------------------

    [Theory(DisplayName = "A candidate that cannot be stat'ed (deleted since the scan) is skipped with a warning; the rest still preload")]
    [InlineData("io")]
    [InlineData("access")]
    public async Task CandidateKeyFailure_SkipsOnlyThatCandidate(string kind)
    {
        var entries = Entries(8);
        var target = new Target
        {
            KeyFailure = path => path == entries[2].Path
                ? (kind == "io" ? new IOException("gone") : new UnauthorizedAccessException("denied"))
                : null,
        };
        var log = new RecordingLog();
        var scheduler = Create(target, entries, workers: 2, new PreloadWindow(6, 0), log: log);

        await scheduler.PreloadAroundAsync(0).WaitAsync(Guard);

        Assert.DoesNotContain(entries[2].Path, target.Started);
        foreach (var i in new[] { 1, 3, 4, 5, 6 }) Assert.Contains(entries[i].Path, target.Started);
        Assert.Contains(log.Warnings, w => w.Contains(entries[2].Path, StringComparison.Ordinal));
        Assert.Empty(log.Errors);
    }

    [Fact(DisplayName = "A centre entry that cannot be stat'ed only costs the decode-box lookup: the neighbours still preload")]
    public async Task CentreKeyFailure_FallsBackToUnboundedBox()
    {
        var entries = Entries(6);
        var target = new Target { KeyFailure = path => path == entries[0].Path ? new IOException("gone") : null };
        var log = new RecordingLog();
        var scheduler = Create(target, entries, workers: 2, new PreloadWindow(3, 0), log: log);

        await scheduler.PreloadAroundAsync(0).WaitAsync(Guard);

        Assert.Equal(3, target.Started.Count);
        Assert.Empty(log.Errors);
    }

    [Theory(DisplayName = "A centre outside the catalog (negative or past the end) preloads nothing and is not an error")]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(100)]
    public async Task CentreOutOfRange_IsANoOpNotAnError(int center)
    {
        var entries = Entries(6);
        var target = new Target();
        var log = new RecordingLog();
        var scheduler = Create(target, entries, workers: 2, new PreloadWindow(3, 0), log: log);

        await scheduler.PreloadAroundAsync(center).WaitAsync(Guard);

        Assert.Empty(target.Started);
        Assert.Empty(log.Errors);
    }

    [Fact(DisplayName = "A decode that fails with an unexpected exception type skips only that image; the scheduler keeps going")]
    public async Task UnexpectedDecodeException_SkipsOnlyThatImage()
    {
        var entries = Entries(8);
        var target = new Target
        {
            OnPreload = (path, _) => path == entries[2].Path ? Task.FromException(new NotSupportedException("odd format")) : Task.CompletedTask,
        };
        var log = new RecordingLog();
        var scheduler = Create(target, entries, workers: 1, new PreloadWindow(6, 0), log: log);

        await scheduler.PreloadAroundAsync(0).WaitAsync(Guard);

        foreach (var i in new[] { 3, 4, 5, 6 }) Assert.Contains(entries[i].Path, target.Started);
        Assert.Contains(log.Errors, e => e.StartsWith("Preload failed", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Errors, e => e.StartsWith("Preload scheduler failed", StringComparison.Ordinal));
    }

    // ---- items waiting for a worker slot --------------------------------------------------------------------------

    [Fact(DisplayName = "A queued item whose image became the current one while it waited for a slot is dropped, not decoded")]
    public async Task QueuedItem_ThatBecameTheCurrentImage_IsDropped()
    {
        var entries = Entries(8);
        var gate = NewGate();
        var target = new Target { OnPreload = (path, token) => path == entries[3].Path ? gate.Task : Task.CompletedTask };
        var scheduler = Create(target, entries, workers: 1, new PreloadWindow(2, 0));

        var first = scheduler.PreloadAroundAsync(2); // image 3 starts and holds the only slot (it ignores cancellation)
        await target.WhenStarted(entries[3].Path).WaitAsync(Guard);
        scheduler.Cancel();
        var second = scheduler.PreloadAroundAsync(0); // new lifetime: image 1 waits for that slot
        scheduler.NotifyNavigation(1); // the user lands on image 1 while it waits
        Assert.False(second.IsCompleted);
        gate.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(Guard);

        Assert.Equal(0, target.StartedCount(entries[1].Path));
    }

    [Fact(DisplayName = "A queued item whose preview got cached while it waited for a slot is not decoded a second time")]
    public async Task QueuedItem_AlreadyCachedMeanwhile_IsNotDecodedAgain()
    {
        var entries = Entries(8);
        var gate = NewGate();
        var target = new Target { OnPreload = (path, token) => path == entries[1].Path ? gate.Task : Task.CompletedTask };
        var scheduler = Create(target, entries, workers: 1, new PreloadWindow(2, 0));

        var first = scheduler.PreloadAroundAsync(0); // image 1 starts and holds the only slot
        await target.WhenStarted(entries[1].Path).WaitAsync(Guard);
        scheduler.Cancel();
        var second = scheduler.PreloadAroundAsync(0); // new lifetime: image 1 queued again, waiting for the slot
        Assert.False(second.IsCompleted);
        gate.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(Guard);

        Assert.Equal(1, target.StartedCount(entries[1].Path));
    }
}
