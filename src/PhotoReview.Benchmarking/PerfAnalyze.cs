using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PhotoReview.Benchmarking.PerfAnalysis;

/// <summary>
/// D11 `--perf-analyze &lt;runDir&gt; [--rules &lt;json&gt;]`: reads every perf-*.csv under runDir
/// (recursively), reassembles navigations, aggregates preload/dispatcher/folder data, applies the
/// R-* decision rules (PERF-DIAGNOSIS-PLAN.md mục 8), and writes summary.md + summary.json into
/// runDir. See docs/refactoring/PERF-DIAGNOSIS-TASKS.md, mục D11, for the full spec.
/// </summary>
public static class PerfAnalyze
{
    public sealed class GroupResult
    {
        public required GroupSummary Summary { get; init; }
        public required List<RuleResult> Rules { get; init; }
    }

    public sealed class AnalysisResult
    {
        public List<GroupResult> Groups { get; } = [];
        public string SummaryMdPath { get; set; } = "";
        public string SummaryJsonPath { get; set; } = "";
        public int CsvFileCount { get; set; }
    }

    public static Task<AnalysisResult> RunAsync(string runDir, string? rulesPath)
    {
        if (!Directory.Exists(runDir)) throw new DirectoryNotFoundException(runDir);

        // run-matrix.ps1 (D06) writes a throwaway "warmup" run per warm cell; it must not be measured.
        var csvFiles = Directory.EnumerateFiles(runDir, "perf-*.csv", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(runDir, p).Split(Path.DirectorySeparatorChar)
                .Any(part => string.Equals(part, "warmup", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (csvFiles.Count == 0)
            throw new InvalidOperationException($"Không tìm thấy file perf-*.csv nào trong {runDir}");

        var rules = !string.IsNullOrEmpty(rulesPath) ? RulesConfig.Load(rulesPath) : RulesConfig.Default();

        var groupNavs = new Dictionary<GroupKey, List<NavRecord>>();
        var groupPreload = new Dictionary<GroupKey, List<PreloadItemRow>>();
        var groupPaused = new Dictionary<GroupKey, int>();
        var groupCancel = new Dictionary<GroupKey, int>();
        var groupDispatcher = new Dictionary<GroupKey, List<DispatcherLongOpRow>>();
        var groupDispatcherBeforeStart = new Dictionary<GroupKey, int>();
        var groupFolder = new Dictionary<GroupKey, List<FolderGenSummary>>();
        var groupMetas = new Dictionary<GroupKey, List<RunFileMeta>>();

        foreach (var path in csvFiles)
        {
            var file = PerfCsvReader.Read(path);
            var analysis = PerfAnalyzeNavBuilder.Build(file);
            var meta = RunFileMeta.Load(file);

            // Each perf-*.csv is normally one run (D06), so its mode is whichever ShowStart.mode
            // dominates its own navigations (majority vote guards against a stray/incomplete nav).
            var mode = analysis.Navs.Count > 0
                ? analysis.Navs
                    .GroupBy(n => string.IsNullOrEmpty(n.Mode) ? "unknown" : n.Mode, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count())
                    .First().Key
                : (string.IsNullOrEmpty(meta.Mode) ? "unknown" : meta.Mode);
            var key = new GroupKey(meta.Scenario, mode, meta.Cond);

            Add(groupNavs, key).AddRange(analysis.Navs);
            Add(groupPreload, key).AddRange(analysis.PreloadItems);
            groupPaused[key] = groupPaused.GetValueOrDefault(key) + analysis.PreloadPausedCount;
            groupCancel[key] = groupCancel.GetValueOrDefault(key) + analysis.PreloadCancelCount;
            Add(groupDispatcher, key).AddRange(analysis.DispatcherLongOps);
            groupDispatcherBeforeStart[key] = groupDispatcherBeforeStart.GetValueOrDefault(key) + analysis.DispatcherLongOpsBeforeStartCount;
            Add(groupFolder, key).AddRange(analysis.FolderGens);
            Add(groupMetas, key).Add(meta);
        }

        var summaries = groupNavs.Keys
            .Select(key => GroupSummary.Build(key, groupNavs[key]))
            .ToList();

        foreach (var s in summaries)
        {
            var metas = groupMetas[s.Key];
            s.PreloadWorkers = metas.Select(m => m.PreloadWorkers).FirstOrDefault(w => w.HasValue);
            s.GcTimePercent = AverageOrNull(metas.Select(m => m.GcTimePercent));
            s.FrameTimeP95Ms = AverageOrNull(metas.Select(m => m.FrameTimeP95Ms));
            s.PreloadItems.AddRange(groupPreload.GetValueOrDefault(s.Key, []));
            s.PreloadPausedCount = groupPaused.GetValueOrDefault(s.Key);
            s.PreloadCancelCount = groupCancel.GetValueOrDefault(s.Key);
            s.DispatcherLongOps.AddRange(groupDispatcher.GetValueOrDefault(s.Key, []));
            s.DispatcherLongOpsBeforeStartCount = groupDispatcherBeforeStart.GetValueOrDefault(s.Key);
            s.FolderGens.AddRange(groupFolder.GetValueOrDefault(s.Key, []));
        }

        var results = new List<GroupResult>();
        foreach (var s in summaries)
        {
            results.Add(new GroupResult { Summary = s, Rules = EvaluateAllRules(rules, s, summaries) });
        }

        var result = new AnalysisResult { CsvFileCount = csvFiles.Count };
        result.Groups.AddRange(results.OrderBy(r => r.Summary.Key.ToString(), StringComparer.Ordinal));
        result.SummaryMdPath = Path.Combine(runDir, "summary.md");
        result.SummaryJsonPath = Path.Combine(runDir, "summary.json");
        PerfAnalyzeReport.WriteMarkdown(result.SummaryMdPath, result);
        PerfAnalyzeReport.WriteJson(result.SummaryJsonPath, result);

        return Task.FromResult(result);
    }

    /// <summary>Evaluates all nine R-* rules for one group. R-CONT is the only cross-group rule: it
    /// compares this group against sibling groups sharing (scenario, mode) but a different
    /// PreloadWorkers count (plan mục 8).</summary>
    private static List<RuleResult> EvaluateAllRules(RulesConfig rules, GroupSummary s, List<GroupSummary> allGroups)
    {
        var complete = s.Navs.Where(n => !n.Incomplete).ToList();
        var sourceMissNavs = complete.Where(n => n.Kind.Contains("SourceMiss", StringComparison.Ordinal)).ToList();
        var decodeNavs = complete.Where(n => n.Kind.Contains("SourceMiss", StringComparison.Ordinal)
            || n.Kind.Contains("InflightJoin", StringComparison.Ordinal)).ToList();
        var ramHitNavs = complete.Where(n => n.Kind.Contains("RamHit", StringComparison.Ordinal)).ToList();
        var diskCacheHitNavs = complete.Where(n => n.Kind.Contains("DiskCacheHit", StringComparison.Ordinal)).ToList();

        var ramHitFinals = ramHitNavs.Where(n => n.FinalVisualMs is not null).Select(n => n.FinalVisualMs!.Value).OrderBy(v => v).ToList();
        var ramHitFinalP95 = ramHitFinals.Count > 0 ? PerfStats.NearestRank(ramHitFinals, 95) : double.NaN;
        var nonRamHitSharePct = complete.Count > 0 ? 100.0 * (complete.Count - ramHitNavs.Count) / complete.Count : double.NaN;
        var isBurst = s.Key.Scenario.Contains("S3", StringComparison.OrdinalIgnoreCase) || s.Key.Scenario.Contains("S4", StringComparison.OrdinalIgnoreCase);

        var inputs = complete.Where(n => n.TInputMs is not null).Select(n => n.TInputMs!.Value).OrderBy(v => v).ToList();
        double? tInputP95 = inputs.Count > 0 ? PerfStats.NearestRank(inputs, 95) : null;

        var dispatcherThreshold = rules.Get("R-THREAD", "dispatcherLongOpMs", 16);
        var dispatcherCount = s.DispatcherLongOps.Count(d => d.Ms > dispatcherThreshold);

        // R-CONT: find a sibling group (same scenario+mode, any cond) with the smallest and the
        // largest known PreloadWorkers value to compare against.
        var siblings = allGroups.Where(g => g.Key.Scenario == s.Key.Scenario && g.Key.Mode == s.Key.Mode && g.PreloadWorkers.HasValue).ToList();
        GroupSummary? lowGroup = siblings.Count > 0 ? siblings.OrderBy(g => g.PreloadWorkers).First() : null;
        GroupSummary? highGroup = siblings.Count > 0 ? siblings.OrderByDescending(g => g.PreloadWorkers).First() : null;
        double? decodeLow = lowGroup is null ? null : AverageDecodeMs(lowGroup);
        double? decodeHigh = highGroup is null ? null : AverageDecodeMs(highGroup);

        return
        [
            PerfRules.EvaluateRIo(rules, sourceMissNavs),
            PerfRules.EvaluateRDec(rules, decodeNavs),
            PerfRules.EvaluateRUi(rules, ramHitNavs, ramHitFinalP95, s.FrameTimeP95Ms),
            PerfRules.EvaluateRPre(rules, ramHitFinalP95, nonRamHitSharePct, isBurst),
            PerfRules.EvaluateRCont(rules, decodeLow, decodeHigh, lowGroup?.PreloadWorkers, highGroup?.PreloadWorkers),
            PerfRules.EvaluateRThread(rules, dispatcherCount, tInputP95),
            PerfRules.EvaluateRGc(rules, s.GcTimePercent),
            PerfRules.EvaluateRDisk(rules, diskCacheHitNavs, sourceMissNavs),
            PerfRules.EvaluateRFolder(rules, s.FolderGens),
        ];
    }

    private static double? AverageDecodeMs(GroupSummary g)
    {
        var values = g.Navs.Where(n => !n.Incomplete
                && (n.Kind.Contains("SourceMiss", StringComparison.Ordinal) || n.Kind.Contains("InflightJoin", StringComparison.Ordinal))
                && n.TDecodeMs.HasValue)
            .Select(n => n.TDecodeMs!.Value).ToList();
        return values.Count > 0 ? values.Average() : null;
    }

    private static List<T> Add<TKey, T>(Dictionary<TKey, List<T>> dict, TKey key) where TKey : notnull
    {
        if (!dict.TryGetValue(key, out var list)) dict[key] = list = [];
        return list;
    }

    private static double? AverageOrNull(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return list.Count > 0 ? list.Average() : null;
    }
}
