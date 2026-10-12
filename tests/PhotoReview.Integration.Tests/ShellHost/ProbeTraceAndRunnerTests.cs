using System.Globalization;
using System.Text.Json;
using PhotoReview.Benchmark.Cli.ShellHost;

namespace PhotoReview.Integration.Tests.ShellHost;

/// <summary>WP-29: parser CSV perf, rút mốc khởi động/key->frame, orchestrator xen kẽ, báo cáo CSV/JSON/Markdown.</summary>
[Trait("Category", "Integration")]
public sealed class ProbeTraceAndRunnerTests
{
    private const long Base = 639_000_000_000_000_000;

    private static long Ticks(double ms) => Base + (long)(ms * TimeSpan.TicksPerMillisecond);

    private static string Row(double atMs, string ev, string nav = "", string a = "", string text = "") =>
        string.Create(CultureInfo.InvariantCulture, $"{Ticks(atMs)},1,1,{ev},{nav},,{a},,,,{text}");

    private static string[] SampleTrace() =>
    [
        "# commit=x diag= qpcFrequency=10000000",
        "utcTicks,qpcTicks,thread,event,nav,pathId,a,b,c,d,text",
        Row(0, "Startup", a: "75", text: "appStartup"),
        Row(180, "Startup", a: "255", text: "windowShown"),
        Row(225, "Startup", a: "300", text: "windowRevealed"),
        Row(400, "Lookup", nav: "1", text: "miss"),
        Row(415, "RenderedFrame", nav: "1", a: "12"),
        Row(1000, "KeyInput", nav: "0", a: "2", text: "Right"),
        Row(1000.5, "Lookup", nav: "2", text: "ramHit"),
        Row(1004, "RenderedFrame", nav: "2", a: "3"),
        Row(1300, "KeyInput", nav: "0", a: "0", text: "Left"),
        Row(1300.5, "Lookup", nav: "3", text: "miss"),
        Row(1330, "RenderedFrame", nav: "3", a: "28"),
        Row(1600, "KeyInput", nav: "0", a: "0", text: "Home"),
        Row(1610, "RenderedFrame", nav: "4", a: "5"),
        Row(2000, "KeyInput", nav: "0", a: "1", text: "Right"),
    ];

    [Fact]
    public void Parse_SkipsCommentsHeaderAndShortLines()
    {
        var rows = ProbeTrace.Parse([.. SampleTrace(), "garbage", "", "1,2,3"]);
        Assert.Equal(14, rows.Count);
        Assert.Equal("Startup", rows[0].Event);
        Assert.Equal("appStartup", rows[0].Text);
        Assert.Equal("75", rows[0].A);
    }

    [Fact]
    public void SplitCsv_HandlesQuotedCommasAndEscapedQuotes()
    {
        var fields = ProbeTrace.SplitCsv("a,\"b,c\",\"d\"\"e\",,f");
        Assert.Equal(["a", "b,c", "d\"e", "", "f"], fields);
    }

    [Fact]
    public void Extract_FirstFrame_IsMeasuredFromTheAppStartupAnchor()
    {
        var metrics = ProbeTrace.Extract(ProbeTrace.Parse(SampleTrace()));
        Assert.NotNull(metrics);
        Assert.Equal(75 + 415, metrics!.FirstFrameMs, 3);
        Assert.Equal(255, metrics.WindowShownMs, 3);
        Assert.Equal(300, metrics.WindowRevealedMs, 3);
    }

    [Fact]
    public void Extract_KeyToFrame_AddsInputDelay_ToTheFollowingRenderedFrame()
    {
        var metrics = ProbeTrace.Extract(ProbeTrace.Parse(SampleTrace()))!;
        // Right: 2 ms input delay + (1004 - 1000); Left: 0 + 30; "Home" is not a navigation key; the last Right has no frame.
        Assert.Equal(2, metrics.KeyToFrameMs.Count);
        Assert.Equal(6.0, metrics.KeyToFrameMs[0], 3);
        Assert.Equal(30.0, metrics.KeyToFrameMs[1], 3);
        Assert.Equal(1, metrics.KeysWithoutFrame);
    }

