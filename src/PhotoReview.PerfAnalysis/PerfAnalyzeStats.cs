using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.Json;

namespace PhotoReview.PerfAnalysis;

/// <summary>(scenario, mode, cond) metadata for one perf-*.csv run, read from sibling
/// session.json/process.json written by D06 when present, with best-effort fallbacks when they
/// are not (D11 must still run standalone against a hand-made CSV, e.g. the D11 sample).</summary>
public sealed record RunFileMeta(string Scenario, string Mode, string Cond, int? PreloadWorkers, double? GcTimePercent, double? FrameTimeP95Ms)
{
    public static RunFileMeta Load(PerfCsvFile file)
    {
        var dir = Path.GetDirectoryName(file.Path) ?? "";
        string scenario = "unknown", mode = "", cond = "unknown";
        int? workers = null;
        double? gcPct = null, frameP95 = null;

        // D06 (--perf-session) writes session.json alongside each iteration's perf-*.csv with the
        // scenario name/alias, mode, DIAG_* env vars and per-step QPC marks. Field names here are
        // best-effort (D06's exact schema lands via a separate merge) — anything missing or
        // differently named just falls back to "unknown"/majority-vote-from-navs (see PerfAnalyze).
        var sessionPath = Path.Combine(dir, "session.json");
        if (File.Exists(sessionPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(sessionPath));
                var root = doc.RootElement;
                scenario = GetString(root, "scenario") ?? GetString(root, "alias") ?? scenario;
                // D06 writes folderAlias; keep fixtures apart so F1 and F2 runs are never pooled.
                if (GetString(root, "folderAlias") is { Length: > 0 } folderAlias) scenario = $"{scenario}@{folderAlias}";
                mode = GetString(root, "mode") ?? mode;
                cond = GetString(root, "cond") ?? GetString(root, "condition") ?? cond;
                if (root.TryGetProperty("preloadWorkers", out var w) && w.TryGetInt32(out var wi)) workers = wi;
                else
                {
                    // D11 draft used "diag"; D06 writes "env" (SortedDictionary<string,string>).
                    foreach (var envName in new[] { "diag", "env" })
                    {
                        if (!root.TryGetProperty(envName, out var diagEl) || diagEl.ValueKind != JsonValueKind.Object
                            || !diagEl.TryGetProperty("PHOTOREVIEW_DIAG_PRELOAD_WORKERS", out var dw)) continue;
                        if (dw.ValueKind == JsonValueKind.Number && dw.TryGetInt32(out var dwi)) { workers = dwi; break; }
                        if (dw.ValueKind == JsonValueKind.String && int.TryParse(dw.GetString(), out var dws)) { workers = dws; break; }
                    }
                }
            }
            catch (JsonException) { /* tolerate a hand-edited or partial session.json */ }
        }

        // run-matrix.ps1 lays runs out as <scenario>\<alias>-<mode>-<condition>\run-NN\; session.json
        // carries no condition, so recover it from the cell directory name.
        if (cond == "unknown")
        {
            var cell = Path.GetFileName(Path.GetDirectoryName(dir) ?? "") ?? "";
            foreach (var known in new[] { "cold-diskcache", "cold-app", "cold-os", "warm" })
                if (cell.EndsWith("-" + known, StringComparison.OrdinalIgnoreCase)) { cond = known; break; }
        }

        if (workers is null && file.DiagFlags.TryGetValue("PHOTOREVIEW_DIAG_PRELOAD_WORKERS", out var wv)
            && int.TryParse(wv, out var wp)) workers = wp;

        var processPath = Path.Combine(dir, "process.json");
        if (File.Exists(processPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(processPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("gcTimePercent", out var g) && g.TryGetDouble(out var gd)) gcPct = gd;
                // D06 writes gcPauseDeltaMs + elapsedMs per iteration rather than a percentage.
                else if (root.TryGetProperty("gcPauseDeltaMs", out var gp) && gp.TryGetDouble(out var gpd)
                    && root.TryGetProperty("elapsedMs", out var el) && el.TryGetDouble(out var eld) && eld > 0)
                    gcPct = gpd / eld * 100.0;
                if (root.TryGetProperty("frameTimeP95Ms", out var f) && f.TryGetDouble(out var fd)) frameP95 = fd;
            }
            catch (JsonException) { /* tolerate a hand-edited or partial process.json */ }
        }

