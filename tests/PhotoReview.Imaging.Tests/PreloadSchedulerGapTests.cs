using System.IO;
using System.Collections.Concurrent;
using System.Reflection;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>Mutation-gap tests for <see cref="PreloadScheduler"/>: window boundaries, the diagnostic worker override and the pause log.</summary>
[Collection("GlobalState")]
public sealed class PreloadSchedulerGapTests
{
    // ---- InPreloadWindow ----

    [Theory(DisplayName = "InPreloadWindow: the current image is outside; forward reaches Lead+Forward, backward reaches Backward")]
    [InlineData(1, 2, 10, false)]   // the center itself
    [InlineData(1, 2, 11, true)]    // one ahead
    [InlineData(1, 2, 15, true)]    // exactly Lead(2)+Forward(3) ahead
    [InlineData(1, 2, 16, false)]   // one beyond
    [InlineData(1, 2, 9, true)]     // one behind (Backward 1)
    [InlineData(1, 2, 8, false)]    // two behind
    [InlineData(-1, 2, 9, true)]    // travelling down: the lower neighbour is ahead
    [InlineData(-1, 2, 5, true)]    // exactly 5 ahead going down
    [InlineData(-1, 2, 4, false)]
    [InlineData(-1, 2, 11, true)]   // one behind going down
    [InlineData(-1, 2, 12, false)]
    [InlineData(0, 0, 11, true)]    // no direction yet counts as forward
    [InlineData(0, 0, 13, true)]    // Forward(3) ahead
    [InlineData(0, 0, 14, false)]
    [InlineData(0, 0, 9, true)]     // behind with direction 0 is backward
    [InlineData(0, 0, 8, false)]
    public void InPreloadWindow_Boundaries(int direction, int lead, int index, bool expected) =>
        Assert.Equal(expected, PreloadScheduler.InPreloadWindow(index, 10, (direction, lead), new PreloadWindow(Forward: 3, Backward: 1)));

    // ---- diagnostic worker override ----

    private sealed class NullTarget : IPreloadTarget
    {
        public bool TryGetCachedPreview(string path) => false;
        public bool TryGetCachedPreview(ImageCacheKey key) => false;
        public Task PreloadAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ImageCacheKey GetCurrentCacheKey(string path) => ImageCacheKey.Create(path, false, new DecodeBox(100, 100));
        public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry) => GetCurrentCacheKey(entry.Path);
        public int CacheCount => 0;
        public long CacheBytes => 0;
    }

    private static int WorkerCountOf(PreloadScheduler scheduler) =>
        (int)typeof(PreloadScheduler).GetField("_workerCount", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scheduler)!;

    [Fact(DisplayName = "The diagnostic environment variable overrides the configured worker count; without it the option applies")]
    public void DiagWorkerEnvironmentVariable_OverridesTheOption()
    {
        const string name = "PHOTOREVIEW_DIAG_PRELOAD_WORKERS";
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "3");
            using (var overridden = new PreloadScheduler(new NullTarget(), new ReviewMetrics(), () => [], new PreloadOptions(WorkerCount: 8), new FakeMemoryProbe(true)))
                Assert.Equal(3, WorkerCountOf(overridden));

            Environment.SetEnvironmentVariable(name, null);
            using var configured = new PreloadScheduler(new NullTarget(), new ReviewMetrics(), () => [], new PreloadOptions(WorkerCount: 8), new FakeMemoryProbe(true));
            Assert.Equal(8, WorkerCountOf(configured));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    // ---- pause log ----

    private sealed class LowMemoryProbe : IMemoryProbe
    {
        public bool HasHeadroom(double maximumLoad, long reserveBytes) => false;
        public MemorySnapshot? GetSnapshot() => new(77, 123456789);
    }

    private sealed class InfoLog : ILog
    {
        public ConcurrentQueue<string> Infos { get; } = new();
        public bool Enabled => true;
        public void Info(string message) => Infos.Enqueue(message);
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }

    [Fact(DisplayName = "A pause for memory logs the probe's load and available bytes")]
    public async Task PausedForMemory_LogsTheSnapshot()
    {
        var log = new InfoLog();
        var entries = Enumerable.Range(0, 5).Select(i => new CatalogEntry(Path.Combine(Path.GetTempPath(), $"gap-{i}.jpg"))).ToArray();
        using var scheduler = new PreloadScheduler(new NullTarget(), new ReviewMetrics(), () => entries,
            new PreloadOptions(WorkerCount: 2, FullFolderThresholdBytes: -1), new LowMemoryProbe(), log);

        await scheduler.PreloadAroundAsync(0).WaitAsync(TimeSpan.FromSeconds(10));

        var paused = Assert.Single(log.Infos, m => m.StartsWith("Preload paused for memory", StringComparison.Ordinal));
        Assert.Contains("availableBytes=123456789", paused, StringComparison.Ordinal);
        Assert.Contains("loadPercent=77", paused, StringComparison.Ordinal);
    }
}
