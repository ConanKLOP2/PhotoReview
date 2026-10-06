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
        var executor = new BenchmarkImageExecutor(profile, files, hasHeadroom: _ => true);
        var cacheDir = executor.DiskCacheDirectory;

        foreach (var file in files) await executor.DecodeAsync(file);
        // The RAM cache is far bigger than 5 tiny previews, so nothing gets evicted/persisted to disk from the
        // decodes above -- seed the scratch directory directly so this test actually exercises "was the
        // directory removed", independent of whether a real persist/prune pass happened to run.
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "seed.pv4"), "seed");

        // APP-T04: hold a real prune pass in flight (a barrier in the prune loop), so "does not wait for the prune" is a
        // property of the code under test and not of how fast the machine happens to prune an empty directory.
        using var pruneEntered = new ManualResetEventSlim(false);
        using var releasePrune = new ManualResetEventSlim(false);
        var diskStore = executor.PreviewServiceForTests.DiskStore;
        diskStore.BeforeClearPruneScheduledForTests = () =>
        {
            pruneEntered.Set();
            releasePrune.Wait(TimeSpan.FromSeconds(60)); // bounded: a failing test must not park a pool thread forever
        };
        try
        {
            diskStore.SchedulePrune();
            Assert.True(pruneEntered.Wait(TimeSpan.FromSeconds(30)), "the prune pass never reached the barrier");

            // The old implementation awaited the prune settling right here. With the pass held open, a DisposeAsync that waits for it
            // cannot complete, so a bounded wait turns "blocks on prune" into a failure instead of a hang.
            await executor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            // The prune is still running: the scratch directory must survive until it settles, and teardown is still pending.
            Assert.False(executor.TeardownBackgroundTask.IsCompleted, "teardown finished while the prune pass was still in flight");
            Assert.True(Directory.Exists(cacheDir), "the scratch directory was removed while the prune pass was still running");
        }
        finally
        {
            releasePrune.Set();
        }

        // Cleanup is only GUARANTEED once the background teardown task settles -- await it explicitly.
        await executor.TeardownBackgroundTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(Directory.Exists(cacheDir), $"Scratch disk-cache directory was not cleaned up: {cacheDir}");
    }
}
