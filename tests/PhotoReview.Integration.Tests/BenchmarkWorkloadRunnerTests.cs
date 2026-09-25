using PhotoReview.Benchmarking;
using System.IO;
using PhotoReview.App;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Covers workload selection, file-action mapping and preload-hit bookkeeping used by the
/// WPF benchmark window and CLI runner.
/// </summary>
[Trait("Category", "Slow")]
public sealed class BenchmarkWorkloadRunnerTests : IDisposable
{
    private readonly TempRoot _root = new("benchmark-workload-runner");
    private readonly List<BenchmarkImageExecutor> _executors = [];
    private readonly RecordingRecycleBin _bin = new();

    /// <summary>Stands in for the user's real Recycle Bin (TEST-10): records the call and removes the file like a real send would.</summary>
    private sealed class RecordingRecycleBin : IRecycleBin
    {
        public List<string> Sent { get; } = [];

        public void SendToRecycleBin(string path)
        {
            Sent.Add(path);
            File.Delete(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    public void Dispose()
    {
        foreach (var executor in _executors) executor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _root.Dispose();
    }

    private BenchmarkImageExecutor NewExecutor(BenchmarkProfile profile, string[] files, long totalSourceBytes,
        Func<double, bool>? hasHeadroom = null)
    {
        var executor = new BenchmarkImageExecutor(profile, files, totalSourceBytes, hasHeadroom);
        _executors.Add(executor);
        return executor;
    }

    [Theory(DisplayName = "A file-action profile performs its own operation and leaves the source in the state its name promises")]
    [InlineData("action-move")]
    [InlineData("action-delete")]
    [InlineData("action-copy")]
    public async Task FileActionProfilePerformsItsOwnOperation(string profileId)
    {
        var profile = BenchmarkProfiles.Find(profileId)!;
        var files = new[] { _root.File($"{profileId}-source.png", TestImages.PreviewPng) };
        var executor = NewExecutor(profile, files, new FileInfo(files[0]).Length);

        var (correct, metrics) = await BenchmarkWorkloadRunner.RunIterationAsync(
            executor, files, profile, BenchmarkWorkload.FileAction, iteration: 0,
            BenchmarkWorkloadRunner.CreateSeededRandom(profile.Id), _bin, CancellationToken.None);

        // RunFileActionAsync's own correctness check already fails if the wrong op ran
        // (e.g. delete's op left the source in place, or copy's op removed it), so a true
        // result here is the regression signal for the action-id -> operation mapping.
        Assert.True(correct);
        Assert.NotNull(metrics);
        // The action profile must operate on a scratch copy, never the catalog file itself.
        Assert.True(File.Exists(files[0]));
        // Only the delete profile goes through the (injected) Recycle Bin, exactly once per iteration.
        Assert.Equal(profileId == "action-delete" ? 1 : 0, _bin.Sent.Count);
    }

    [Fact(DisplayName = "PrepareIterationAsync does the file-action setup but defers the action itself to the timed measure step (R2-F-14)")]
    public async Task PrepareIterationAsync_FileAction_ActionRunsOnlyInMeasureStep()
    {
        var profile = BenchmarkProfiles.Find("action-delete")!;
        var files = new[] { _root.File("prepare-source.png", TestImages.PreviewPng) };
        var executor = NewExecutor(profile, files, new FileInfo(files[0]).Length);

        var measure = await BenchmarkWorkloadRunner.PrepareIterationAsync(
            executor, files, profile, BenchmarkWorkload.FileAction, iteration: 0,
            BenchmarkWorkloadRunner.CreateSeededRandom(profile.Id), _bin, CancellationToken.None);

        Assert.Empty(_bin.Sent); // setup only: the timed action has not run
        var (correct, _) = await measure();
        Assert.True(correct);
        Assert.Single(_bin.Sent);
    }

    [Fact(DisplayName = "The interleaved action profile cycles move, delete, copy across iterations and matches each op's expected source state")]
    [Trait("Category", "Integration")]
    public async Task InterleavedActionProfileCyclesThroughAllThreeOperations()
    {
        var profile = BenchmarkProfiles.Find("action-interleaved")!;
        var files = new[] { _root.File("interleaved-source.png", TestImages.PreviewPng) };
        var executor = NewExecutor(profile, files, new FileInfo(files[0]).Length);
        var random = BenchmarkWorkloadRunner.CreateSeededRandom(profile.Id);

        for (var iteration = 0; iteration < 3; iteration++)
        {
            var (correct, _) = await BenchmarkWorkloadRunner.RunIterationAsync(
                executor, files, profile, BenchmarkWorkload.FileAction, iteration, random, _bin, CancellationToken.None);
            Assert.True(correct, $"iteration {iteration} (op {iteration % 3}) did not match its expected source state");
        }

        // Iterations 0..2 map to move, delete, copy: exactly one of them is a delete.
        Assert.Single(_bin.Sent);
    }

    [Fact(DisplayName = "CreateSeededRandom produces the same sequence for the same profile id across separate calls")]
    public void CreateSeededRandomIsStableAcrossCalls()
    {
        var first = BenchmarkWorkloadRunner.CreateSeededRandom("preview-balanced");
        var second = BenchmarkWorkloadRunner.CreateSeededRandom("preview-balanced");
        var firstSequence = Enumerable.Range(0, 25).Select(_ => first.Next(1000)).ToArray();
        var secondSequence = Enumerable.Range(0, 25).Select(_ => second.Next(1000)).ToArray();
        Assert.Equal(firstSequence, secondSequence);
    }

    [Fact(DisplayName = "Random workload selects the identical file sequence across separate runs seeded from the same profile id")]
    [Trait("Category", "Integration")]
    public async Task RandomWorkloadSelectionIsDeterministicAcrossRuns()
    {
        var profile = BenchmarkProfiles.Find("random-navigation")! with { Workers = 1 };
        var files = Enumerable.Range(0, 5)
            .Select(i => _root.File($"rand-{i}.png", TestImages.PreviewPng)).ToArray();
        var totalBytes = files.Sum(f => new FileInfo(f).Length);

        async Task<long> RunAsync()
        {
            var executor = NewExecutor(profile, files, totalBytes);
            var random = BenchmarkWorkloadRunner.CreateSeededRandom(profile.Id);
            for (var iteration = 0; iteration < 10; iteration++)
                await BenchmarkWorkloadRunner.RunIterationAsync(
                    executor, files, profile, BenchmarkWorkload.Random, iteration, random, _bin, CancellationToken.None);
            // Cache hit/miss pattern (and so this count) depends entirely on the random
            // index sequence, so it only matches across runs if that sequence is identical.
            return executor.Metrics.SourceReads;
        }

        Assert.Equal(await RunAsync(), await RunAsync());
    }

    [Fact(DisplayName = "WarmNext workload records a real preload hit once the previous iteration's warm-up lands")]
    public async Task WarmNextWorkloadRecordsPreloadHitAfterWarming()
    {
        var profile = BenchmarkProfiles.Find("fast-balanced")!;
        var files = Enumerable.Range(0, 2)
            .Select(i => _root.File($"warm-{i}.png", TestImages.PreviewPng)).ToArray();
        // Real preload memory headroom is machine-dependent (production behavior); fix it to
        // "always available" so this assertion exercises the preload-hit bookkeeping itself,
        // not how much free RAM the machine running the test happens to have.
        var executor = NewExecutor(profile, files, files.Sum(f => new FileInfo(f).Length), hasHeadroom: _ => true);

        await executor.DecodeAsync(files[0]);
        executor.WarmPreloadAround(0);

        // WarmPreloadAround is fire-and-forget by design (matches production), so poll for
        // the background warm-up to land instead of asserting on a fixed delay.
        // TC09: poll a condition with a deadline instead of asserting after a fixed delay.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (executor.Metrics.PreloadHits == 0 && DateTime.UtcNow < deadline)
        {
            await executor.DecodeAsync(files[1]);
            await Task.Delay(10); // not Task.Yield: a yield loop busy-spins a pool thread and starved the code under test on 2-vCPU CI
        }

        Assert.True(executor.Metrics.PreloadHits > 0);
    }
}
