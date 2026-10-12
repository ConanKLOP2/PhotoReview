using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhotoReview.Benchmark.Cli.ShellHost;

/// <summary>Một bản ứng dụng cần đo (publish R2R): <c>Id</c> ngắn (<c>shell</c>/<c>wpf</c>), đường dẫn exe.</summary>
internal sealed record ProbeApp(string Id, string ExePath);

/// <summary>Kết quả một lần chạy (một tiến trình mới).</summary>
internal sealed record ProbeRunResult(
    int Round, string App, bool IsWarmup, bool Ok, string Note,
    double FirstFrameMs, double WindowShownMs, double WindowRevealedMs,
    IReadOnlyList<double> KeyToFrameMs, IReadOnlyList<double> KeyToFrameCachedMs, int KeysWithoutFrame, double PeakWorkingSetMb, bool ForcedKill, double SystemCpuPercent = double.NaN);

/// <summary>Ranh giới tiến trình: một lần chạy thật hoặc giả lập trong test.</summary>
internal interface IProbeSession
{
    ProbeRunResult Run(ProbeApp app, ProbeSlot slot);
}

internal sealed record ProbeOptions(
    IReadOnlyList<ProbeApp> Apps, int Rounds, int WarmupRounds, int Seed, string OutDirectory,
    double RegressionPercent = 5.0, double RegressionMinMs = 1.0);

/// <summary>Ngưỡng perf gate đợt 3 (NO-WPF-EXEC-PLAN-WP mục 7.4 + thẻ WP-20/NW-5).</summary>
internal static class WaveThreeTargets
{
    public const double FirstFrameMs = 600;       // NW-5 (a) R2R; thẻ WP-20 ghi 450 (mục tiêu cuối đợt 3)
    public const double FirstFrameStretchMs = 450;
    public const double WindowVisibleMs = 150;
    public const double KeyToFrameP95Ms = 3;
    public const double SettingsSecondOpenMs = 150;
    public const double RegressionPercent = 5;
}

internal sealed record AppAggregate(
    string App, int Runs, int FailedRuns, int ForcedKills,
    SampleSummary FirstFrame, SampleSummary WindowShown, SampleSummary WindowRevealed,
    SampleSummary KeyToFrame, SampleSummary KeyToFrameCached, SampleSummary KeyCachedRunP95, SampleSummary PeakWorkingSetMb, SampleSummary SystemCpuPercent,
    int KeysWithoutFrame);

internal sealed record ProbeOutcome(
    IReadOnlyList<ProbeRunResult> Runs, IReadOnlyList<AppAggregate> Aggregates,
    IReadOnlyDictionary<string, Comparison> Comparisons, string Baseline, string Candidate);

internal static class StartupProbeRunner
{
    /// <summary>Chạy lịch xen kẽ, bỏ vòng khởi động, gom theo ứng dụng, so ứng dụng thứ hai với thứ nhất (baseline).</summary>
    public static ProbeOutcome Run(ProbeOptions options, IProbeSession session)
    {
        var slots = ProbeSchedule.Interleave([.. options.Apps.Select(a => a.Id)], options.Rounds, options.WarmupRounds, options.Seed);
        var runs = new List<ProbeRunResult>();
        foreach (var slot in slots)
        {
            var app = options.Apps.First(a => a.Id == slot.App);
            runs.Add(session.Run(app, slot));
        }

        return Analyse(options, runs);
    }

    internal static ProbeOutcome Analyse(ProbeOptions options, IReadOnlyList<ProbeRunResult> runs)
    {
        var aggregates = new List<AppAggregate>();
        foreach (var app in options.Apps)
        {
            var mine = runs.Where(r => r.App == app.Id && !r.IsWarmup).ToArray();
            var good = mine.Where(r => r.Ok).ToArray();
            var keys = good.SelectMany(r => r.KeyToFrameMs).ToArray();
            var cached = good.SelectMany(r => r.KeyToFrameCachedMs).ToArray();
            var perRunP95 = good.Where(r => r.KeyToFrameCachedMs.Count > 0).Select(r => ProbeStatistics.Percentile(r.KeyToFrameCachedMs, 95)).ToArray();
            aggregates.Add(new AppAggregate(
                app.Id, mine.Length, mine.Length - good.Length, mine.Count(r => r.ForcedKill),
                ProbeStatistics.Summarize([.. good.Select(r => r.FirstFrameMs)]),
                ProbeStatistics.Summarize([.. good.Select(r => r.WindowShownMs)]),
                ProbeStatistics.Summarize([.. good.Select(r => r.WindowRevealedMs)]),
                ProbeStatistics.Summarize(keys, removeOutliers: false),
                ProbeStatistics.Summarize(cached, removeOutliers: false),
                ProbeStatistics.Summarize(perRunP95, removeOutliers: false),
                ProbeStatistics.Summarize([.. good.Select(r => r.PeakWorkingSetMb)], removeOutliers: false),
                ProbeStatistics.Summarize([.. mine.Select(r => r.SystemCpuPercent)], removeOutliers: false),
                good.Sum(r => r.KeysWithoutFrame)));
        }

        var comparisons = new Dictionary<string, Comparison>(StringComparer.Ordinal);
        var baseline = options.Apps[0].Id;
        var candidate = options.Apps.Count > 1 ? options.Apps[1].Id : baseline;
        if (options.Apps.Count > 1)
        {
            double[] Pick(string app, Func<ProbeRunResult, double> f) =>
                [.. runs.Where(r => r.App == app && !r.IsWarmup && r.Ok).Select(f)];
            double[] Keys(string app) =>
                [.. runs.Where(r => r.App == app && !r.IsWarmup && r.Ok).SelectMany(r => r.KeyToFrameCachedMs)];
            Comparison C(double[] b, double[] c) =>
                ProbeStatistics.Compare(b, c, options.RegressionPercent, options.RegressionMinMs);
            comparisons["firstFrame"] = C(Pick(baseline, r => r.FirstFrameMs), Pick(candidate, r => r.FirstFrameMs));
            comparisons["windowRevealed"] = C(Pick(baseline, r => r.WindowRevealedMs), Pick(candidate, r => r.WindowRevealedMs));
            comparisons["keyToFrame"] = C(Keys(baseline), Keys(candidate));
            comparisons["peakWorkingSetMb"] = C(Pick(baseline, r => r.PeakWorkingSetMb), Pick(candidate, r => r.PeakWorkingSetMb));
        }

        return new ProbeOutcome(runs, aggregates, comparisons, baseline, candidate);
    }
}

