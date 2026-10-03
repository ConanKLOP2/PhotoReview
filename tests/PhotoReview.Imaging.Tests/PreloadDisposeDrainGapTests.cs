using System.Collections.Concurrent;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Imaging.Tests;

/// <summary>RV-T45: Dispose past DisposeDrainTimeout returns, warns, and only releases the slots/CTS once the uncancellable worker has finished.</summary>
[Trait("Category", "HotPath")]
public sealed class PreloadDisposeDrainGapTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class RecordingLog : ILog
    {
        public ConcurrentQueue<string> Warnings { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Enqueue(message);
        public void Error(string message, Exception? ex = null) => Errors.Enqueue(message + " :: " + ex?.GetType().Name);
    }

    /// <summary>A decode that ignores cancellation: blocks until its gate is released.</summary>
    private sealed class UncancellableTarget : IPreloadTarget
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _finished;

        public Task Started => _started.Task;
        public int Finished => Volatile.Read(ref _finished);
        public void Release() => _gate.TrySetResult();

        public bool TryGetCachedPreview(string path) => false;
        public bool TryGetCachedPreview(ImageCacheKey key) => false;
        public ImageCacheKey GetCurrentCacheKey(string path) => ImageCacheKey.Create(path, false, 100);
        public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry) => GetCurrentCacheKey(entry.Path);
        public int CacheCount => 0;
        public long CacheBytes => 0;

        public async Task PreloadAsync(string path, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            try { await _gate.Task.ConfigureAwait(false); }
            finally { Interlocked.Increment(ref _finished); }
        }
    }

    [Fact(DisplayName = "Dispose past the drain timeout returns with a warning; the worker finishing afterwards hits no disposed slot/CTS")]
    public async Task Dispose_WorkerOutlivesDrainTimeout_WarnsAndWorkerFinishesCleanly()
    {
        using var root = new TempRoot("PreloadDisposeDrain");
        var entries = Enumerable.Range(0, 3).Select(i => new CatalogEntry(root.File($"i{i}.jpg", 1, 2, 3))).ToArray();
        var target = new UncancellableTarget();
        var log = new RecordingLog();
        var scheduler = new PreloadScheduler(target, new ReviewMetrics(), () => entries,
            new PreloadOptions(WorkerCount: 1), new FakeMemoryProbe(true), log)
        {
            DisposeDrainTimeout = TimeSpan.FromMilliseconds(100),
        };
        // The scheduler drains worker faults silently, so watch for the exception itself: a slot disposed under the worker
        // makes its finally (_preloadSlots.Release) throw ObjectDisposedException.
        var disposedThrows = new ConcurrentQueue<string>();
        void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is ObjectDisposedException ode && Environment.StackTrace.Contains("PreloadScheduler", StringComparison.Ordinal))
                disposedThrows.Enqueue(ode.ObjectName);
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            var run = scheduler.PreloadAroundAsync(0);
            await target.Started.WaitAsync(Timeout);

            await Task.Run(scheduler.Dispose).WaitAsync(Timeout); // returns although the decode is still running

            Assert.Equal(0, target.Finished);
            Assert.Contains(log.Warnings, w => w.Contains("did not drain", StringComparison.Ordinal));
            target.Release();
            await run.WaitAsync(Timeout); // the abandoned run unwinds (its finally releases the slot)

            Assert.Equal(1, target.Finished);
            Assert.Empty(log.Errors);
            Assert.False(run.IsFaulted);
            Assert.Empty(disposedThrows);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance; }
    }
}