        return new RunFileMeta(scenario, mode, cond, workers, gcPct, frameP95);
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public static class PerfStats
{
    /// <summary>Nearest-rank percentile (D11 spec item 6): P-th percentile of a value already
    /// sorted ascending is the element at 1-based rank ceil(P/100 * N).</summary>
    public static double NearestRank(IReadOnlyList<double> sortedAscending, double percentile)
    {
        if (sortedAscending.Count == 0) return double.NaN;
        var rank = (int)Math.Ceiling(percentile / 100.0 * sortedAscending.Count);
        rank = Math.Clamp(rank, 1, sortedAscending.Count);
        return sortedAscending[rank - 1];
    }

    /// <summary>Average per-phase share (phase ms / finalVisual ms) across the slowest 10% of the
    /// given complete navigations, ordered by finalVisual (plan mục 8 / D11 spec item 6). Only
    /// navs with a finalVisual are eligible; callers must already have filtered out Incomplete.</summary>
    public static Dictionary<string, double> SlowestDecilePhaseShare(IReadOnlyList<NavRecord> completeNavs)
    {
        var eligible = completeNavs.Where(n => n.FinalVisualMs is > 0).OrderBy(n => n.FinalVisualMs).ToList();
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (eligible.Count == 0) return result;

        var sliceCount = Math.Max(1, (int)Math.Ceiling(eligible.Count * 0.10));
        var slow = eligible.Skip(eligible.Count - sliceCount).ToList();

        foreach (var (name, get) in PhaseGetters())
        {
            // Only report a phase that at least one nav in the slice actually measured — a phase
            // absent for every nav (e.g. t_disk when nothing was a disk-cache hit) is omitted
            // rather than reported as a misleading 0% share.
            if (!slow.Any(n => get(n).HasValue)) continue;
            var ratios = slow.Select(n => (get(n) ?? 0.0) / n.FinalVisualMs!.Value).ToList();
            result[name] = ratios.Average();
        }
        return result;
    }

    public static IEnumerable<(string Name, Func<NavRecord, double?> Get)> PhaseGetters()
    {
        yield return ("t_input", n => n.TInputMs);
        yield return ("t_pre", n => n.TPreMs);
        yield return ("t_stat", n => n.TStatMs);
        yield return ("t_thumb", n => n.TThumbMs);
        yield return ("t_join", n => n.TJoinMs);
        yield return ("t_disk", n => n.TDiskMs);
        yield return ("t_open", n => n.TOpenMs);
        yield return ("t_read", n => n.TReadMs);
        yield return ("t_decode", n => n.TDecodeMs);
        yield return ("t_verify", n => n.TVerifyMs);
        yield return ("t_assign", n => n.TAssignMs);
        yield return ("t_render", n => n.TRenderMs);
        yield return ("t_renderFrame", n => n.TRenderFrameMs);
        // t_post (preload kick, compare, hash, dims, session) runs after the frame is presented
        // (plan mục 3), so it is not a share of key→present. Post-work cost stays available on
        // NavRecord.PostMs for a separate report section (not yet in summary.md).
    }
}

/// <summary>(scenario, mode, cond, preload worker count) grouping key used to bucket navigations for the summary table
/// (D11 spec item 6).</summary>
public readonly record struct GroupKey(string Scenario, string Mode, string Cond, int? PreloadWorkers = null)
{
    public override string ToString() => $"{Scenario}/{Mode}/{Cond}/workers={PreloadWorkers?.ToString(CultureInfo.InvariantCulture) ?? "?"}";
}

public sealed class GroupSummary
{
    public required GroupKey Key { get; init; }
    public int Count { get; set; }
    public int Incomplete { get; set; }
    public bool LowSampleWarning => Count < 20;

