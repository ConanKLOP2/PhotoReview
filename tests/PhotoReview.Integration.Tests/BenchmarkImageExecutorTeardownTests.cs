using System.Diagnostics;
using System.IO;
using PhotoReview.Benchmarking;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// perf(bench-window): BenchmarkImageExecutor.DisposeAsync must not block the NEXT profile's startup on a slow
/// prune pass (the window/CLI run profiles sequentially), but it must still remove its own scratch disk-cache
/// directory once that prune settles -- no leaked temp directories after the window closes.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BenchmarkImageExecutorTeardownTests : IDisposable
{
    private readonly TempRoot _root = new("benchmark-teardown");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "DisposeAsync returns without waiting for the prune pass to finish, and the scratch directory is still removed once it settles")]
    public async Task DisposeAsync_DoesNotBlockOnPrune_ButStillCleansUpEventually()
    {
        var files = Enumerable.Range(0, 5)
            .Select(i => _root.File($"img-{i:000}.png", TestImages.PreviewPng)).ToArray();
        // DiskCache=true forces the real persist/prune path (every shipped profile ships with DiskCache=false --
        // see BenchmarkProfiles.P -- so the override is needed to exercise the prune machinery here).
        var profile = BenchmarkProfiles.Find("fast-sequential")! with { DiskCache = true, Iterations = 1, WarmupCount = 0 };
        var executor = new BenchmarkImageExecutor(profile, files, files.Sum(f => new FileInfo(f).Length), hasHeadroom: _ => true);
        var cacheDir = executor.DiskCacheDirectory;

        foreach (var file in files) await executor.DecodeAsync(file);

        var sw = Stopwatch.StartNew();
        await executor.DisposeAsync();
        sw.Stop();

        // The old implementation awaited up to 5s of prune settling right here; teardown itself must return fast
        // regardless of how long the prune pass actually takes, so the next profile can start immediately.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"DisposeAsync took {sw.Elapsed} -- it must not wait for the prune pass");

        // Cleanup is only GUARANTEED once the background teardown task settles -- await it explicitly.
        await executor.TeardownBackgroundTask.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(Directory.Exists(cacheDir), $"Scratch disk-cache directory was not cleaned up: {cacheDir}");
    }
}
