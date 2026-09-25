using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PhotoReview.PerfAnalysis;

/// <summary>One navigation (ShowImageAsync token) reassembled from its perf events (D11, plan mục 3).</summary>
public sealed class NavRecord
{
    public long Nav { get; init; }

    public string Mode { get; set; } = "";
    public int Index { get; set; }

    public double? TInputMs { get; set; }
    public double? TPreMs { get; set; }
    public double? TStatMs { get; set; }
    public string LookupResult { get; set; } = "";
    public double? TThumbMs { get; set; }
    public string ThumbSource { get; set; } = "";
    public bool HasThumbnail { get; set; }
    public double? TJoinMs { get; set; }
    public double? TDiskMs { get; set; }
    public double? TOpenMs { get; set; }
    public double? TReadMs { get; set; }
    public double? TDecodeMs { get; set; }
    public bool DecodeDownscaled { get; set; }
    public bool DecodeFallback { get; set; }
    public double? TVerifyMs { get; set; }
    public double? TAssignMs { get; set; }
    public double? TRenderFirstMs { get; set; }
    public double? TRenderMs { get; set; }

    /// <summary>
    /// perf(render-metric): time from the Source assign to the SECOND CompositionTarget.Rendering
    /// tick after it (the RenderedFrame event) -- i.e. the frame containing the new image has
    /// actually finished rendering, unlike <see cref="TRenderMs"/> which only measures the
    /// dispatcher/vsync phase before layout/render run. Null for navs from an older perf-*.csv
    /// that predates this event, or when the token was superseded before the second tick arrived.
    /// </summary>
    public double? TRenderFrameMs { get; set; }
    public Dictionary<string, double> PostMs { get; } = new(StringComparer.Ordinal);

    public double? FirstVisualMs { get; set; }
    public double? FinalVisualMs { get; set; }
    public string FinalPresentedKind { get; set; } = "";

    /// <summary>True until a Presented(kind=final|compare) row is matched for this nav — e.g. the
    /// token was superseded by a newer navigation before it ever rendered. Excluded from
    /// percentiles but still counted (D11 spec item 2).</summary>
    public bool Incomplete { get; set; } = true;

    /// <summary>RamHit | InflightJoin | DiskCacheHit | SourceMiss, optionally prefixed
    /// "Thumbnail+" when a thumbnail was shown first (plan mục 3).</summary>
    public string Kind { get; set; } = "Unknown";
}

public sealed record PreloadItemRow(int Slot, string PathId, double QueueWaitMs, string Kind, double Ms);
public sealed record DispatcherLongOpRow(double Ms, string Priority, string Name);

public sealed class FolderGenSummary
{
    public long Gen { get; init; }
    public double? T1CatalogReadyMs { get; set; }
    public double? T2Ms { get; set; }
    public double? T3Ms { get; set; }
    public string T3Phase { get; set; } = "";
}

/// <summary>Everything reassembled from one perf-*.csv file's rows (D11 spec items 1-5).</summary>
public sealed class PerfFileAnalysis
{
    public List<NavRecord> Navs { get; } = [];
    public List<PreloadItemRow> PreloadItems { get; } = [];
    public int PreloadPausedCount { get; set; }
    public int PreloadCancelCount { get; set; }
    public List<FolderGenSummary> FolderGens { get; } = [];
    public List<DispatcherLongOpRow> DispatcherLongOps { get; } = [];

    /// <summary>DispatcherLongOp rows dropped because they happened before the first ShowStart or
    /// Folder(start) in this file — driver/window setup (e.g. --perf-session's own STA harness
    /// creating the WPF Window, ~700ms, per D06) rather than app UI-thread contention during the
    /// scenario itself (D11 coordinator note, 2026-09-17).</summary>
    public int DispatcherLongOpsBeforeStartCount { get; set; }

    /// <summary>
    /// perf(startup): Startup(phase, msSinceProcessStart) milestones of this process (first row per
    /// phase), plus the derived <c>firstPresented</c>: the first Presented event mapped onto the same
    /// process-start timeline through a Startup row's qpcTicks. Empty for files without Startup rows.
    /// </summary>
    public Dictionary<string, double> Startup { get; } = new(StringComparer.Ordinal);
}

public static class PerfAnalyzeNavBuilder
{
    /// <summary>KeyInput→ShowStart matching window (D11 spec item 2): the nearest KeyInput on the
    /// same thread strictly at or before ShowStart's timestamp, within this many milliseconds.</summary>
    private const double KeyInputMatchWindowMs = 500.0;

