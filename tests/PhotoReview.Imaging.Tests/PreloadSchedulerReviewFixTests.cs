using System.Collections.Concurrent;
using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>
/// Review 2026-10 (RV-I12..I15): scheduler lifetime races and bookkeeping. Every interleaving is forced through a
/// test seam or a gate in the fake target -- no sleeps, no polling; WaitAsync timeouts are only hang guards.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreloadSchedulerReviewFixTests : IDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview-PreloadReviewFix", Guid.NewGuid().ToString("N"));

    public PreloadSchedulerReviewFixTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact(DisplayName = "RV-I12: a navigation landing between the loop's final version check and its completion is still preloaded")]
    public async Task NavigationDuringSchedulerExit_IsPreloaded()
    {
        var entries = Entries("a", 8);
        var target = new ScriptedTarget();
        using var scheduler = Create(target, () => entries, workers: 1, new PreloadWindow(1, 0));
        Task? afterExit = null;
        var fired = 0;
        scheduler.BeforeSchedulerExitForTests = () =>
        {
            if (Interlocked.Exchange(ref fired, 1) != 0) return;
            scheduler.NotifyNavigation(5);
            afterExit = scheduler.PreloadAroundAsync(5);
        };

        await scheduler.PreloadAroundAsync(0).WaitAsync(Guard);
        Assert.NotNull(afterExit);
        await afterExit!.WaitAsync(Guard);

        Assert.Contains(entries[1].Path, target.Started);
        Assert.Contains(entries[6].Path, target.Started); // the new center's next image
    }

    [Fact(DisplayName = "RV-I13: entries that changed without Cancel restart the running lifetime on the new snapshot")]
    public async Task EntriesChangedWithoutCancel_NextPreloadUsesNewSnapshot()
    {
        var oldEntries = Entries("old", 8);
        var newEntries = Entries("new", 8);
        var entries = oldEntries;
        var version = 1;
        var oldGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new ScriptedTarget
        {
            OnPreload = (path, token) => string.Equals(path, oldEntries[1].Path, StringComparison.OrdinalIgnoreCase)
                ? oldGate.Task.WaitAsync(token)
                : Task.CompletedTask,
        };
        using var scheduler = Create(target, () => Volatile.Read(ref entries), workers: 1, new PreloadWindow(1, 0),
            snapshotVersion: () => Volatile.Read(ref version));

        _ = scheduler.PreloadAroundAsync(0);
        await target.WhenStarted(oldEntries[1].Path).WaitAsync(Guard); // the first run is live, its worker blocked

        Volatile.Write(ref entries, newEntries); // e.g. undo re-inserted a file: no Cancel() on that path
        Interlocked.Increment(ref version);
        var run = scheduler.PreloadAroundAsync(0);
        oldGate.TrySetResult();
        await run.WaitAsync(Guard);

        Assert.Contains(newEntries[1].Path, target.Started);
    }

    [Fact(DisplayName = "RV-I13: an unchanged snapshot version keeps joining the running lifetime (no restart per navigation)")]
    public async Task UnchangedSnapshotVersion_JoinsRunningLifetime()
    {
        var entries = Entries("same", 8);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new ScriptedTarget { OnPreload = (path, token) => gate.Task.WaitAsync(token) };
        using var scheduler = Create(target, () => entries, workers: 1, new PreloadWindow(1, 0), snapshotVersion: () => 7);

        var first = scheduler.PreloadAroundAsync(0);
        await target.WhenStarted(entries[1].Path).WaitAsync(Guard);
        var second = scheduler.PreloadAroundAsync(0);

        Assert.Same(first, second);
        gate.TrySetResult();
        await first.WaitAsync(Guard);
    }

    [Fact(DisplayName = "RV-I14: a foreign OperationCanceledException from one item fails only that item; later items still preload")]
    public async Task ForeignCancellation_SkipsOnlyThatItem()
    {
        var entries = Entries("oce", 8);
        using var foreign = new CancellationTokenSource();
        foreign.Cancel();
        var target = new ScriptedTarget
        {
            OnPreload = (path, token) => string.Equals(path, entries[2].Path, StringComparison.OrdinalIgnoreCase)
                ? Task.FromException(new OperationCanceledException("foreign token", foreign.Token))
                : Task.CompletedTask,
        };
        var log = new RecordingLog();
        using var scheduler = Create(target, () => entries, workers: 1, new PreloadWindow(7, 0), log: log);

        await scheduler.PreloadAroundAsync(0).WaitAsync(Guard);

        for (var i = 3; i < entries.Length; i++) Assert.Contains(entries[i].Path, target.Started);
        Assert.Single(target.Started, p => string.Equals(p, entries[2].Path, StringComparison.OrdinalIgnoreCase)); // failed, not retried
        Assert.DoesNotContain(log.Errors, m => m.Contains("Preload scheduler failed", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "RV-I15: warmed keys of an old target box are dropped when the box changes; items re-preload at the new box")]
    public async Task BoxChange_DropsOldBoxKeys()
    {
        var entries = Entries("box", 8);
        var target = new ScriptedTarget();
        var log = new RecordingLog();
        using var scheduler = Create(target, () => entries, workers: 1, new PreloadWindow(3, 0), log: log);

        await scheduler.PreloadAroundAsync(0).WaitAsync(Guard);
        Assert.True(scheduler.PreloadedKeyCountForTests == 3, $"keys={scheduler.PreloadedKeyCountForTests} started={target.Started.Count} errors={string.Join(" | ", log.Errors)}");
        var oldKey = target.GetCurrentCacheKey(entries[1].Path);

        target.Box = new DecodeBox(200, 150); // window resized: previews are cached at a new box
        await scheduler.PreloadAroundAsync(0).WaitAsync(Guard);

        Assert.Equal(3, scheduler.PreloadedKeyCountForTests); // only the new box's keys, not 6
        Assert.Equal(6, target.Started.Count); // every item re-preloaded at the new box
        Assert.False(scheduler.TryConsumePreloadedKey(oldKey));
        Assert.True(scheduler.TryConsumePreloadedKey(target.GetCurrentCacheKey(entries[1].Path)));
    }

    // Real (tiny) files: ImageCacheKey.Create stats the path.
    private CatalogEntry[] Entries(string prefix, int count) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var path = Path.Combine(_root, $"{prefix}-{i}.jpg");
            File.WriteAllBytes(path, [1, 2, 3, (byte)i]);
            return new CatalogEntry(path);
        }).ToArray();

    private static PreloadScheduler Create(ScriptedTarget target, Func<CatalogEntry[]> entries, int workers, PreloadWindow window,
        Func<int>? snapshotVersion = null, ILog? log = null) =>
        new(target, new ReviewMetrics(), entries, () => 0,
            // Negative threshold: never whole-folder, so exactly the window is preloaded.
            new PreloadOptions(WorkerCount: workers, FullFolderThresholdBytes: -1) { Window = window },
            new FakeMemoryProbe(true), ImmediateUiScheduler.Instance, log, snapshotVersion: snapshotVersion);

    private sealed class RecordingLog : ILog
    {
        public ConcurrentQueue<string> Errors { get; } = new();
        public bool Enabled => false;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) => Errors.Enqueue(message + (ex is null ? "" : " :: " + ex));
    }

    private sealed class ScriptedTarget : IPreloadTarget
    {
        private readonly ConcurrentDictionary<ImageCacheKey, byte> _cached = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new(StringComparer.OrdinalIgnoreCase);

        public ConcurrentQueue<string> Started { get; } = new();
        public Func<string, CancellationToken, Task>? OnPreload { get; init; }
        private DecodeBox _box = new(100, 100);
        public DecodeBox Box { get => _box; set => _box = value; }
        public int CacheCount => _cached.Count;
        public long CacheBytes => 0;

        public Task WhenStarted(string path) => StartedSignal(path).Task;

        private TaskCompletionSource StartedSignal(string path) =>
            _started.GetOrAdd(path, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public bool TryGetCachedPreview(string path) => _cached.ContainsKey(GetCurrentCacheKey(path));
        public bool TryGetCachedPreview(ImageCacheKey key) => _cached.ContainsKey(key);
        public ImageCacheKey GetCurrentCacheKey(string path) => ImageCacheKey.Create(path, false, Box);
        public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry) => GetCurrentCacheKey(entry.Path);

        public async Task PreloadAsync(string path, CancellationToken cancellationToken = default)
        {
            Started.Enqueue(path);
            StartedSignal(path).TrySetResult();
            // A real decode never completes synchronously: run the rest off the scheduler's thread.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            if (OnPreload is not null) await OnPreload(path, cancellationToken).ConfigureAwait(false);
            _cached.TryAdd(GetCurrentCacheKey(path), 0);
        }
    }
}
