using PhotoReview.Core.Settings;
using PhotoReview.Core.Model;

namespace PhotoReview.Benchmarking;

public static class BenchmarkProfiles
{
    private const long Reserve = PerformanceOptions.MemoryReserveBytes;
    public static IReadOnlyList<BenchmarkProfile> All { get; } =
    [
        P("instant-review", "Instant Review", "Prioritize the first image", LoadingMode.Fast, 2, 4, 1, false, BenchmarkWorkload.FirstFrame),
        P("fast-sequential", "Fast Sequential", "Continuous Next", LoadingMode.Fast, 8, 32, 8, false, BenchmarkWorkload.Sequential),
        P("fast-balanced", "Fast Balanced", "Balance Next and Previous", LoadingMode.Fast, 4, 16, 8, false, BenchmarkWorkload.WarmNext),
        P("fast-aggressive", "Fast Aggressive", "Deep preload with many workers", LoadingMode.Fast, 16, 64, 16, true, BenchmarkWorkload.Preload),
        P("preview-light", "Preview Light", "Small preview, low RAM", LoadingMode.Preview, 4, 16, 4, false, BenchmarkWorkload.Sequential),
        P("preview-balanced", "Preview Balanced", "Viewport-sized preview", LoadingMode.Preview, 8, 32, 8, false, BenchmarkWorkload.WarmNext),
        P("preview-quality", "Preview Quality", "Large preview, quality first", LoadingMode.Preview, 4, 16, 8, false, BenchmarkWorkload.WarmNext),
        P("preview-high-quality", "Preview High Quality", "Close to Original quality", LoadingMode.Preview, 8, 16, 8, false, BenchmarkWorkload.FirstFrame),
        P("no-preload-baseline", "No Preload Baseline", "Baseline without preload", LoadingMode.Preview, 1, 0, 0, false, BenchmarkWorkload.Sequential),
        P("nearby-only", "Nearby Only", "Preload nearby images only", LoadingMode.Preview, 4, 8, 4, false, BenchmarkWorkload.Preload),
        P("full-folder-warm", "Full Folder Warm", "Warm up the whole folder", LoadingMode.Preview, 8, 32, 8, true, BenchmarkWorkload.Preload),
        P("large-folder-safe", "Large Folder Safe", "Cap RAM for large folders", LoadingMode.Preview, 4, 8, 2, false, BenchmarkWorkload.Preload),
        P("huge-image-safe", "Huge Image Safe", "Lower concurrency for huge images", LoadingMode.Preview, 2, 4, 2, false, BenchmarkWorkload.FirstFrame),
        P("ssd-throughput", "SSD High Throughput", "Parallel reads on SSD", LoadingMode.Preview, 12, 32, 8, false, BenchmarkWorkload.Sequential),
        P("hdd-conservative", "HDD Conservative", "Fewer seeks on HDD", LoadingMode.Preview, 2, 4, 1, false, BenchmarkWorkload.Sequential),
        P("network-safe", "Network Safe", "Careful reads on network drives", LoadingMode.Preview, 2, 4, 1, false, BenchmarkWorkload.FirstFrame),
        P("low-memory", "Low Memory", "Keep a large RAM reserve", LoadingMode.Preview, 2, 4, 2, false, BenchmarkWorkload.Sequential, reserve: 4L * 1024 * 1024 * 1024),
        P("ram-maximizer", "RAM Maximizer", "Use RAM up to the safe limit", LoadingMode.Preview, 12, 64, 16, true, BenchmarkWorkload.Preload),
        P("rapid-key-press", "Rapid Key Press", "Rapid Next presses, no skipping", LoadingMode.Preview, 8, 16, 4, false, BenchmarkWorkload.WarmNext),
        P("random-navigation", "Random Navigation", "Random navigation", LoadingMode.Preview, 6, 16, 8, false, BenchmarkWorkload.Random),
        P("cache-recovery", "Cache Recovery", "Clear and rebuild the cache", LoadingMode.Preview, 4, 8, 2, false, BenchmarkWorkload.Correctness),
        P("explorer-reindex", "Explorer Reindex", "Check the native order", LoadingMode.Preview, 4, 16, 8, false, BenchmarkWorkload.Correctness),
        P("logging-on", "Logging On", "Measure the detailed-logging overhead", LoadingMode.Preview, 4, 16, 8, false, BenchmarkWorkload.Sequential, detailed: true),
        P("logging-off", "Logging Off", "Measure without detailed logging", LoadingMode.Preview, 4, 16, 8, false, BenchmarkWorkload.Sequential, detailed: false),
        P("recommended-auto", "Recommended Auto", "Choose automatically by folder and RAM", LoadingMode.Preview, 8, 32, 8, false, BenchmarkWorkload.WarmNext),
        P("action-move", "Move Race", "Move while decoding", LoadingMode.Preview, 4, 8, 2, false, BenchmarkWorkload.FileAction),
        P("action-delete", "Delete To Recycle Bin Race", "Delete while decoding, no retry", LoadingMode.Preview, 4, 8, 2, false, BenchmarkWorkload.FileAction),
        P("action-copy", "Copy During Decode", "Copy while decoding", LoadingMode.Preview, 4, 8, 2, false, BenchmarkWorkload.FileAction),
        P("action-interleaved", "Interleaved Actions", "Next, Move, Delete, Copy interleaved", LoadingMode.Preview, 4, 8, 2, false, BenchmarkWorkload.FileAction),
        new("original-correctness", "Original Correctness", "Check quality and cache identity", LoadingMode.Original, 1, 0, 0, false, Reserve, false, true, BenchmarkWorkload.Correctness, 0, 3, true)
    ];

    public static BenchmarkProfile? Find(string id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    private static BenchmarkProfile P(string id, string name, string desc, LoadingMode mode, int workers, int next, int previous, bool full, BenchmarkWorkload workload, long reserve = Reserve, bool detailed = false)
        => new(id, name, desc, mode, workers, next, previous, full, reserve, false, detailed, workload);
}


