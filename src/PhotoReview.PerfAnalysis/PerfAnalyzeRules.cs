using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PhotoReview.PerfAnalysis;

/// <summary>Loads tools/diag/rules.json (or an override path): a nested {rule: {key: number}}
/// document of thresholds, so R-* thresholds can be tuned without rebuilding (D11 spec item 4 /
/// PERF-DIAGNOSIS-PLAN.md má»¥c 8).</summary>
public sealed class RulesConfig
{
    private readonly Dictionary<string, Dictionary<string, double>> _data;

    private RulesConfig(Dictionary<string, Dictionary<string, double>> data) => _data = data;

    public double Get(string rule, string key, double @default) =>
        _data.TryGetValue(rule, out var section) && section.TryGetValue(key, out var v) ? v : @default;

    public static RulesConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(json)
                  ?? new Dictionary<string, Dictionary<string, double>>();
        return new RulesConfig(doc);
    }

    public static RulesConfig Default() => new(new Dictionary<string, Dictionary<string, double>>());
}

/// <summary>Result of evaluating one R-* rule against one (scenario, mode, cond) group.
/// Triggered is null when there isn't enough data to decide (N/A) â€” plan má»¥c 8 explicitly allows
/// this for R-IO (no file-open counter yet) and R-CONT (needs runs at different worker counts).</summary>
public sealed record RuleResult(string Rule, bool? Triggered, string Evidence, string? Note = null);

/// <summary>
/// Pure, independently-testable evaluators for the nine decision rules in
/// docs/refactoring/PERF-DIAGNOSIS-PLAN.md má»¥c 8. Each takes plain aggregates (not the full
/// pipeline) so PerfAnalyzeTests can build a group's numbers by hand and check both the
/// triggered and not-triggered branch.
/// </summary>
public static class PerfRules
{
    /// <summary>R-IO: in the SourceMiss group, (t_open+t_read) share of finalVisual is high, or the
    /// source is opened too many times per image. t_open is never emitted by the current app (no
    /// call site yet) and t_read only exists under PHOTOREVIEW_DIAG_PREREAD, so this is N/A when
    /// neither is present in the data (plan má»¥c 8 / D11 spec item 7).</summary>
    public static RuleResult EvaluateRIo(RulesConfig cfg, IReadOnlyList<NavRecord> sourceMissNavs)
    {
        var sharePct = cfg.Get("R-IO", "sourceMissIoSharePct", 40);
        var eligible = sourceMissNavs.Where(n => n.FinalVisualMs is > 0 && (n.TOpenMs.HasValue || n.TReadMs.HasValue)).ToList();
        if (eligible.Count == 0)
        {
            return new RuleResult("R-IO", null,
                $"0/{sourceMissNavs.Count} nav SourceMiss cÃ³ t_open/t_read",
                "ChÆ°a cÃ³ bá»™ Ä‘áº¿m sá»‘ láº§n má»Ÿ file nguá»“n (SourceOpen chÆ°a Ä‘Æ°á»£c gá»i); chá»‰ cÃ³ khi báº­t PHOTOREVIEW_DIAG_PREREAD. DÃ¹ng sá»‘ liá»‡u Procmon D02 Ä‘á»ƒ suy ra sá»‘ láº§n má»Ÿ file.");
        }
        var avgShare = eligible.Average(n => ((n.TOpenMs ?? 0) + (n.TReadMs ?? 0)) / n.FinalVisualMs!.Value) * 100.0;
        return new RuleResult("R-IO", avgShare >= sharePct,
            $"avg((t_open+t_read)/finalVisual)={avgShare:F1}% trÃªn {eligible.Count} nav SourceMiss (ngÆ°á»¡ng {sharePct}%)");
    }

    /// <summary>R-DEC: in the SourceMiss/InflightJoin group, t_decode share of finalVisual is high.</summary>
    public static RuleResult EvaluateRDec(RulesConfig cfg, IReadOnlyList<NavRecord> decodeNavs)
    {
        var sharePct = cfg.Get("R-DEC", "decodeSharePct", 40);
        var eligible = decodeNavs.Where(n => n.FinalVisualMs is > 0 && n.TDecodeMs.HasValue).ToList();
        if (eligible.Count == 0)
            return new RuleResult("R-DEC", null, "0 nav SourceMiss/InflightJoin cÃ³ t_decode", "KhÃ´ng Ä‘á»§ dá»¯ liá»‡u decode.");
        var avgShare = eligible.Average(n => n.TDecodeMs!.Value / n.FinalVisualMs!.Value) * 100.0;
        return new RuleResult("R-DEC", avgShare >= sharePct,
            $"avg(t_decode/finalVisual)={avgShare:F1}% trÃªn {eligible.Count} nav (ngÆ°á»¡ng {sharePct}%)");
    }