    public double FirstP50 { get; set; } = double.NaN;
    public double FirstP95 { get; set; } = double.NaN;
    public double FirstMax { get; set; } = double.NaN;
    public double FinalP50 { get; set; } = double.NaN;
    public double FinalP95 { get; set; } = double.NaN;
    public double FinalMax { get; set; } = double.NaN;

    /// <summary>
    /// perf(render-metric): P50/P95/max of t_renderFrame (assign -> second Rendering tick) across
    /// complete navs that recorded one -- the frame-accurate proxy for "time to see the new
    /// image", alongside (not instead of) FirstP50/FinalP50 above. NaN when no nav in this group
    /// carries a RenderedFrame event (e.g. an older perf-*.csv).
    /// </summary>
    public double RenderedFrameP50 { get; set; } = double.NaN;
    public double RenderedFrameP95 { get; set; } = double.NaN;
    public double RenderedFrameMax { get; set; } = double.NaN;

    public Dictionary<string, int> KindCounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> SlowestPhaseShare { get; } = new(StringComparer.Ordinal);
    public double HitRate { get; set; } = double.NaN;

    public int? PreloadWorkers { get; set; }
    public double? GcTimePercent { get; set; }
    public double? FrameTimeP95Ms { get; set; }

    public List<PreloadItemRow> PreloadItems { get; } = [];
    public int PreloadPausedCount { get; set; }
    public int PreloadCancelCount { get; set; }
    public List<DispatcherLongOpRow> DispatcherLongOps { get; } = [];
    public int DispatcherLongOpsBeforeStartCount { get; set; }
    public List<FolderGenSummary> FolderGens { get; } = [];

    public List<NavRecord> Navs { get; } = [];

    public static GroupSummary Build(GroupKey key, List<NavRecord> navs)
    {
        var summary = new GroupSummary { Key = key };
        summary.Navs.AddRange(navs);
        summary.Count = navs.Count;
        summary.Incomplete = navs.Count(n => n.Incomplete);

        var complete = navs.Where(n => !n.Incomplete).ToList();
        var firsts = complete.Where(n => n.FirstVisualMs is not null).Select(n => n.FirstVisualMs!.Value).OrderBy(v => v).ToList();
        var finals = complete.Where(n => n.FinalVisualMs is not null).Select(n => n.FinalVisualMs!.Value).OrderBy(v => v).ToList();

        if (firsts.Count > 0)
        {
            summary.FirstP50 = PerfStats.NearestRank(firsts, 50);
            summary.FirstP95 = PerfStats.NearestRank(firsts, 95);
            summary.FirstMax = firsts[^1];
        }
        if (finals.Count > 0)
        {
            summary.FinalP50 = PerfStats.NearestRank(finals, 50);
            summary.FinalP95 = PerfStats.NearestRank(finals, 95);
            summary.FinalMax = finals[^1];
        }

        var renderedFrames = complete.Where(n => n.TRenderFrameMs is not null).Select(n => n.TRenderFrameMs!.Value).OrderBy(v => v).ToList();
        if (renderedFrames.Count > 0)
        {
            summary.RenderedFrameP50 = PerfStats.NearestRank(renderedFrames, 50);
            summary.RenderedFrameP95 = PerfStats.NearestRank(renderedFrames, 95);
            summary.RenderedFrameMax = renderedFrames[^1];
        }

        foreach (var kind in complete.Select(n => n.Kind))
            summary.KindCounts[kind] = summary.KindCounts.GetValueOrDefault(kind) + 1;

        foreach (var (name, value) in PerfStats.SlowestDecilePhaseShare(complete))
            summary.SlowestPhaseShare[name] = value;

        if (complete.Count > 0)
        {
            var ramLike = complete.Count(n => n.Kind.Contains("RamHit", StringComparison.Ordinal));
            summary.HitRate = ramLike / (double)complete.Count;
        }

        return summary;
    }
}
