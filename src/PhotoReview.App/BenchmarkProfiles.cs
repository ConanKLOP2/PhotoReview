using PhotoReview.Core.Settings;
using PhotoReview.Core.Model;

namespace PhotoReview.App;

public static class BenchmarkProfiles
{
    private const long Reserve = PerformanceOptions.MemoryReserveBytes;
    public static IReadOnlyList<BenchmarkProfile> All { get; } =
    [
        P("instant-review", "Instant Review", "Ưu tiên ảnh đầu tiên", LoadingMode.Fast, 2, 4, 1, false, BenchmarkWorkload.FirstFrame),
        P("fast-sequential", "Fast Sequential", "Next liên tục", LoadingMode.Fast, 8, 32, 8, false, BenchmarkWorkload.Sequential),
        P("fast-balanced", "Fast Balanced", "Cân bằng Next và Previous", LoadingMode.Fast, 4, 16, 8, false, BenchmarkWorkload.WarmNext),
        P("fast-aggressive", "Fast Aggressive", "Preload sâu và nhiều worker", LoadingMode.Fast, 16, 64, 16, true, BenchmarkWorkload.Preload),
        P("preview-light", "Preview Light", "Preview nhỏ, ít RAM", LoadingMode.Preview, 4, 16, 4, false, BenchmarkWorkload.Sequential),
        P("preview-balanced", "Preview Balanced", "Preview theo viewport", LoadingMode.Preview, 8, 32, 8, false, BenchmarkWorkload.WarmNext),
        P("preview-quality", "Preview Quality", "Preview lớn, ưu tiên chất lượng", LoadingMode.Preview, 4, 16, 8, false, BenchmarkWorkload.WarmNext),
        P("preview-high-quality", "Preview High Quality", "Gần chất lượng Original", LoadingMode.Preview, 8, 16, 8, false, BenchmarkWorkload.FirstFrame),
        P("no-preload-baseline", "No Preload Baseline", "Mốc không preload", LoadingMode.Preview, 1, 0, 0, false, BenchmarkWorkload.Sequential),
        P("nearby-only", "Nearby Only", "Chỉ preload vùng lân cận", LoadingMode.Preview, 4, 8, 4, false, BenchmarkWorkload.Preload),
        P("full-folder-warm", "Full Folder Warm", "Làm ấm toàn bộ folder", LoadingMode.Preview, 8, 32, 8, true, BenchmarkWorkload.Preload),
        P("large-folder-safe", "Large Folder Safe", "Giới hạn RAM cho folder lớn", LoadingMode.Preview, 4, 8, 2, false, BenchmarkWorkload.Preload),
        P("huge-image-safe", "Huge Image Safe", "Giảm concurrency cho ảnh lớn", LoadingMode.Preview, 2, 4, 2, false, BenchmarkWorkload.FirstFrame),
        P("ssd-throughput", "SSD High Throughput", "Đọc song song trên SSD", LoadingMode.Preview, 12, 32, 8, false, BenchmarkWorkload.Sequential),
        P("hdd-conservative", "HDD Conservative", "Giảm seek trên HDD", LoadingMode.Preview, 2, 4, 1, false, BenchmarkWorkload.Sequential),
        P("network-safe", "Network Safe", "Đọc ổ mạng thận trọng", LoadingMode.Preview, 2, 4, 1, false, BenchmarkWorkload.FirstFrame),
        P("low-memory", "Low Memory", "Giữ reserve RAM lớn", LoadingMode.Preview, 2, 4, 2, false, BenchmarkWorkload.Sequential, reserve: 4L * 1024 * 1024 * 1024),
        P("ram-maximizer", "RAM Maximizer", "Tận dụng RAM trong ngưỡng an toàn", LoadingMode.Preview, 12, 64, 16, true, BenchmarkWorkload.Preload),
        P("rapid-key-press", "Rapid Key Press", "Nhấn Next nhanh, không skip", LoadingMode.Preview, 8, 16, 4, false, BenchmarkWorkload.WarmNext),
        P("random-navigation", "Random Navigation", "Điều hướng ngẫu nhiên", LoadingMode.Preview, 6, 16, 8, false, BenchmarkWorkload.Random),
        P("cache-recovery", "Cache Recovery", "Clear và dựng lại cache", LoadingMode.Preview, 4, 8, 2, false, BenchmarkWorkload.Correctness),
        P("explorer-reindex", "Explorer Reindex", "Kiểm tra native order", LoadingMode.Preview, 4, 16, 8, false, BenchmarkWorkload.Correctness),
        P("logging-on", "Logging On", "Đo overhead log chi tiết", LoadingMode.Preview, 4, 16, 8, false, BenchmarkWorkload.Sequential, detailed: true),
        P("logging-off", "Logging Off", "Đo không có log chi tiết", LoadingMode.Preview, 4, 16, 8, false, BenchmarkWorkload.Sequential, detailed: false),
        P("recommended-auto", "Recommended Auto", "Tự chọn theo folder và RAM", LoadingMode.Preview, 8, 32, 8, false, BenchmarkWorkload.WarmNext),
        P("action-move", "Move Race", "Move lúc decode", LoadingMode.Preview, 4, 8, 2, false, BenchmarkWorkload.FileAction),
        P("action-delete", "Delete To Recycle Bin Race", "Delete lúc decode, không retry", LoadingMode.Preview, 4, 8, 2, false, BenchmarkWorkload.FileAction),
        P("action-copy", "Copy During Decode", "Copy lúc decode", LoadingMode.Preview, 4, 8, 2, false, BenchmarkWorkload.FileAction),
        P("action-interleaved", "Interleaved Actions", "Next, Move, Delete, Copy xen kẽ", LoadingMode.Preview, 4, 8, 2, false, BenchmarkWorkload.FileAction),
        new("original-correctness", "Original Correctness", "Kiểm tra chất lượng và cache identity", LoadingMode.Original, 1, 0, 0, false, Reserve, false, true, BenchmarkWorkload.Correctness, 0, 3, true)
    ];

    public static BenchmarkProfile? Find(string id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    private static BenchmarkProfile P(string id, string name, string desc, LoadingMode mode, int workers, int next, int previous, bool full, BenchmarkWorkload workload, long reserve = Reserve, bool detailed = false)
        => new(id, name, desc, mode, workers, next, previous, full, reserve, false, detailed, workload);
}