    [Fact]
    public void Extract_CachedKeyToFrame_OnlyCountsRamHitNavigations()
    {
        var metrics = ProbeTrace.Extract(ProbeTrace.Parse(SampleTrace()))!;
        Assert.Single(metrics.KeyToFrameCachedMs);
        Assert.Equal(6.0, metrics.KeyToFrameCachedMs[0], 3);
    }

    [Fact]
    public void Extract_SortsRowsByTimestamp_WhenWrittenOutOfOrder()
    {
        var lines = SampleTrace().Reverse().ToArray();
        var metrics = ProbeTrace.Extract(ProbeTrace.Parse(lines));
        Assert.NotNull(metrics);
        Assert.Equal(490, metrics!.FirstFrameMs, 3);
        Assert.Equal(2, metrics.KeyToFrameMs.Count);
    }

    [Fact]
    public void Extract_ReturnsNull_WithoutAnchorOrFirstFrame()
    {
        Assert.Null(ProbeTrace.Extract(ProbeTrace.Parse([Row(10, "RenderedFrame", nav: "1", a: "1")])));
        Assert.Null(ProbeTrace.Extract(ProbeTrace.Parse([Row(0, "Startup", a: "10", text: "appStartup")])));
    }

    [Fact]
    public void Extract_MissingWindowMarks_AreNaN()
    {
        var metrics = ProbeTrace.Extract(ProbeTrace.Parse(
            [Row(0, "Startup", a: "10", text: "appStartup"), Row(5, "RenderedFrame", nav: "1", a: "1")]))!;
        Assert.True(double.IsNaN(metrics.WindowShownMs));
        Assert.True(double.IsNaN(metrics.WindowRevealedMs));
        Assert.Equal(15, metrics.FirstFrameMs, 3);
    }

    // ---- orchestrator ----

    private sealed class FakeSession(Func<ProbeApp, ProbeSlot, ProbeRunResult> run) : IProbeSession
    {
        public List<(string App, int Round, bool Warmup)> Calls { get; } = [];

        public ProbeRunResult Run(ProbeApp app, ProbeSlot slot)
        {
            Calls.Add((app.Id, slot.Round, slot.IsWarmup));
            return run(app, slot);
        }
    }

    private static ProbeRunResult Result(ProbeSlot slot, string app, double first, bool ok = true, double[]? keys = null, double ws = 150) =>
        new(slot.Round, app, slot.IsWarmup, ok, ok ? "" : "failed", first, first - 100, first - 50,
            keys ?? [], keys ?? [], 0, ws, false, 20);

    private static ProbeOptions Options(int rounds = 3, int warmup = 1) =>
        new([new ProbeApp("wpf", "wpf.exe"), new ProbeApp("shell", "shell.exe")], rounds, warmup, 1, "out");

    [Fact]
    public void Run_DiscardsWarmupRounds_AndExcludesFailedRunsFromTheStatistics()
    {
        var session = new FakeSession((app, slot) =>
        {
            if (slot.IsWarmup)
            {
                return Result(slot, app.Id, 9999);
            }

            if (app.Id == "shell" && slot.Round == 2)
            {
                return Result(slot, app.Id, 1, ok: false);
            }

            return Result(slot, app.Id, app.Id == "wpf" ? 800 : 500);
        });

        var outcome = StartupProbeRunner.Run(Options(), session);
        Assert.Equal(8, session.Calls.Count);
        Assert.Equal(2, session.Calls.Count(c => c.Warmup));

        var shell = outcome.Aggregates.Single(a => a.App == "shell");
        Assert.Equal(3, shell.Runs);
        Assert.Equal(1, shell.FailedRuns);
        Assert.Equal(2, shell.FirstFrame.Count);
        Assert.Equal(500, shell.FirstFrame.Median, 6);
        var wpf = outcome.Aggregates.Single(a => a.App == "wpf");
        Assert.Equal(0, wpf.FailedRuns);
        Assert.Equal(800, wpf.FirstFrame.Max, 6);
    }

    [Fact]
    public void Run_ComparesTheSecondAppWithTheFirst_AndFlagsRegressions()
    {
        var slower = StartupProbeRunner.Run(Options(), new FakeSession((app, slot) => Result(slot, app.Id, app.Id == "wpf" ? 500 : 560)));
        Assert.Equal("wpf", slower.Baseline);
        Assert.Equal("shell", slower.Candidate);
        Assert.True(slower.Comparisons["firstFrame"].IsRegression);
        Assert.Equal(60, slower.Comparisons["firstFrame"].DeltaMs, 6);

        var faster = StartupProbeRunner.Run(Options(), new FakeSession((app, slot) => Result(slot, app.Id, app.Id == "wpf" ? 800 : 500)));
        Assert.False(faster.Comparisons["firstFrame"].IsRegression);
    }