    public static PerfFileAnalysis Build(PerfCsvFile file)
    {
        var result = new PerfFileAnalysis();

        var byNav = new Dictionary<long, List<PerfRow>>();
        var keyInputs = new List<PerfRow>();
        var folderRows = new List<PerfRow>();
        var presentedGlobal = new List<PerfRow>();
        PerfRow? startupAnchor = null;

        // D06 driver note (2026-09-17): --perf-session's own STA harness logs a DispatcherLongOp
        // for creating the WPF Window (~700ms) before any scenario step runs. That is driver
        // setup, not app UI-thread contention, so DispatcherLongOp rows before the first
        // ShowStart/Folder(start) in the file are counted separately and excluded from R-THREAD.
        long? firstRelevantQpc = null;
        foreach (var row in file.Rows)
        {
            if (row.Event == "ShowStart" || (row.Event == "Folder" && row.Text == "start"))
            {
                if (firstRelevantQpc is null || row.QpcTicks < firstRelevantQpc) firstRelevantQpc = row.QpcTicks;
            }
        }

        foreach (var row in file.Rows)
        {
            switch (row.Event)
            {
                case "KeyInput":
                    keyInputs.Add(row);
                    continue;
                case "PreloadItem":
                    result.PreloadItems.Add(new PreloadItemRow(
                        (int)(row.ANum ?? 0), row.PathId, row.BNum ?? 0, row.Text, row.CNum ?? 0));
                    continue;
                case "PreloadPaused":
                    result.PreloadPausedCount++;
                    continue;
                case "PreloadCancel":
                    result.PreloadCancelCount++;
                    continue;
                case "DispatcherLongOp":
                {
                    if (firstRelevantQpc is { } threshold && row.QpcTicks < threshold)
                    {
                        result.DispatcherLongOpsBeforeStartCount++;
                        continue;
                    }
                    var parts = row.Text.Split(';', 2);
                    result.DispatcherLongOps.Add(new DispatcherLongOpRow(
                        row.ANum ?? 0, parts.Length > 0 ? parts[0] : "", parts.Length > 1 ? parts[1] : ""));
                    continue;
                }
                case "Folder":
                    folderRows.Add(row);
                    continue;
                case "Startup":
                    if (row.ANum is { } sinceStart && !result.Startup.ContainsKey(row.Text))
                    {
                        result.Startup[row.Text] = sinceStart;
                        startupAnchor ??= row;
                    }
                    continue;
            }

            if (row.Event == "Presented") presentedGlobal.Add(row);

            // nav = -1 is preload work not tied to a live navigation (plan mục 6); nav absent/unparseable
            // rows were already dispatched above. Only nav > 0 belongs to a navigation record.
            if (row.NavId is { } navId && navId > 0)
            {
                if (!byNav.TryGetValue(navId, out var list)) byNav[navId] = list = [];
                list.Add(row);
            }
        }

        BuildFolderSummaries(file, folderRows, presentedGlobal, result.FolderGens);

        if (startupAnchor?.ANum is { } anchorMs && presentedGlobal.Count > 0)
        {
            var firstPresented = presentedGlobal.MinBy(p => p.QpcTicks)!;
            result.Startup["firstPresented"] = anchorMs + file.QpcToMs(firstPresented.QpcTicks - startupAnchor.QpcTicks);
        }

        foreach (var (navId, rows) in byNav)
        {
            result.Navs.Add(BuildNavRecord(file, navId, rows, keyInputs));
        }

        return result;
    }

    private static void BuildFolderSummaries(PerfCsvFile file, List<PerfRow> folderRows,
        List<PerfRow> presentedGlobal, List<FolderGenSummary> output)
    {
        foreach (var group in folderRows.GroupBy(r => r.Nav))
        {
            if (!long.TryParse(group.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gen)) continue;
            var ordered = group.OrderBy(r => r.QpcTicks).ToList();
            var summary = new FolderGenSummary { Gen = gen };

            var catalogReady = ordered.FirstOrDefault(r => r.Text == "catalogReady");
            summary.T1CatalogReadyMs = catalogReady?.ANum;

            var t3 = ordered.FirstOrDefault(r => r.Text is "explorerApplied" or "explorerFallback" or "explorerIgnored");
            summary.T3Ms = t3?.ANum;
            summary.T3Phase = t3?.Text ?? "";

            var start = ordered.FirstOrDefault(r => r.Text == "start");
            if (start is not null)
            {
                var firstPresented = presentedGlobal
                    .Where(p => p.QpcTicks >= start.QpcTicks)
                    .OrderBy(p => p.QpcTicks)
                    .FirstOrDefault();
                if (firstPresented is not null)
                    summary.T2Ms = file.QpcToMs(firstPresented.QpcTicks - start.QpcTicks);
            }

            output.Add(summary);
        }
    }

