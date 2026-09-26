using System;
using System.Diagnostics;
using System.IO;
using PhotoReview.Imaging.Caching;
using Xunit;
using Xunit.Abstractions;

namespace PhotoReview.Imaging.Tests.Quality;

/// <summary>
/// Manual micro-benchmark: wall time and managed allocation of one prune scan over a large cache directory (nothing is
/// deleted: the quota is never exceeded). Run with
/// <c>--filter "FullyQualifiedName~PruneScanBenchmark" --logger "console;verbosity=detailed"</c>.
/// </summary>
[Trait("Category", "Manual")]
public sealed class PruneScanBenchmarkTests(ITestOutputHelper output) : IDisposable
{
    private const int FileCount = 20_000;
    private readonly TempRoot _root = new("prune-scan-bench");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "One prune scan over 20k cache files: time and allocation")]
    public void PruneScanCost()
    {
        var dir = _root.Dir("cache");
        var payload = new byte[64];
        for (var i = 0; i < FileCount; i++) File.WriteAllBytes(Path.Combine(dir, $"{i:D6}.pv4"), payload);

        DiskCacheStore.PruneDirectory(dir, "*.pv4", maxBytes: long.MaxValue, log: null); // warm-up
        var times = new double[9];
        long allocated = 0;
        for (var i = 0; i < times.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var t0 = Stopwatch.GetTimestamp();
            DiskCacheStore.PruneDirectory(dir, "*.pv4", maxBytes: long.MaxValue, log: null);
            times[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(times);
        output.WriteLine($"{FileCount} files: median {times[times.Length / 2]:F1} ms, min {times[0]:F1} ms, allocated {allocated / times.Length / 1024} KiB/scan");
        Assert.Equal(FileCount, Directory.GetFiles(dir).Length);
    }
}
