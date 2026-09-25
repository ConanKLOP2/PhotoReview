using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PhotoReview.PerfAnalysis;

/// <summary>Writes summary.md (human-readable Markdown tables) and summary.json (full numeric
/// detail) for one `--perf-analyze` run (D11 spec item 8).</summary>
public static class PerfAnalyzeReport
{
    // Groups without complete navs carry NaN percentiles; the default writer throws on them, so they are written as null.
    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        WriteIndented = true,
        Converters = { new NaNAsNullDoubleConverter() },
    };

    public static void WriteMarkdown(string path, PerfAnalyze.AnalysisResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Perf analyze summary");
        sb.AppendLine();
        sb.AppendLine(FormattableString.Invariant($"- perf-*.csv files: {result.CsvFileCount}"));
        sb.AppendLine(FormattableString.Invariant($"- Groups (scenario/mode/cond/workers): {result.Groups.Count}"));
        sb.AppendLine();

        sb.AppendLine("## Navigation groups (scenario/mode/cond/workers)");
        sb.AppendLine();
        sb.AppendLine("| Group | count | incomplete | first P50 | first P95 | first max | final P50 | final P95 | final max | renderedFrame P50 | renderedFrame P95 | renderedFrame max | hit rate | notes |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var g in result.Groups)
        {
            var s = g.Summary;
            sb.AppendLine(FormattableString.Invariant(
                $"| {s.Key} | {s.Count} | {s.Incomplete} | {Ms(s.FirstP50)} | {Ms(s.FirstP95)} | {Ms(s.FirstMax)} | {Ms(s.FinalP50)} | {Ms(s.FinalP95)} | {Ms(s.FinalMax)} | {Ms(s.RenderedFrameP50)} | {Ms(s.RenderedFrameP95)} | {Ms(s.RenderedFrameMax)} | {Pct(s.HitRate)} | {(s.LowSampleWarning ? "N<20" : "")} |"));
        }
        sb.AppendLine();

        sb.AppendLine("## Breakdown by kind");
        sb.AppendLine();
        sb.AppendLine("| Group | kind | count |");
        sb.AppendLine("|---|---|---:|");
        foreach (var g in result.Groups)
        foreach (var (kind, count) in g.Summary.KindCounts.OrderByDescending(kv => kv.Value))
            sb.AppendLine(FormattableString.Invariant($"| {g.Summary.Key} | {kind} | {count} |"));
        sb.AppendLine();

        sb.AppendLine("## Phase share in the slowest 10% of navigations (mean t_x/finalVisual)");
        sb.AppendLine();
        sb.AppendLine("| Group | phase | share |");
        sb.AppendLine("|---|---|---:|");
        foreach (var g in result.Groups)
        foreach (var (phase, share) in g.Summary.SlowestPhaseShare.OrderByDescending(kv => kv.Value))
            sb.AppendLine(FormattableString.Invariant($"| {g.Summary.Key} | {phase} | {Pct(share)} |"));
        sb.AppendLine();

        sb.AppendLine("## Preload");
        sb.AppendLine();
        sb.AppendLine("| Group | worker | items | paused | cancel | queueWait P50 | queueWait P95 | decode P50 | decode P95 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var g in result.Groups)
        {
            var s = g.Summary;
            var queueWaits = s.PreloadItems.Select(p => p.QueueWaitMs).OrderBy(v => v).ToList();
            var decodeMs = s.PreloadItems.Where(p => p.Kind == "decoded").Select(p => p.Ms).OrderBy(v => v).ToList();
            sb.AppendLine(FormattableString.Invariant(
                $"| {s.Key} | {(s.PreloadWorkers?.ToString(CultureInfo.InvariantCulture) ?? "?")} | {s.PreloadItems.Count} | {s.PreloadPausedCount} | {s.PreloadCancelCount} | {Ms(PercentileOrNaN(queueWaits, 50))} | {Ms(PercentileOrNaN(queueWaits, 95))} | {Ms(PercentileOrNaN(decodeMs, 50))} | {Ms(PercentileOrNaN(decodeMs, 95))} |"));
        }
        sb.AppendLine();

        sb.AppendLine("## Dispatcher (>16ms)");
        sb.AppendLine();
        sb.AppendLine("DispatcherLongOp before the first ShowStart/Folder(start) of each file is excluded " +
                       "(e.g. --perf-session builds the WPF window ~700 ms before the scenario starts — D06).");
        sb.AppendLine();
        sb.AppendLine("| Group | count | (excluded before start) | total ms | top 5 names |");
        sb.AppendLine("|---|---:|---:|---:|---|");
        foreach (var g in result.Groups)
        {
            var s = g.Summary;
            var top5 = s.DispatcherLongOps.GroupBy(d => d.Name)
                .Select(gr => (Name: gr.Key, Count: gr.Count(), Total: gr.Sum(x => x.Ms)))
                .OrderByDescending(x => x.Total).Take(5)
                .Select(x => FormattableString.Invariant($"{x.Name}×{x.Count} ({x.Total:F1}ms)"));
            sb.AppendLine(FormattableString.Invariant(
                $"| {s.Key} | {s.DispatcherLongOps.Count} | {s.DispatcherLongOpsBeforeStartCount} | {s.DispatcherLongOps.Sum(d => d.Ms):F1} | {string.Join("; ", top5)} |"));
        }
        sb.AppendLine();

        sb.AppendLine("## Folder (T0..T3)");
        sb.AppendLine();
        sb.AppendLine("| Group | gen | T1 catalogReady | T2 (T0->first present) | T3 phase | T3 |");
        sb.AppendLine("|---|---:|---:|---:|---|---:|");
        foreach (var g in result.Groups)
        foreach (var f in g.Summary.FolderGens.OrderBy(f => f.Gen))
            sb.AppendLine(FormattableString.Invariant(
                $"| {g.Summary.Key} | {f.Gen} | {Ms(f.T1CatalogReadyMs)} | {Ms(f.T2Ms)} | {f.T3Phase} | {Ms(f.T3Ms)} |"));
        sb.AppendLine();

        sb.AppendLine("## Startup (ms since process start, median across runs)");
        sb.AppendLine();
        sb.AppendLine("| Group | phase | median | min | max | N |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|");
        foreach (var g in result.Groups)
        foreach (var phase in StartupPhases(g.Summary))
            sb.AppendLine(FormattableString.Invariant(
                $"| {g.Summary.Key} | {phase.Phase} | {phase.Median:F1} | {phase.Min:F1} | {phase.Max:F1} | {phase.Count} |"));
        sb.AppendLine();

        sb.AppendLine("## Decision rules (rules.json)");
        sb.AppendLine();
        sb.AppendLine("| Group | Rule | Triggered | Evidence | Notes |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var g in result.Groups)
        foreach (var r in g.Rules)
            sb.AppendLine(FormattableString.Invariant(
                $"| {g.Summary.Key} | {r.Rule} | {(r.Triggered is null ? "N/A" : r.Triggered.Value ? "yes" : "no")} | {Escape(r.Evidence)} | {Escape(r.Note ?? "")} |"));

        File.WriteAllText(path, sb.ToString());
    }

    public static void WriteJson(string path, PerfAnalyze.AnalysisResult result)
    {
        var payload = new
        {
            csvFileCount = result.CsvFileCount,
            groups = result.Groups.Select(g => new
            {
                scenario = g.Summary.Key.Scenario,
                mode = g.Summary.Key.Mode,
                cond = g.Summary.Key.Cond,
                preloadWorkers = g.Summary.Key.PreloadWorkers,
                count = g.Summary.Count,
                incomplete = g.Summary.Incomplete,
                lowSampleWarning = g.Summary.LowSampleWarning,
                firstVisualMs = new { p50 = g.Summary.FirstP50, p95 = g.Summary.FirstP95, max = g.Summary.FirstMax },
                finalVisualMs = new { p50 = g.Summary.FinalP50, p95 = g.Summary.FinalP95, max = g.Summary.FinalMax },
                renderedFrameMs = new { p50 = g.Summary.RenderedFrameP50, p95 = g.Summary.RenderedFrameP95, max = g.Summary.RenderedFrameMax },
                hitRate = g.Summary.HitRate,
                kindCounts = g.Summary.KindCounts,
                slowestDecilePhaseShare = g.Summary.SlowestPhaseShare,
                preload = new
                {
                    workers = g.Summary.PreloadWorkers,
                    items = g.Summary.PreloadItems.Count,
                    pausedCount = g.Summary.PreloadPausedCount,
                    cancelCount = g.Summary.PreloadCancelCount,
                },
                dispatcherLongOpCount = g.Summary.DispatcherLongOps.Count,
                dispatcherLongOpsBeforeStartCount = g.Summary.DispatcherLongOpsBeforeStartCount,
                gcTimePercent = g.Summary.GcTimePercent,
                frameTimeP95Ms = g.Summary.FrameTimeP95Ms,
                folder = g.Summary.FolderGens.Select(f => new { f.Gen, f.T1CatalogReadyMs, f.T2Ms, f.T3Phase, f.T3Ms }),
                startup = StartupPhases(g.Summary).Select(p => new { p.Phase, p.Median, p.Min, p.Max, p.Count }),
                rules = g.Rules.Select(r => new { r.Rule, r.Triggered, r.Evidence, r.Note }),
            }),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, DefaultOptions));
    }

    /// <summary>perf(startup): per-phase median/min/max over the group's runs, ordered by median.</summary>
    internal static IEnumerable<(string Phase, double Median, double Min, double Max, int Count)> StartupPhases(GroupSummary summary) =>
        summary.StartupRuns
            .SelectMany(run => run)
            .GroupBy(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
            .Select(group =>
            {
                var sorted = group.OrderBy(v => v).ToList();
                var mid = sorted.Count / 2;
                var median = sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
                return (group.Key, median, sorted[0], sorted[^1], sorted.Count);
            })
            .OrderBy(p => p.Item2);

    private static double PercentileOrNaN(List<double> sortedAsc, double p) => sortedAsc.Count == 0 ? double.NaN : PerfStats.NearestRank(sortedAsc, p);

    private static string Ms(double? value) => value is null || double.IsNaN(value.Value) ? "N/A" : value.Value.ToString("F1", CultureInfo.InvariantCulture);
    private static string Pct(double value) => double.IsNaN(value) ? "N/A" : (value * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
    private static string Escape(string value) => value.Replace("|", "\\|").Replace("\n", " ");
}