    private static NavRecord BuildNavRecord(PerfCsvFile file, long navId, List<PerfRow> rows, List<PerfRow> keyInputs)
    {
        var ordered = rows.OrderBy(r => r.QpcTicks).ToList();
        var rec = new NavRecord { Nav = navId };

        var showStart = ordered.FirstOrDefault(r => r.Event == "ShowStart");
        var t0Qpc = showStart?.QpcTicks ?? ordered[0].QpcTicks;
        if (showStart is not null)
        {
            rec.Index = (int)(showStart.ANum ?? 0);
            rec.Mode = showStart.Text;

            var matchedKeyInput = keyInputs
                .Where(k => k.Thread == showStart.Thread && k.QpcTicks <= showStart.QpcTicks)
                .Where(k => file.QpcToMs(showStart.QpcTicks - k.QpcTicks) <= KeyInputMatchWindowMs)
                .OrderByDescending(k => k.QpcTicks)
                .FirstOrDefault();
            if (matchedKeyInput is not null)
            {
                rec.TInputMs = matchedKeyInput.ANum;
                rec.TPreMs = file.QpcToMs(showStart.QpcTicks - matchedKeyInput.QpcTicks);
                t0Qpc = matchedKeyInput.QpcTicks;
            }
        }

        double? pendingRenderMs = null;
        var firstPresentedSeen = false;

        foreach (var row in ordered)
        {
            switch (row.Event)
            {
                case "Stat":
                    rec.TStatMs = row.ANum;
                    break;
                case "Lookup":
                    rec.LookupResult = row.Text;
                    break;
                case "ThumbEnd":
                    // MainWindow emits its own call-site total as source="unknown"; ThumbnailCache
                    // emits a second ThumbEnd with the real source (ram|disk|embedded|none) for the
                    // same nav. D11: prefer the specific one; fall back to "unknown" if that's all
                    // there is. Since ImagePresenter races the thumbnail against the preview (perf:
                    // never block the preview on the thumbnail), ThumbStart/ThumbEnd alone no longer
                    // mean a thumbnail was actually shown -- only a fetch was attempted, and it may
                    // have lost the race or found nothing embedded. HasThumbnail is set below, from
                    // Presented(kind=thumbnail), which fires only when one was really shown first.
                    if (row.Text != "unknown" || rec.TThumbMs is null)
                    {
                        rec.TThumbMs = row.ANum;
                        rec.ThumbSource = row.Text;
                    }
                    break;
                case "JoinEnd":
                    rec.TJoinMs = row.ANum;
                    break;
                case "DiskCacheRead":
                    rec.TDiskMs = row.ANum;
                    break;
                case "SourceOpen":
                    rec.TOpenMs = row.ANum;
                    break;
                case "SourceRead":
                    rec.TReadMs = row.ANum;
                    break;
                case "Decode":
                    rec.TDecodeMs = row.ANum;
                    rec.DecodeDownscaled = row.CNum == 1;
                    rec.DecodeFallback = row.DNum == 1;
                    break;
                case "Verify":
                    rec.TVerifyMs = row.ANum;
                    break;
                case "Assign":
                    rec.TAssignMs = row.ANum;
                    break;
                case "Rendered":
                    pendingRenderMs = row.ANum;
                    break;
                case "Presented":
                    if (row.Text == "thumbnail") rec.HasThumbnail = true;
                    if (!firstPresentedSeen)
                    {
                        firstPresentedSeen = true;
                        rec.FirstVisualMs = file.QpcToMs(row.QpcTicks - t0Qpc);
                        rec.TRenderFirstMs = pendingRenderMs;
                    }
                    if (row.Text is "final" or "compare")
                    {
                        rec.FinalVisualMs = file.QpcToMs(row.QpcTicks - t0Qpc);
                        rec.FinalPresentedKind = row.Text;
                        rec.TRenderMs = pendingRenderMs;
                        rec.Incomplete = false;
                    }
                    pendingRenderMs = null;
                    break;
                case "PostEnd":
                    rec.PostMs[row.Text] = row.ANum ?? 0;
                    break;
                case "RenderedFrame":
                    // Unlike Rendered/Presented above, RenderedFrame is emitted after Presented
                    // (it waits for a second Rendering tick), so it is assigned directly rather
                    // than gated behind a later Presented row -- see WpfPresentationSink.
                    rec.TRenderFrameMs = row.ANum;
                    break;
            }
        }

        // D05: with PHOTOREVIEW_DIAG_PREREAD the Decode event (emitted by DecodeAndCacheAsync around
        // DecodeSource) also covers the in-memory read that SourceRead reports, so split them here.
        if (rec.TReadMs is { } readMs && rec.TDecodeMs is { } decodeMs)
            rec.TDecodeMs = Math.Max(0, decodeMs - readMs);

        rec.Kind = ClassifyKind(rec);
        return rec;
    }

    /// <summary>D11 spec item: RamHit/InflightJoin/DiskCacheHit/SourceMiss classification, with a
    /// Thumbnail+ prefix when a thumbnail was shown first.</summary>
    public static string ClassifyKind(NavRecord n)
    {
        var @base = n.LookupResult switch
        {
            "ramHit" => "RamHit",
            "inflight" => "InflightJoin",
            // "miss": DiskCacheRead present -> disk cache hit; Decode present -> real source miss;
            // neither -> the decode was actually already in flight (kicked by preload) and this
            // navigation only joined it (JoinStart/JoinEnd without its own DiskCacheRead/Decode).
            "miss" => n.TDiskMs.HasValue ? "DiskCacheHit" : n.TDecodeMs.HasValue ? "SourceMiss" : "InflightJoin",
            _ => "Unknown",
        };
        return n.HasThumbnail ? $"Thumbnail+{@base}" : @base;
    }
}
