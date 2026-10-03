using System.Collections.Concurrent;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Imaging.Tests;

/// <summary>Dispose that outlasts the drain timeout hands the slot/CTS release to a continuation that finishes only after the last worker.</summary>
[Trait("Category", "HotPath")]
public sealed class PreloadLateDisposalTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class NullLog : ILog
    {
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }

    /// <summary>A decode that ignores cancellation: blocks until its gate is released.</summary>
    private sealed class BlockedTarget : IPreloadTarget
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
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
            await _gate.Task.ConfigureAwait(false);
        }
    }

    private static (PreloadScheduler Scheduler, TempRoot Root) Create(BlockedTarget target)
    {
        var root = new TempRoot("LateDisposal");
        var entries = Enumerable.Range(0, 3).Select(i => new CatalogEntry(root.File($"i{i}.jpg", 1, 2, 3))).ToArray();
        var scheduler = new PreloadScheduler(target, new ReviewMetrics(), () => entries,
            new PreloadOptions(WorkerCount: 1), new FakeMemoryProbe(true), new NullLog())
        {
            DisposeDrainTimeout = TimeSpan.FromMilliseconds(100),
        };
        return (scheduler, root);
    }

    [Fact(DisplayName = "Dispose that drains in time leaves LateDisposalForTests null")]
    public async Task Dispose_DrainsInTime_NoLateDisposal()
    {
        var target = new BlockedTarget();
        var (scheduler, root) = Create(target);
        using var _ = root;

        var run = scheduler.PreloadAroundAsync(0);
        await target.Started.WaitAsync(Timeout);
        target.Release();
        await run.WaitAsync(Timeout);

        await Task.Run(scheduler.Dispose).WaitAsync(Timeout);

        Assert.Null(scheduler.LateDisposalForTests);
    }

    [Fact(DisplayName = "Dispose past the drain timeout exposes a late disposal that completes only after the blocked decode finishes")]
    public async Task Dispose_WorkerOutlivesDrainTimeout_LateDisposalCompletesAfterRelease()
    {
        var target = new BlockedTarget();
        var (scheduler, root) = Create(target);
        using var _ = root;

        var run = scheduler.PreloadAroundAsync(0);
        await target.Started.WaitAsync(Timeout);

        await Task.Run(scheduler.Dispose).WaitAsync(Timeout);

        var late = scheduler.LateDisposalForTests;
        Assert.NotNull(late);
        Assert.False(late.IsCompleted, "slots/CTS were released while the decode was still running");

        target.Release();
        await run.WaitAsync(Timeout);
        await late.WaitAsync(Timeout);

        Assert.True(late.IsCompletedSuccessfully);
        Assert.False(late.IsFaulted);
    }
}