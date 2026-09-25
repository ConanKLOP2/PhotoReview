using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Caching;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>Failure paths of the background prune loop (the default gate excludes DiskCacheStoreTests, which are Slow).</summary>
[Trait("Category", "HotPath")]
public sealed class DiskCacheStoreRobustnessTests : IDisposable
{
    private readonly TempRoot _root = new("DiskCacheRobust");

    public void Dispose() => _root.Dispose();

    private sealed class RecordingLog : ILog
    {
        private readonly List<string> _errors = [];
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { lock (_errors) _errors.Add(message + " :: " + ex?.GetType().Name); }
        public string[] Errors { get { lock (_errors) return [.. _errors]; } }
    }

    [Fact(DisplayName = "An unexpected exception in a background prune pass is logged and does not wedge the store: the pass is finished and later passes still run")]
    public async Task UnexpectedExceptionInPrunePass_DoesNotWedgeTheStore()
    {
        var dir = _root.Dir("wedge");
        var log = new RecordingLog();
        // A NUL in the search pattern makes Directory.EnumerateFiles throw ArgumentException, which is not an I/O error.
        var store = new DiskCacheStore(dir, "a\0b", maxBytes: 1, log);

        store.SchedulePrune();
        var finished = await store.WaitForPruneAsync(TimeSpan.FromSeconds(3));

        Assert.True(finished, "the prune machinery stayed 'scheduled' forever after one unexpected exception");
        Assert.Contains(log.Errors, e => e.Contains("Prune failed", StringComparison.Ordinal) && e.Contains("ArgumentException", StringComparison.Ordinal));

        // A second request must start a fresh pass (and fail the same, logged way) instead of being swallowed as "pending".
        store.SchedulePrune();
        Assert.True(await store.WaitForPruneAsync(TimeSpan.FromSeconds(3)));
        Assert.True(log.Errors.Count(e => e.Contains("Prune failed", StringComparison.Ordinal)) >= 2, string.Join(" | ", log.Errors));
    }

    [Fact(DisplayName = "Pruning a directory that vanishes mid-way, or never existed, finishes quietly")]
    public async Task MissingDirectory_FinishesQuietly()
    {
        var log = new RecordingLog();
        var store = new DiskCacheStore(_root.Combine("does-not-exist"), "*.pv4", maxBytes: 1, log);

        store.SchedulePrune();

        Assert.True(await store.WaitForPruneAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(log.Errors);
    }

    [Fact(DisplayName = "Concurrent SchedulePrune callers coalesce: the store goes idle, with the directory within quota, after a burst from many threads")]
    public async Task ConcurrentSchedulePrune_BurstEndsIdleAndWithinQuota()
    {
        var dir = _root.Dir("burst");
        for (var i = 0; i < 20; i++)
        {
            var path = Path.Combine(dir, $"f{i:D2}.pv4");
            File.WriteAllBytes(path, new byte[100]);
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddMinutes(-60 + i));
        }

        var store = new DiskCacheStore(dir, "*.pv4", maxBytes: 500);
        using var start = new Barrier(8);
        var callers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 50; i++) store.SchedulePrune();
        })).ToArray();
        await Task.WhenAll(callers);

        Assert.True(await store.WaitForPruneAsync(TimeSpan.FromSeconds(10)));
        Assert.True(Directory.GetFiles(dir, "*.pv4").Sum(f => new FileInfo(f).Length) <= 500);
    }
}