    [Fact]
    public void Run_PerRunP95_UsesOnlyTheCachedKeyLatencies()
    {
        var outcome = StartupProbeRunner.Run(Options(rounds: 2, warmup: 0), new FakeSession((app, slot) =>
            Result(slot, app.Id, 500, keys: [1, 2, 3, 4, 100]) with { KeyToFrameCachedMs = [2, 2, 2, 2, 2] }));
        var shell = outcome.Aggregates.Single(a => a.App == "shell");
        Assert.Equal(2.0, shell.KeyCachedRunP95.Median, 6);
        Assert.Equal(10, shell.KeyToFrame.Count);
        Assert.Equal(10, shell.KeyToFrameCached.Count);
    }

    [Fact]
    public void Report_Csv_HasOneRowPerRunWithMatchingColumnCount()
    {
        var outcome = StartupProbeRunner.Run(Options(rounds: 2, warmup: 1),
            new FakeSession((app, slot) => Result(slot, app.Id, 500, ok: slot.Round != 2 || app.Id != "wpf", keys: [3, 5])));
        var lines = ProbeReport.ToCsv(outcome).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(1 + 6, lines.Length);
        Assert.Equal(ProbeReport.CsvHeader, lines[0]);
        var columns = ProbeReport.CsvHeader.Split(',').Length;
        Assert.All(lines.Skip(1), l => Assert.Equal(columns, ProbeTrace.SplitCsv(l).Count));
        Assert.Contains(lines, l => l.StartsWith("0,", StringComparison.Ordinal) && l.Contains(",1,1,500.0,", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith(",failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Report_Json_RoundTripsAndKeepsNaNAsNamedLiteral()
    {
        var outcome = StartupProbeRunner.Run(Options(rounds: 2, warmup: 0),
            new FakeSession((app, slot) => Result(slot, app.Id, 500) with { WindowShownMs = double.NaN }));
        using var doc = JsonDocument.Parse(ProbeReport.ToJson(outcome));
        Assert.Equal("wpf", doc.RootElement.GetProperty("baseline").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("aggregates").GetArrayLength());
        Assert.Equal(4, doc.RootElement.GetProperty("runs").GetArrayLength());
    }

    [Fact]
    public void Report_Markdown_ListsEveryAppAndTheComparisons()
    {
        var outcome = StartupProbeRunner.Run(Options(), new FakeSession((app, slot) => Result(slot, app.Id, app.Id == "wpf" ? 800 : 500)));
        var markdown = ProbeReport.ToMarkdown(outcome);
        Assert.Contains("| wpf | 3 | 0 | 800 [800-800]", markdown, StringComparison.Ordinal);
        Assert.Contains("| shell | 3 | 0 | 500 [500-500]", markdown, StringComparison.Ordinal);
        Assert.Contains("(firstFrame): delta -300.0 ms (-37.5 %), hồi quy: không", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Arguments_AppliesDefaultsAndValidates()
    {
        var parsed = StartupProbeCommand.Parse(["--startup-probe", "--shell", "s.exe", "--source", "photos"]);
        Assert.Equal(8, parsed.Rounds);
        Assert.Equal(1, parsed.Warmup);
        Assert.Equal(10, parsed.Count);
        Assert.Null(parsed.WpfExe);

        Assert.Throws<ArgumentException>(() => StartupProbeCommand.Parse(["--startup-probe", "--source", "photos"]));
        Assert.Throws<ArgumentException>(() => StartupProbeCommand.Parse(["--startup-probe", "--shell", "s.exe"]));
        Assert.Throws<ArgumentException>(() => StartupProbeCommand.Parse(["--startup-probe", "--shell", "s.exe", "--source", "p", "--rounds", "0"]));
        Assert.Throws<ArgumentException>(() => StartupProbeCommand.Parse(["--startup-probe", "--shell", "s.exe", "--source", "p", "--bogus"]));
    }
}