    /// <summary>R-UI: in the RamHit group, (t_input+t_assign+t_render) share is high, or RamHit
    /// P95(finalVisual) is high, or S6 frame time P95 is high.</summary>
    public static RuleResult EvaluateRUi(RulesConfig cfg, IReadOnlyList<NavRecord> ramHitNavs, double ramHitFinalP95, double? frameTimeP95Ms)
    {
        var sharePct = cfg.Get("R-UI", "ramHitUiSharePct", 40);
        var ramHitP95Threshold = cfg.Get("R-UI", "ramHitP95Ms", 50);
        var frameThreshold = cfg.Get("R-UI", "frameTimeP95Ms", 33);

        var eligible = ramHitNavs.Where(n => n.FinalVisualMs is > 0).ToList();
        double? avgShare = eligible.Count == 0
            ? null
            : eligible.Average(n => ((n.TInputMs ?? 0) + (n.TAssignMs ?? 0) + (n.TRenderMs ?? 0)) / n.FinalVisualMs!.Value) * 100.0;

        var byShare = avgShare is { } s && s >= sharePct;
        var byRamHitP95 = !double.IsNaN(ramHitFinalP95) && ramHitFinalP95 > ramHitP95Threshold;
        var byFrameTime = frameTimeP95Ms is { } ft && ft > frameThreshold;

        var evidence = $"avg((t_input+t_assign+t_render)/finalVisual)={(avgShare is { } sv ? $"{sv:F1}%" : "N/A")} " +
                        $"(ngÆ°á»¡ng {sharePct}%); RamHit finalVisual P95={ramHitFinalP95:F1}ms (ngÆ°á»¡ng {ramHitP95Threshold}ms); " +
                        $"frameTime P95={(frameTimeP95Ms is { } f ? $"{f:F1}ms" : "N/A")} (ngÆ°á»¡ng {frameThreshold}ms)";

        if (avgShare is null && double.IsNaN(ramHitFinalP95) && frameTimeP95Ms is null)
            return new RuleResult("R-UI", null, evidence, "KhÃ´ng cÃ³ nav RamHit hoáº·c dá»¯ liá»‡u frame time trong nhÃ³m nÃ y.");

        return new RuleResult("R-UI", byShare || byRamHitP95 || byFrameTime, evidence);
    }

    /// <summary>R-PRE: RamHit is fast but the RamHit-only share of navigations is too low (cache
    /// coverage gap rather than a raw speed problem). The non-RamHit threshold differs for
    /// burst-navigation scenarios (S3/S4) vs. paced ones (S2).</summary>
    public static RuleResult EvaluateRPre(RulesConfig cfg, double ramHitFinalP95, double nonRamHitSharePct, bool isBurstScenario)
    {
        var ramHitMax = cfg.Get("R-PRE", "ramHitP95MaxMs", 50);
        var thresholdKey = isBurstScenario ? "nonRamHitShareS3S4Pct" : "nonRamHitShareS2Pct";
        var threshold = cfg.Get("R-PRE", thresholdKey, isBurstScenario ? 30 : 10);

        if (double.IsNaN(ramHitFinalP95))
            return new RuleResult("R-PRE", null, "KhÃ´ng cÃ³ nav RamHit trong nhÃ³m nÃ y.");

        var triggered = ramHitFinalP95 <= ramHitMax && nonRamHitSharePct >= threshold;
        return new RuleResult("R-PRE", triggered,
            $"RamHit finalVisual P95={ramHitFinalP95:F1}ms (ngÆ°á»¡ng â‰¤{ramHitMax}ms); " +
            $"tá»· lá»‡ khÃ´ng pháº£i RamHit={nonRamHitSharePct:F1}% (ngÆ°á»¡ng â‰¥{threshold}% cho {(isBurstScenario ? "S3/S4" : "S2")})");
    }

    /// <summary>R-CONT: the viewed image's decode time grows substantially between a low- and a
    /// high-preload-worker run of the same scenario/mode â€” preload contends with the foreground
    /// decode. N/A when we don't have both a low- and a high-worker run to compare (plan má»¥c 8).</summary>
    public static RuleResult EvaluateRCont(RulesConfig cfg, double? decodeMsLowWorkers, double? decodeMsHighWorkers, int? lowWorkers, int? highWorkers)
    {
        var factor = cfg.Get("R-CONT", "decodeSlowdownFactor", 1.3);
        if (decodeMsLowWorkers is null || decodeMsHighWorkers is null || lowWorkers == highWorkers)
        {
            return new RuleResult("R-CONT", null, "Thiáº¿u cáº·p run PHOTOREVIEW_DIAG_PRELOAD_WORKERS khÃ¡c nhau Ä‘á»ƒ so sÃ¡nh.",
                "Cáº§n Ã­t nháº¥t 2 run cÃ¹ng scenario/mode vá»›i PHOTOREVIEW_DIAG_PRELOAD_WORKERS khÃ¡c nhau (D07).");
        }
        var ratio = decodeMsHighWorkers.Value / Math.Max(decodeMsLowWorkers.Value, 0.0001);
        return new RuleResult("R-CONT", ratio >= factor,
            $"t_decode(workers={highWorkers})={decodeMsHighWorkers:F1}ms so t_decode(workers={lowWorkers})={decodeMsLowWorkers:F1}ms, tá»· lá»‡={ratio:F2} (ngÆ°á»¡ng {factor})");
    }

