using System.IO;
using System.Text.Json;
using PhotoReview.Benchmark.Cli.PerfCompare;
using PhotoReview.Benchmark.Cli.ShellHost;

namespace PhotoReview.Integration.Tests.PerfCompare;

/// <summary>
/// "Chứng minh bằng số" bước 3-4: schema kết quả đo, thống kê so sánh (PASS/WARN/REGRESSION/INCONCLUSIVE), mã thoát và verb CLI
/// <c>compare</c> với số liệu giả. Mutation hai bước: xem docs/refactoring/decisions/NOWPF-PERF-COMPARE.md.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PerfCompareTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pr-perfcompare-test-" + Guid.NewGuid().ToString("N")[..8]);

    public PerfCompareTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly double[] Quiet = [100, 101, 99, 100, 102, 98];
    private static readonly double[] Noisy = [100, 104, 96, 108, 92, 100];

    private static PerfMachine Machine(string name = "PC1") => new(name, "CPU X", 8, 16, "Windows", "GPU");

    private static PerfMetric Metric(string name, IEnumerable<double> samples, string unit = "ms", string direction = PerfSchema.LowerIsBetter) =>
        new(name, unit, direction, [.. samples]);

    private static PerfResult Result(IEnumerable<PerfMetric> metrics, double cpu = 20, string scenario = "startup-probe", string machine = "PC1",
        IReadOnlyList<string>? flags = null, IReadOnlyDictionary<string, string>? parameters = null, int version = PerfSchema.Version) =>
        new(version, scenario, new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero), Machine(machine), "abc123", false, "1.0", 6, 1,
            parameters ?? new Dictionary<string, string> { ["count"] = "10" }, new PerfBackground(cpu, cpu + 10, 6, flags ?? []), [.. metrics]);

    private static double[] Scale(double[] values, double factor) => [.. values.Select(v => v * factor)];

    private static MetricComparison Single(PerfResult b, PerfResult c, PerfCompareOptions? o = null) =>
        Assert.Single(PerfComparer.Compare(b, c, o).Metrics);

    // ---- bảng kịch bản --------------------------------------------------------------------

    [Fact]
    public void Unchanged_IsPass_ExitZero()
    {
        var r = PerfComparer.Compare(Result([Metric("a", Quiet)]), Result([Metric("a", Quiet)]));
        Assert.Equal(PerfVerdict.Pass, r.Overall);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(0, r.Metrics[0].Worse, 9);
        Assert.False(r.Metrics[0].Improved);
    }

    [Fact]
    public void Improvement_IsPass_AndMarkedImproved()
    {
        var m = Single(Result([Metric("a", Quiet)]), Result([Metric("a", Scale(Quiet, 0.8))]));
        Assert.Equal(PerfVerdict.Pass, m.Verdict);
        Assert.True(m.Improved);
        Assert.True(m.Worse < 0);
    }

    [Fact]
    public void SmallRegressionBelowHalfTheThreshold_IsPass()
    {
        var m = Single(Result([Metric("a", Quiet)]), Result([Metric("a", Scale(Quiet, 1.02))]));
        Assert.Equal(PerfVerdict.Pass, m.Verdict);
    }

    [Fact]
    public void ClearRegression_IsRegression_ExitOne()
    {
        var r = PerfComparer.Compare(Result([Metric("a", Quiet)]), Result([Metric("a", Scale(Quiet, 1.2))]));
        Assert.Equal(PerfVerdict.Regression, r.Overall);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal(20.0, r.Metrics[0].WorsePercent, 6);
    }

    [Fact]
    public void RegressionInsideNoise_IsWarn_NotRegression()
    {
        // median +8 ms (> 5 ms allowed) but the 95 % interval of the difference reaches below the threshold.
        var m = Single(Result([Metric("a", Noisy)]), Result([Metric("a", Noisy.Select(v => v + 8))]));
        Assert.Equal(PerfVerdict.Warn, m.Verdict);
        Assert.Equal(0, PerfComparer.Compare(Result([Metric("a", Noisy)]), Result([Metric("a", Noisy.Select(v => v + 8))])).ExitCode);
    }

    [Fact]
    public void Drift_MoreThanHalfTheThresholdAndSignificant_IsWarn()
    {
        // +3 % of a very quiet metric: below 5 %, above half of it, and the interval is far from zero.
        double[] quiet = [100, 100.1, 99.9, 100, 100.05, 99.95];
        var m = Single(Result([Metric("a", quiet)]), Result([Metric("a", quiet.Select(v => v + 3))]));
        Assert.Equal(PerfVerdict.Warn, m.Verdict);
    }

    [Fact]
    public void BusyMachine_DowngradesARegressionToInconclusive_WithReason()
    {
        var r = PerfComparer.Compare(Result([Metric("a", Quiet)]), Result([Metric("a", Scale(Quiet, 1.5))], cpu: 75));
        Assert.Equal(PerfVerdict.Inconclusive, r.Overall);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("busy", r.Metrics[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BusyBaseline_AlsoCounts()
    {
        var r = PerfComparer.Compare(Result([Metric("a", Quiet)], cpu: 61), Result([Metric("a", Scale(Quiet, 1.5))]));
        Assert.Equal(PerfVerdict.Inconclusive, r.Overall);
    }

    [Fact]
    public void CpuExactlyAtTheBusyLimit_IsNotBusy()
    {
        var r = PerfComparer.Compare(Result([Metric("a", Quiet)], cpu: 60), Result([Metric("a", Scale(Quiet, 1.5))], cpu: 60));
        Assert.Equal(PerfVerdict.Regression, r.Overall);
    }

    [Fact]
    public void BusyMachine_StillAllowsAPass()
    {
        var r = PerfComparer.Compare(Result([Metric("a", Quiet)], cpu: 90), Result([Metric("a", Quiet)], cpu: 90));
        Assert.Equal(PerfVerdict.Pass, r.Overall);
    }

    [Fact]
    public void HighVariance_DowngradesAnApparentRegression()
    {
        double[] wild = [100, 160, 60, 140, 70, 100];
        var m = Single(Result([Metric("a", wild)]), Result([Metric("a", Scale(wild, 1.5))]));
        Assert.Equal(PerfVerdict.Inconclusive, m.Verdict);
        Assert.Contains("variance", m.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingMetricInCurrent_IsInconclusive_NewMetricIsPass()
    {
        var r = PerfComparer.Compare(Result([Metric("a", Quiet), Metric("b", Quiet)]), Result([Metric("a", Quiet), Metric("c", Quiet)]));
        Assert.Equal(PerfVerdict.Inconclusive, r.Metrics.Single(m => m.Name == "b").Verdict);
        Assert.Equal(PerfVerdict.Pass, r.Metrics.Single(m => m.Name == "c").Verdict);
        Assert.Equal(PerfVerdict.Inconclusive, r.Overall);
    }

    [Fact]
    public void TooFewSamples_IsInconclusive()
    {
        var m = Single(Result([Metric("a", [100, 101])]), Result([Metric("a", [200, 201])]));
        Assert.Equal(PerfVerdict.Inconclusive, m.Verdict);
        Assert.Contains("too few", m.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactlyMinSamples_IsEnough()
    {
        var m = Single(Result([Metric("a", [100, 100, 100])]), Result([Metric("a", [130, 130, 130])]));
        Assert.Equal(PerfVerdict.Regression, m.Verdict);
    }

    [Fact]
    public void HigherIsBetter_ADropIsARegression_AGainIsPass()
    {
        double[] fps = [60, 61, 59, 60, 60, 61];
        var down = Single(Result([Metric("fps", fps, "count", PerfSchema.HigherIsBetter)]), Result([Metric("fps", Scale(fps, 0.7), "count", PerfSchema.HigherIsBetter)]));
        Assert.Equal(PerfVerdict.Regression, down.Verdict);
        Assert.True(down.Worse > 0);
        var up = Single(Result([Metric("fps", fps, "count", PerfSchema.HigherIsBetter)]), Result([Metric("fps", Scale(fps, 1.3), "count", PerfSchema.HigherIsBetter)]));
        Assert.Equal(PerfVerdict.Pass, up.Verdict);
        Assert.True(up.Improved);
        Assert.Equal(60, up.BaseMedian, 3);   // reported in original (not negated) units
    }

    [Fact]
    public void AbsoluteFloor_ProtectsTinyMetrics()
    {
        // 2 ms -> 2.8 ms is +40 % but under the 1 ms absolute floor.
        var m = Single(Result([Metric("k", [2, 2, 2, 2, 2, 2])]), Result([Metric("k", [2.8, 2.8, 2.8, 2.8, 2.8, 2.8])]));
        Assert.Equal(PerfVerdict.Pass, m.Verdict);
        Assert.Equal(1.0, m.Allowed, 9);
        var over = Single(Result([Metric("k", [2, 2, 2, 2, 2, 2])]), Result([Metric("k", [3.2, 3.2, 3.2, 3.2, 3.2, 3.2])]));
        Assert.Equal(PerfVerdict.Regression, over.Verdict);
    }

    [Fact]
    public void MemoryUnit_UsesItsOwnFloor()
    {
        // 100 MB -> 110 MB: +10 % > 5 % and > 16 MB? 10 MB < 16 MB floor but 5 % = 5 MB so allowed = 16 -> PASS.
        var m = Single(Result([Metric("ws", [100, 100, 100, 100], "MB")]), Result([Metric("ws", [110, 110, 110, 110], "MB")]));
        Assert.Equal(16.0, m.Allowed, 9);
        Assert.Equal(PerfVerdict.Pass, m.Verdict);
    }

    [Fact]
    public void OneSpikeInTheCurrentRun_IsDroppedAsAnOutlier()
    {
        double[] spiky = [100, 101, 99, 100, 102, 450];
        var m = Single(Result([Metric("a", Quiet)]), Result([Metric("a", spiky)]));
        Assert.Equal(PerfVerdict.Pass, m.Verdict);
        Assert.Equal(1, m.DroppedCur);
        Assert.Equal(5, m.CurN);
    }

    [Fact]
    public void ASpikeInTheBaseline_IsDroppedToo()
    {
        double[] spiky = [100, 101, 99, 100, 102, 450];
        var m = Single(Result([Metric("a", spiky)]), Result([Metric("a", Quiet)]));
        Assert.Equal(1, m.DroppedBase);
        Assert.Equal(PerfVerdict.Pass, m.Verdict);
    }

    [Fact]
    public void Outliers_AreRemovedOnTheBadSideOnly()
    {
        // lower-is-better: a LOW outlier is real data and stays; higher-is-better: a low spike is the bad side and goes.
        double[] lowSpike = [100, 101, 99, 100, 102, 10];
        var lower = Single(Result([Metric("a", Quiet)]), Result([Metric("a", lowSpike)]));
        Assert.Equal(0, lower.DroppedCur);
        var higher = Single(Result([Metric("a", Quiet, "ms", PerfSchema.HigherIsBetter)]), Result([Metric("a", lowSpike, "ms", PerfSchema.HigherIsBetter)]));
        Assert.Equal(1, higher.DroppedCur);
    }

    [Fact]
    public void DifferentMachine_DowngradesARegression()
    {
        var r = PerfComparer.Compare(Result([Metric("a", Quiet)]), Result([Metric("a", Scale(Quiet, 1.5))], machine: "PC2"));
        Assert.Equal(PerfVerdict.Inconclusive, r.Overall);
        Assert.Contains(r.ContextNotes, n => n.Contains("different machine", StringComparison.Ordinal));
    }

    [Fact]
    public void DifferentParameters_DowngradeARegression()
    {
        var r = PerfComparer.Compare(Result([Metric("a", Quiet)]),
            Result([Metric("a", Scale(Quiet, 1.5))], parameters: new Dictionary<string, string> { ["count"] = "20" }));
        Assert.Equal(PerfVerdict.Inconclusive, r.Overall);
    }

    [Theory]
    [InlineData("failed-runs")]
    [InlineData("forced-kill")]
    public void FailedOrKilledRuns_DowngradeARegression(string flag)
    {
        var r = PerfComparer.Compare(Result([Metric("a", Quiet)]), Result([Metric("a", Scale(Quiet, 1.5))], flags: [flag]));
        Assert.Equal(PerfVerdict.Inconclusive, r.Overall);
    }

    [Fact]
    public void RegressionBeatsInconclusive_InTheOverallVerdict()
    {
        var r = PerfComparer.Compare(Result([Metric("a", Quiet), Metric("gone", Quiet)]), Result([Metric("a", Scale(Quiet, 1.5))]));
        Assert.Equal(PerfVerdict.Regression, r.Overall);
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void ThresholdOption_IsHonoured()
    {
        var b = Result([Metric("a", Quiet)]);
        var c = Result([Metric("a", Scale(Quiet, 1.2))]);
        Assert.Equal(PerfVerdict.Regression, Single(b, c).Verdict);
        Assert.Equal(PerfVerdict.Pass, Single(b, c, new PerfCompareOptions(ThresholdPercent: 50)).Verdict);
    }

    [Fact]
    public void DifferentScenarios_AreNotComparable()
    {
        Assert.Throws<PerfSchemaException>(() => PerfComparer.Compare(Result([Metric("a", Quiet)]), Result([Metric("a", Quiet)], scenario: "other")));
    }

    [Fact]
    public void SummaryOnlyMetric_HasTooFewSamplesToJudge()
    {
        var old = new PerfMetric("a", "ms", PerfSchema.LowerIsBetter, [], SummaryP50: 100);
        var m = Single(Result([old]), Result([Metric("a", Quiet)]));
        Assert.Equal(PerfVerdict.Inconclusive, m.Verdict);
    }

    // ---- schema ---------------------------------------------------------------------------

    [Fact]
    public void Json_RoundTrips_AndIsStable()
    {
        var original = Result([Metric("z.b", Quiet), Metric("a.a", [1.23456, 2.5, 3], "MB"), new PerfMetric("empty", "ms", PerfSchema.LowerIsBetter, [])]);
        var json = PerfResultJson.Write(original);
        var back = PerfResultJson.Parse(json);
        Assert.Equal(original.Scenario, back.Scenario);
        Assert.Equal(PerfSchema.Version, back.SchemaVersion);
        Assert.Equal("PC1", back.Machine.Name);
        Assert.Equal("abc123", back.Commit);
        Assert.Equal(["a.a", "empty", "z.b"], back.Metrics.Select(m => m.Name));
        Assert.Equal([1.235, 2.5, 3], back.Metrics[0].Samples);
        Assert.Equal(PerfResultJson.Write(back), json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(PerfSchema.Name, doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("metrics")[1].GetProperty("p50").ValueKind);
        Assert.Equal(100, doc.RootElement.GetProperty("metrics")[2].GetProperty("p50").GetDouble(), 3);
    }

    [Fact]
    public void Parse_RejectsOldAndFutureSchemaVersions_AndForeignFiles()
    {
        var json = PerfResultJson.Write(Result([Metric("a", Quiet)]));
        Assert.Throws<PerfSchemaException>(() => PerfResultJson.Parse(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 0", StringComparison.Ordinal)));
        Assert.Throws<PerfSchemaException>(() => PerfResultJson.Parse(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal)));
        Assert.Throws<PerfSchemaException>(() => PerfResultJson.Parse(json.Replace("\"schemaVersion\": 1,", "", StringComparison.Ordinal)));
        Assert.Throws<PerfSchemaException>(() => PerfResultJson.Parse("{\"schemaVersion\":1,\"metrics\":[]}"));
        Assert.Throws<PerfSchemaException>(() => PerfResultJson.Parse("[1,2]"));
        Assert.Throws<PerfSchemaException>(() => PerfResultJson.Parse("not json"));
    }

    [Fact]
    public void Parse_RejectsBadDirectionAndDuplicateNames()
    {
        var json = PerfResultJson.Write(Result([Metric("a", Quiet)]));
        Assert.Throws<PerfSchemaException>(() => PerfResultJson.Parse(json.Replace("lowerIsBetter", "sideways", StringComparison.Ordinal)));
        var duplicate = Result([Metric("a", Quiet)]) with { Metrics = [Metric("a", Quiet), Metric("a", Quiet)] };
        Assert.Throws<PerfSchemaException>(() => PerfResultJson.Parse(PerfResultJson.Write(duplicate)));
    }

    // ---- CLI end-to-end -------------------------------------------------------------------

    private string Save(string name, PerfResult result)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, PerfResultJson.Write(result));
        return path;
    }

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = PerfCompareCommand.RunCompare(["compare", .. args], o, e);
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public void Cli_SameFile_Passes_AndPrintsATable()
    {
        var path = Save("a.json", Result([Metric("shell.firstFrameMs", [460, 465, 470, 455, 462, 468])]));
        var (exit, output, error) = Cli(path, path);
        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Contains("PERF-COMPARE: PASS (exit 0)", output, StringComparison.Ordinal);
        Assert.Contains("| shell.firstFrameMs | ms |", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_TwentyPercentSlower_IsRegression_ExitOne_AndWritesJsonAndMarkdown()
    {
        var b = Save("b.json", Result([Metric("shell.firstFrameMs", [460, 465, 470, 455, 462, 468])]));
        var c = Save("c.json", Result([Metric("shell.firstFrameMs", new double[] { 460, 465, 470, 455, 462, 468 }.Select(v => v * 1.2))]));
        var json = Path.Combine(_dir, "out.json");
        var md = Path.Combine(_dir, "out.md");
        var (exit, output, _) = Cli(b, c, "--json", json, "--markdown", md);
        Assert.Equal(1, exit);
        Assert.Contains("REGRESSION", output, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        Assert.Equal("REGRESSION", doc.RootElement.GetProperty("overall").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("REGRESSION", doc.RootElement.GetProperty("metrics")[0].GetProperty("verdict").GetString());
        Assert.Contains("REGRESSION", File.ReadAllText(md), StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_BusyMachine_IsInconclusive_ExitThree()
    {
        var b = Save("b.json", Result([Metric("a", Quiet)]));
        var c = Save("c.json", Result([Metric("a", Scale(Quiet, 1.3))], cpu: 80));
        var (exit, output, _) = Cli(b, c);
        Assert.Equal(3, exit);
        Assert.Contains("INCONCLUSIVE", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_ThresholdArgument_Changes_TheVerdict()
    {
        var b = Save("b.json", Result([Metric("a", Quiet)]));
        var c = Save("c.json", Result([Metric("a", Scale(Quiet, 1.2))]));
        Assert.Equal(1, Cli(b, c).Exit);
        Assert.Equal(0, Cli(b, c, "--threshold-percent", "50").Exit);
    }

    [Fact]
    public void Cli_BusyCpuArgument_Changes_TheVerdict()
    {
        var b = Save("b.json", Result([Metric("a", Quiet)]));
        var c = Save("c.json", Result([Metric("a", Scale(Quiet, 1.3))], cpu: 40));
        Assert.Equal(1, Cli(b, c).Exit);
        Assert.Equal(3, Cli(b, c, "--busy-cpu", "30").Exit);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--threshold-percent")]
    [InlineData("--threshold-percent abc")]
    [InlineData("--max-cv -1")]
    public void Cli_BadArguments_ExitTwo(string extra)
    {
        var path = Save("a.json", Result([Metric("a", Quiet)]));
        var (exit, _, error) = Cli([path, path, .. extra.Split(' ')]);
        Assert.Equal(2, exit);
        Assert.NotEqual("", error);
    }

    [Fact]
    public void Cli_MissingFileAndWrongPositionalCount_ExitTwo()
    {
        var path = Save("a.json", Result([Metric("a", Quiet)]));
        Assert.Equal(2, Cli(path, Path.Combine(_dir, "nope.json")).Exit);
        Assert.Equal(2, Cli(path).Exit);
    }

    [Fact]
    public void Cli_OldSchemaAndScenarioMismatch_ExitTwo_NotARegression()
    {
        var good = Save("a.json", Result([Metric("a", Quiet)]));
        var old = Path.Combine(_dir, "old.json");
        File.WriteAllText(old, File.ReadAllText(good).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 0", StringComparison.Ordinal));
        Assert.Equal(2, Cli(good, old).Exit);
        var other = Save("o.json", Result([Metric("a", Quiet)], scenario: "perf-session:S2"));
        var (exit, _, error) = Cli(good, other);
        Assert.Equal(2, exit);
        Assert.Contains("scenarios differ", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_PerfCompareAlias_IsRecognised()
    {
        Assert.True(PerfCompareCommand.IsCompare(["compare", "a", "b"]));
        Assert.True(PerfCompareCommand.IsCompare(["--perf-compare", "a", "b"]));
        Assert.False(PerfCompareCommand.IsCompare(["--startup-probe"]));
        Assert.False(PerfCompareCommand.IsCompare([]));
    }

    // ---- builders -------------------------------------------------------------------------

    private static ProbeRunResult Run(int round, string app, bool warmup, bool ok, double first, double cpu = 30, bool kill = false, double[]? keys = null) =>
        new(round, app, warmup, ok, "", first, first - 100, first - 50, keys ?? [5, 6, 7], keys ?? [1, 2, 3], 0, 150, kill, cpu);

    [Fact]
    public void FromStartupProbe_UsesMeasuredSuccessfulRunsOnly_AndRoundTripsToPass()
    {
        ProbeApp[] apps = [new("wpf", "w.exe"), new("shell", "s.exe")];
        var options = new ProbeOptions(apps, 4, 1, 1, _dir);
        List<ProbeRunResult> runs =
        [
            Run(0, "wpf", true, true, 9999), Run(0, "shell", true, true, 9999),
            Run(1, "wpf", false, true, 1200), Run(1, "shell", false, true, 460),
            Run(2, "wpf", false, true, 1210), Run(2, "shell", false, false, 7777, cpu: 90),
            Run(3, "wpf", false, true, 1190), Run(3, "shell", false, true, 470, kill: true),
            Run(4, "wpf", false, true, 1205), Run(4, "shell", false, true, 465),
        ];
        var outcome = StartupProbeRunner.Analyse(options, runs);
        var env = new PerfEnvironment(Machine(), "deadbeef", true, "9.9", DateTimeOffset.UnixEpoch);
        var result = PerfResultBuilder.FromStartupProbe(outcome, options, new Dictionary<string, string> { ["count"] = "10" }, env);

        Assert.Equal("startup-probe", result.Scenario);
        Assert.Equal(4, result.Runs);
        Assert.Equal(1, result.WarmupRuns);
        var shell = result.Metrics.Single(m => m.Name == "shell.firstFrameMs");
        Assert.Equal([460.0, 470.0, 465.0], shell.Samples);
        var wpf = result.Metrics.Single(m => m.Name == "wpf.firstFrameMs");
        Assert.Equal([1200.0, 1210.0, 1190.0, 1205.0], wpf.Samples);
        Assert.Equal("MB", result.Metrics.Single(m => m.Name == "shell.peakWorkingSetMb").Unit);
        Assert.Contains("failed-runs", result.Background.NoiseFlags);
        Assert.Contains("forced-kill", result.Background.NoiseFlags);
        Assert.DoesNotContain("busy-machine", result.Background.NoiseFlags);   // median of the measured runs is 30
        Assert.Equal("deadbeef", result.Commit);
        Assert.True(result.Dirty);

        var path = Save("probe.json", result);
        Assert.Equal(PerfVerdict.Pass, PerfComparer.Compare(PerfResultJson.Load(path), PerfResultJson.Load(path)).Overall);
    }

    [Fact]
    public void FromStartupProbe_FlagsABusyMachine()
    {
        ProbeApp[] apps = [new("shell", "s.exe")];
        var options = new ProbeOptions(apps, 3, 0, 1, _dir);
        var outcome = StartupProbeRunner.Analyse(options, [Run(0, "shell", false, true, 460, cpu: 75), Run(1, "shell", false, true, 462, cpu: 80), Run(2, "shell", false, true, 465, cpu: 70)]);
        var result = PerfResultBuilder.FromStartupProbe(outcome, options, new Dictionary<string, string>(),
            new PerfEnvironment(Machine(), "c", false, "v", DateTimeOffset.UnixEpoch));
        Assert.Contains("busy-machine", result.Background.NoiseFlags);
        Assert.Equal(75, result.Background.CpuPercentMedian, 6);
        Assert.Equal(80, result.Background.CpuPercentMax, 6);
    }

    [Fact]
    public void FromPerfSessionDirectory_ReadsEveryIteration()
    {
        var root = Path.Combine(_dir, "session");
        for (var i = 1; i <= 3; i++)
        {
            var it = Path.Combine(root, $"iter-{i}");
            Directory.CreateDirectory(it);
            File.WriteAllText(Path.Combine(it, "process.json"),
                "{\"peakWorkingSetBytes\":" + (200L * 1024 * 1024 * i) + ",\"workingSetBytes\":104857600,\"gcCountDelta\":{\"gen0\":10,\"gen1\":4,\"gen2\":" + i + "},\"gcPauseDeltaMs\":" + (5 * i) + ",\"cpuDeltaMs\":900,\"elapsedMs\":1500}");
            File.WriteAllText(Path.Combine(it, "session.json"),
                "{\"scenario\":\"S2\",\"commit\":\"abc\",\"mode\":\"window\",\"keySettle\":{\"p50Ms\":2.0,\"p95Ms\":" + (3 + i) + "}}");
            File.WriteAllText(Path.Combine(it, "metrics.json"), "{\"SourceReads\":" + (40 + i) + ",\"DecodeMilliseconds\":812.5,\"CacheMisses\":7}");
        }

        var result = PerfResultBuilder.FromPerfSessionDirectory(root, new PerfEnvironment(Machine(), "ignored", false, "v", DateTimeOffset.UnixEpoch));
        Assert.Equal("perf-session:S2", result.Scenario);
        Assert.Equal(3, result.Runs);
        Assert.Equal("abc", result.Commit);
        Assert.Equal([200.0, 400.0, 600.0], result.Metrics.Single(m => m.Name == "peakWorkingSetMb").Samples);
        Assert.Equal([1.0, 2.0, 3.0], result.Metrics.Single(m => m.Name == "gcGen2Count").Samples);
        Assert.Equal([41.0, 42.0, 43.0], result.Metrics.Single(m => m.Name == "sourceReads").Samples);
        Assert.Equal([4.0, 5.0, 6.0], result.Metrics.Single(m => m.Name == "keySettleP95Ms").Samples);
        Assert.Equal("window", result.Parameters["mode"]);
        Assert.True(double.IsNaN(result.Background.CpuPercentMedian));
        // round trip through JSON keeps NaN as null
        Assert.True(double.IsNaN(PerfResultJson.Parse(PerfResultJson.Write(result)).Background.CpuPercentMedian));
    }

    [Fact]
    public void FromPerfSessionDirectory_WithoutIterations_Throws()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "empty"));
        Assert.Throws<InvalidOperationException>(() =>
            PerfResultBuilder.FromPerfSessionDirectory(Path.Combine(_dir, "empty"), new PerfEnvironment(Machine(), "c", false, "v", DateTimeOffset.UnixEpoch)));
    }

    [Fact]
    public void Export_WritesAFileThatCompareCanRead()
    {
        var root = Path.Combine(_dir, "session");
        var it = Path.Combine(root, "iter-1");
        Directory.CreateDirectory(it);
        File.WriteAllText(Path.Combine(it, "process.json"), "{\"peakWorkingSetBytes\":1048576,\"elapsedMs\":10}");
        File.WriteAllText(Path.Combine(it, "session.json"), "{\"scenario\":\"S1\"}");
        var outPath = Path.Combine(_dir, "exported.json");
        var o = new StringWriter();
        Assert.Equal(0, PerfCompareCommand.RunExport(["--perf-export", root, "--out", outPath], o, new StringWriter()));
        Assert.Contains("PERF-JSON:", o.ToString(), StringComparison.Ordinal);
        Assert.Equal("perf-session:S1", PerfResultJson.Load(outPath).Scenario);
        Assert.Equal(2, PerfCompareCommand.RunExport(["--perf-export"], new StringWriter(), new StringWriter()));
        Assert.Equal(2, PerfCompareCommand.RunExport(["--perf-export", Path.Combine(_dir, "nope")], new StringWriter(), new StringWriter()));
    }

    [Fact]
    public void DefaultPath_GoesToWorkPerf_WhenTheRepoRootIsKnown()
    {
        var r = Result([Metric("a", Quiet)], scenario: "startup-probe");
        var path = PerfCompareCommand.DefaultPath(@"C:\repo", @"C:\fallback", r);
        Assert.Equal(Path.Combine(@"C:\repo", "work", "perf", "startup-probe-PC1-20261013-000000.json"), path);
        Assert.StartsWith(@"C:\fallback", PerfCompareCommand.DefaultPath(null, @"C:\fallback", r), StringComparison.Ordinal);
    }

    [Fact]
    public void StartupProbeArguments_AcceptPerfJson()
    {
        var parsed = StartupProbeCommand.Parse(["--startup-probe", "--shell", "s.exe", "--source", "p", "--perf-json", "x.json"]);
        Assert.Equal("x.json", parsed.PerfJson);
        Assert.Null(StartupProbeCommand.Parse(["--startup-probe", "--shell", "s.exe", "--source", "p"]).PerfJson);
    }
}
