using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PhotoReview.Benchmark.Cli;
using PhotoReview.Benchmarking;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Integration.Tests;

/// <summary>Function-audit benchmark trustworthiness: TOOL-01, PERF-01/02/03, TOOL-04/05.</summary>
[Trait("Category", "Integration")]
public sealed class BenchmarkTrustTests : IDisposable
{
    private readonly TempRoot _root = new("benchmark-trust");
    private readonly List<BenchmarkImageExecutor> _executors = [];

    public void Dispose()
    {
        foreach (var executor in _executors) executor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _root.Dispose();
    }

    // ---- TOOL-01 ---------------------------------------------------------------------------------

    [Fact(DisplayName = "TOOL-01: decoder-bench fails and records failure counts when no decode succeeds")]
    public async Task DecoderBench_NoSuccessfulDecode_FailsAndRecordsFailures()
    {
        var folder = _root.Dir("bad");
        File.WriteAllText(Path.Combine(folder, "broken.jpg"), "this is not a jpeg");
        var outDir = _root.Combine("bad-out");

        var exit = await DecoderBenchmark.RunAsync(["--decoder-bench", folder, outDir, "Wpf", "0", "1"]);

        Assert.NotEqual(0, exit);
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "summary.json")));
        Assert.Equal(1, summary.RootElement.GetProperty("TotalFailures").GetInt32());
        var group = Assert.Single(summary.RootElement.GetProperty("Groups").EnumerateArray());
        Assert.Equal(1, group.GetProperty("FailureCount").GetInt32());
        Assert.Equal(0, group.GetProperty("SuccessCount").GetInt32());
    }

    [Fact(DisplayName = "TOOL-01: decoder-bench still succeeds when decodes work")]
    public async Task DecoderBench_SuccessfulDecode_ReturnsZero()
    {
        var folder = _root.Dir("good");
        _root.File("good/ok.png", TestImages.PreviewPng);

        var exit = await DecoderBenchmark.RunAsync(["--decoder-bench", folder, _root.Combine("good-out"), "Wpf", "0", "1"]);

        Assert.Equal(0, exit);
    }

    // ---- PERF-01 ---------------------------------------------------------------------------------

    private string[] MakePngs(int count) => Enumerable.Range(0, count)
        .Select(i => _root.File($"win{count}/img-{i:000}.png", TestImages.PreviewPng)).ToArray();

    private BenchmarkImageExecutor NewExecutor(BenchmarkProfile profile, string[] files)
    {
        var executor = new BenchmarkImageExecutor(profile, files, files.Sum(f => new FileInfo(f).Length), hasHeadroom: _ => true);
        _executors.Add(executor);
        return executor;
    }

    [Fact(DisplayName = "PERF-01: NextWindow/PreviousWindow bound what preload warms")]
    public async Task Executor_HonorsNextAndPreviousWindow()
    {
        var files = MakePngs(9);
        var profile = BenchmarkProfiles.Find("fast-balanced")! with { NextWindow = 2, PreviousWindow = 1, FullFolder = false };
        var executor = NewExecutor(profile, files);

        executor.WarmPreloadAround(4);
        await executor.WhenPreloadSettledAsync();

        Assert.True(executor.IsPreviewCached(files[5]));
        Assert.True(executor.IsPreviewCached(files[6]));
        Assert.True(executor.IsPreviewCached(files[3]));
        Assert.False(executor.IsPreviewCached(files[7]));
        Assert.False(executor.IsPreviewCached(files[2]));
        Assert.False(executor.IsPreviewCached(files[0]));
        Assert.False(executor.IsPreviewCached(files[8]));
    }

    [Fact(DisplayName = "PERF-01: a zero window (no-preload baseline) warms no neighbours")]
    public async Task Executor_ZeroWindowWarmsNoNeighbours()
    {
        var files = MakePngs(5);
        var profile = BenchmarkProfiles.Find("no-preload-baseline")!;
        var executor = NewExecutor(profile, files);

        executor.WarmPreloadAround(2);
        await executor.WhenPreloadSettledAsync();

        Assert.False(executor.IsPreviewCached(files[3]));
        Assert.False(executor.IsPreviewCached(files[1]));
    }

    [Fact(DisplayName = "PERF-01: FullFolder warms beyond the scheduler window; non-FullFolder never does")]
    public async Task Executor_FullFolderControlsWholeFolderWarming()
    {
        // 45 files: the scheduler's own forward window (32) leaves the last file out unless it goes whole-folder.
        var files = MakePngs(45);
        var full = NewExecutor(BenchmarkProfiles.Find("full-folder-warm")! with { NextWindow = 100, PreviousWindow = 100, FullFolder = true }, files);
        full.WarmPreloadAround(0);
        await full.WhenPreloadSettledAsync();
        Assert.True(full.IsPreviewCached(files[44]));

        var windowed = NewExecutor(BenchmarkProfiles.Find("fast-balanced")! with { NextWindow = 100, PreviousWindow = 100, FullFolder = false }, files);
        windowed.WarmPreloadAround(0);
        await windowed.WhenPreloadSettledAsync();
        Assert.False(windowed.IsPreviewCached(files[44]));
    }

    [Fact(DisplayName = "PERF-01: DetailedLogging is applied for the run and the previous state restored")]
    public void ProfileScope_AppliesAndRestoresLogging()
    {
        var enabled = false;
        var on = BenchmarkProfiles.Find("logging-on")!;
        var off = BenchmarkProfiles.Find("logging-off")!;

        using (BenchmarkProfileScope.ApplyLogging(on, () => enabled, v => enabled = v))
            Assert.True(enabled);
        Assert.False(enabled);

        enabled = true;
        using (BenchmarkProfileScope.ApplyLogging(off, () => enabled, v => enabled = v))
            Assert.False(enabled);
        Assert.True(enabled);
    }

    // ---- PERF-02 ---------------------------------------------------------------------------------

    private static Task<(bool Idle, string How)> WaitIdle(Func<bool> preloadIdle, TimeSpan timeout) =>
        PerfSession.WaitIdleCoreAsync(() => new ReviewMetrics().Snapshot(), preloadIdle, () => true, () => Task.CompletedTask,
            timeout, pollInterval: TimeSpan.FromMilliseconds(5), stableRequired: TimeSpan.FromMilliseconds(20),
            preloadFallbackAfter: TimeSpan.FromHours(1));

    [Fact(DisplayName = "PERF-02: waitIdle does not report idle while preload is still active")]
    public async Task WaitIdle_BlockedPreload_TimesOut()
    {
        var (idle, _) = await WaitIdle(() => false, TimeSpan.FromMilliseconds(300));

        Assert.False(idle);
    }

    [Fact(DisplayName = "PERF-02: waitIdle completes only after preload reports idle")]
    public async Task WaitIdle_CompletesOnlyOncePreloadIsIdle()
    {
        var polls = 0;

        var (idle, how) = await WaitIdle(() => ++polls > 8, TimeSpan.FromSeconds(30));

        Assert.True(idle);
        Assert.True(polls > 8);
        Assert.Contains("preload done", how, StringComparison.Ordinal);
    }

    // ---- PERF-03 ---------------------------------------------------------------------------------

    [Fact(DisplayName = "PERF-03: ranking compares per-image latency, not samples of different image counts")]
    public void Ranking_NormalizesByImagesPerSample()
    {
        // 8 images in 80 ms (10 ms/image) beats 1 image in 50 ms, although 80 > 50 raw.
        var parallel = new BenchmarkPhaseResult("many", BenchmarkWorkload.Sequential, [80, 80], BenchmarkResultStatus.Pass, ImagesPerSample: 8);
        var single = new BenchmarkPhaseResult("one", BenchmarkWorkload.Sequential, [50, 50], BenchmarkResultStatus.Pass, ImagesPerSample: 1);

        var ranked = BenchmarkRanking.Rank([single, parallel], BenchmarkWorkload.Sequential);

        Assert.Equal(["many", "one"], ranked.Select(p => p.ProfileId));
    }

    [Fact(DisplayName = "PERF-03: a phase without a stamped count derives it from the profile registry")]
    public void Ranking_DerivesImagesPerSampleFromProfile()
    {
        // fast-sequential runs 8 workers, no-preload-baseline 1.
        var many = new BenchmarkPhaseResult("fast-sequential", BenchmarkWorkload.Sequential, [80], BenchmarkResultStatus.Pass);
        var one = new BenchmarkPhaseResult("no-preload-baseline", BenchmarkWorkload.Sequential, [50], BenchmarkResultStatus.Pass);

        Assert.Equal(["fast-sequential", "no-preload-baseline"],
            BenchmarkRanking.Rank([one, many], BenchmarkWorkload.Sequential).Select(p => p.ProfileId));
    }

    [Fact(DisplayName = "PERF-03: the profile runner stamps the images each sample decoded")]
    public async Task RunProfileAsync_StampsImagesPerSample()
    {
        var profile = BenchmarkProfiles.Find("fast-sequential")! with { Iterations = 1, WarmupCount = 0 };

        var report = await BenchmarkWorkloadRunner.RunProfileAsync(_root.Path, profile,
            (_, _, _) => Task.FromResult<Func<Task<(bool Correct, ReviewMetricsSnapshot? Metrics)>>>(
                () => Task.FromResult<(bool, ReviewMetricsSnapshot?)>((true, null))),
            progress: null, timeProvider: null, CancellationToken.None, fileCount: 3);

        Assert.Equal(3, Assert.Single(report.Phases).ImagesPerSample); // 8 workers capped by 3 files
        Assert.Equal(1, BenchmarkWorkloadRunner.ImagesPerSample(profile, BenchmarkWorkload.FirstFrame, 3));
    }

    // ---- TOOL-04 / TOOL-05 -----------------------------------------------------------------------

    private static readonly string[] PowerShellPrefix = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"];
    private static readonly byte[] Bytes = [1, 2, 3];

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PhotoReview.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("PhotoReview.slnx not found above " + AppContext.BaseDirectory);
    }

    private static (int ExitCode, string Output) RunPowerShell(params string[] args)
    {
        var psi = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in PowerShellPrefix.Concat(args)) psi.ArgumentList.Add(a);
        PowerShellRunner.ForWindowsPowerShell(psi);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    [Fact(DisplayName = "TOOL-04: procmon-summary counts production .pv4 preview-cache I/O as cache, not other")]
    public void ProcmonSummary_CountsPv4PreviewCache()
    {
        var csv = _root.Combine("procmon.csv");
        File.WriteAllLines(csv,
        [
            "\"Time of Day\",\"Process Name\",\"PID\",\"Operation\",\"Path\",\"Result\",\"Detail\"",
            "\"1\",\"PhotoReview.App.exe\",\"1\",\"CreateFile\",\"C:\\Users\\u\\AppData\\Local\\PhotoReview\\cache\\abc.pv4\",\"SUCCESS\",\"\"",
            "\"2\",\"PhotoReview.App.exe\",\"1\",\"ReadFile\",\"C:\\Users\\u\\AppData\\Local\\PhotoReview\\cache\\abc.pv4\",\"SUCCESS\",\"Length: 4,096\"",
        ]);

        var (exit, output) = RunPowerShell(Path.Combine(RepoRoot(), "tools", "diag", "procmon-summary.ps1"), "-Csv", csv, "-Out", _root.Combine("out.csv"));

        Assert.Equal(0, exit);
        var line = Assert.Single(output.Split('\n'), l => l.StartsWith("cache-pv4", StringComparison.Ordinal));
        Assert.Contains("CreateFile=1", line, StringComparison.Ordinal);
        Assert.Contains("ReadFile=1", line, StringComparison.Ordinal);
        Assert.Contains("Bytes=4096", line, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "TOOL-05: benchmark-folder rejects a folder holding only formats the app cannot review (.webp)")]
    public void BenchmarkFolder_IgnoresWebp()
    {
        var folder = _root.Dir("webp");
        _root.File("webp/a.webp", Bytes);
        var script = Path.Combine(RepoRoot(), "tools", "benchmark-folder.ps1");

        var (exit, _) = RunPowerShell(script, "-Folder", folder, "-Runs", "1");
        Assert.NotEqual(0, exit);

        _root.File("webp/b.jpg", Bytes);
        var (okExit, okOutput) = RunPowerShell(script, "-Folder", folder, "-Runs", "1");
        Assert.Equal(0, okExit);
        Assert.Contains("files=1;", okOutput, StringComparison.Ordinal);
    }
}