    /// <summary>R-THREAD: repeated DispatcherLongOp (>16ms) during S2/S3, or high t_input P95.</summary>
    public static RuleResult EvaluateRThread(RulesConfig cfg, int dispatcherLongOpCount, double? tInputP95Ms)
    {
        var msThreshold = cfg.Get("R-THREAD", "dispatcherLongOpMs", 16);
        var inputThreshold = cfg.Get("R-THREAD", "inputP95Ms", 16);
        var byDispatcher = dispatcherLongOpCount > 0;
        var byInput = tInputP95Ms is { } t && t > inputThreshold;
        var evidence = $"DispatcherLongOp>{msThreshold}ms count={dispatcherLongOpCount}; t_input P95={(tInputP95Ms is { } v ? $"{v:F1}ms" : "N/A")} (ngÆ°á»¡ng {inputThreshold}ms)";
        if (dispatcherLongOpCount == 0 && tInputP95Ms is null)
            return new RuleResult("R-THREAD", null, evidence, "KhÃ´ng cÃ³ DispatcherLongOp hay t_input trong nhÃ³m nÃ y.");
        return new RuleResult("R-THREAD", byDispatcher || byInput, evidence);
    }

    /// <summary>R-GC: % time in GC is high (needs process.json from D06/dotnet-counters; N/A otherwise).</summary>
    public static RuleResult EvaluateRGc(RulesConfig cfg, double? gcTimePercent)
    {
        var threshold = cfg.Get("R-GC", "gcTimePct", 10);
        if (gcTimePercent is null)
            return new RuleResult("R-GC", null, "KhÃ´ng cÃ³ process.json/% time in GC cho nhÃ³m nÃ y.", "Cáº§n dotnet-counters (D09) hoáº·c process.json (D06).");
        return new RuleResult("R-GC", gcTimePercent.Value >= threshold, $"% time in GC={gcTimePercent:F1}% (ngÆ°á»¡ng {threshold}%)");
    }

    /// <summary>R-DISK: reading the on-disk preview cache is no faster than re-reading and
    /// re-decoding the source (aggregate approximation: avg t_disk of DiskCacheHit navs vs
    /// avg (t_read+t_decode) of SourceMiss navs in the same group).</summary>
    public static RuleResult EvaluateRDisk(RulesConfig cfg, IReadOnlyList<NavRecord> diskCacheHitNavs, IReadOnlyList<NavRecord> sourceMissNavs)
    {
        var factor = cfg.Get("R-DISK", "diskVsReadDecodeFactor", 1.0);
        var diskTimes = diskCacheHitNavs.Where(n => n.TDiskMs.HasValue).Select(n => n.TDiskMs!.Value).ToList();
        var readDecodeTimes = sourceMissNavs.Where(n => n.TDecodeMs.HasValue)
            .Select(n => (n.TReadMs ?? 0) + n.TDecodeMs!.Value).ToList();
        if (diskTimes.Count == 0 || readDecodeTimes.Count == 0)
            return new RuleResult("R-DISK", null, $"diskCacheHit navs={diskTimes.Count}, sourceMiss navs={readDecodeTimes.Count}",
                "Cáº§n Ã­t nháº¥t má»™t nav DiskCacheHit vÃ  má»™t nav SourceMiss trong cÃ¹ng nhÃ³m.");
        var avgDisk = diskTimes.Average();
        var avgReadDecode = readDecodeTimes.Average();
        return new RuleResult("R-DISK", avgDisk >= factor * avgReadDecode,
            $"avg(t_disk)={avgDisk:F1}ms so avg(t_read+t_decode)={avgReadDecode:F1}ms (ngÆ°á»¡ng há»‡ sá»‘ {factor})");
    }

    /// <summary>R-FOLDER: opening a folder (T0â†’T2, first image presented) takes too long.</summary>
    public static RuleResult EvaluateRFolder(RulesConfig cfg, IReadOnlyList<FolderGenSummary> gens)
    {
        var thresholdMs = cfg.Get("R-FOLDER", "folderT0ToT2Ms", 1000);
        var withT2 = gens.Where(g => g.T2Ms.HasValue).ToList();
        if (withT2.Count == 0)
            return new RuleResult("R-FOLDER", null, "KhÃ´ng cÃ³ Folder gen nÃ o cÃ³ T2 (áº£nh Ä‘áº§u tiÃªn present).");
        var worst = withT2.OrderByDescending(g => g.T2Ms).First();
        return new RuleResult("R-FOLDER", worst.T2Ms > thresholdMs,
            $"T0->T2 tá»‡ nháº¥t={worst.T2Ms:F1}ms (gen={worst.Gen}, T1 catalogReady={worst.T1CatalogReadyMs:F1}ms, T3 {worst.T3Phase}={worst.T3Ms:F1}ms; ngÆ°á»¡ng {thresholdMs}ms)");
    }
}