/// <summary>Ghi CSV (một dòng/lần chạy, cùng cột cho mọi ứng dụng), JSON và bảng Markdown.</summary>
internal static class ProbeReport
{
    public const string CsvHeader =
        "round,app,warmup,ok,firstFrameMs,windowShownMs,windowRevealedMs,keyCount,keyToFrameP50Ms,keyToFrameP95Ms,cachedKeyCount,cachedKeyP50Ms,cachedKeyP95Ms,keysWithoutFrame,peakWorkingSetMb,forcedKill,systemCpuPercent,note";

    public static string ToCsv(ProbeOutcome outcome)
    {
        var text = new StringBuilder();
        text.AppendLine(CsvHeader);
        foreach (var r in outcome.Runs)
        {
            text.AppendLine(string.Join(',',
                r.Round.ToString(CultureInfo.InvariantCulture), r.App, r.IsWarmup ? "1" : "0", r.Ok ? "1" : "0",
                F(r.FirstFrameMs), F(r.WindowShownMs), F(r.WindowRevealedMs),
                r.KeyToFrameMs.Count.ToString(CultureInfo.InvariantCulture),
                F(ProbeStatistics.Percentile(r.KeyToFrameMs, 50), "F2"), F(ProbeStatistics.Percentile(r.KeyToFrameMs, 95), "F2"),
                r.KeyToFrameCachedMs.Count.ToString(CultureInfo.InvariantCulture),
                F(ProbeStatistics.Percentile(r.KeyToFrameCachedMs, 50), "F2"), F(ProbeStatistics.Percentile(r.KeyToFrameCachedMs, 95), "F2"),
                r.KeysWithoutFrame.ToString(CultureInfo.InvariantCulture), F(r.PeakWorkingSetMb),
                r.ForcedKill ? "1" : "0", F(r.SystemCpuPercent, "F0"), CsvText(r.Note)));
        }

        return text.ToString();
    }

    private static string F(double value, string format = "F1") =>
        double.IsFinite(value) ? value.ToString(format, CultureInfo.InvariantCulture) : "";

    private static string CsvText(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string ToJson(ProbeOutcome outcome) =>
        JsonSerializer.Serialize(new
        {
            baseline = outcome.Baseline,
            candidate = outcome.Candidate,
            aggregates = outcome.Aggregates,
            comparisons = outcome.Comparisons,
            runs = outcome.Runs,
        }, JsonOptions);

    public static string ToMarkdown(ProbeOutcome outcome)
    {
        var text = new StringBuilder();
        text.AppendLine("| App | Runs | Fail | Ảnh đầu median [min-max] | P95 | Cửa sổ hiện | Cửa sổ thấy được | key→frame mọi phím P50 / P95 | key→frame trong cache P50 / P95 (ms) | WS đỉnh (MB) | CPU máy % |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var a in outcome.Aggregates)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {a.App} | {a.Runs} | {a.FailedRuns} | {S(a.FirstFrame.Median, "F0")} [{S(a.FirstFrame.Min, "F0")}-{S(a.FirstFrame.Max, "F0")}] | {S(a.FirstFrame.P95, "F0")} | {S(a.WindowShown.Median, "F0")} | {S(a.WindowRevealed.Median, "F0")} | {S(a.KeyToFrame.Median, "F1")} / {S(a.KeyToFrame.P95, "F1")} (n={a.KeyToFrame.Count}) | {S(a.KeyToFrameCached.Median, "F1")} / {S(a.KeyToFrameCached.P95, "F1")} (n={a.KeyToFrameCached.Count}) | {S(a.PeakWorkingSetMb.Median, "F0")} | {S(a.SystemCpuPercent.Median, "F0")} |"));
        }

        foreach (var (name, c) in outcome.Comparisons)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- {outcome.Candidate} so với {outcome.Baseline} ({name}): delta {S(c.DeltaMs, "F1")} ms ({S(c.DeltaPercent, "F1")} %), hồi quy: {(c.IsRegression ? "CÓ" : "không")}"));
        }

        return text.ToString();
    }

    private static string S(double v, string f) => ProbeStatistics.Format(v, f);

    public static void Write(string directory, ProbeOutcome outcome)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "runs.csv"), ToCsv(outcome), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "summary.json"), ToJson(outcome), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "summary.md"), ToMarkdown(outcome), new UTF8Encoding(false));
    }
}
