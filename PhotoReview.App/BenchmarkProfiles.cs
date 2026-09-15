namespace PhotoReview.App;

public static class BenchmarkProfiles
{
    private const long Reserve = 2L * 1024 * 1024 * 1024;
    public static IReadOnlyList<BenchmarkProfile> All { get; } =
    [
        P("instant-review", "Instant Review", "Ưu tiên ảnh đầu tiên", "Fast", 2, 4, 1, false, BenchmarkWorkload.FirstFrame),
        P("fast-sequential", "Fast Sequential", "Next liên tục", "Fast", 8, 32, 8, false, BenchmarkWorkload.Sequential),
        P("fast-balanced", "Fast Balanced", "Cân bằng Next và Previous", "Fast", 4, 16, 8, false, BenchmarkWorkload.WarmNext),
        P("fast-aggressive", "Fast Aggressive", "Preload sâu và nhiều worker", "Fast", 16, 64, 16, true, BenchmarkWorkload.Preload),
        P("preview-light", "Preview Light", "Preview nhỏ, ít RAM", "Preview", 4, 16, 4, false, BenchmarkWorkload.Sequential),
        P("preview-balanced", "Preview Balanced", "Preview theo viewport", "Preview", 8, 32, 8, false, BenchmarkWorkload.WarmNext),
        P("preview-quality", "Preview Quality", "Preview lớn, ưu tiên chất lượng", "Preview", 4, 16, 8, false, BenchmarkWorkload.WarmNext),
        P("preview-high-quality", "Preview High Quality", "Gần chất lượng Original", "Preview", 8, 16, 8, false, BenchmarkWorkload.FirstFrame),
        P("no-preload-baseline", "No Preload Baseline", "Mốc không preload", "Preview", 1, 0, 0, false, BenchmarkWorkload.Sequential),
        P("nearby-only", "Nearby Only", "Chỉ preload vùng lân cận", "Preview", 4, 8, 4, false, BenchmarkWorkload.Preload),
        P("full-folder-warm", "Full Folder Warm", "Làm ấm toàn bộ folder", "Preview", 8, 32, 8, true, BenchmarkWorkload.Preload),
        P("large-folder-safe", "Large Folder Safe", "Giới hạn RAM cho folder lớn", "Preview", 4, 8, 2, false, BenchmarkWorkload.Preload),
        P("huge-image-safe", "Huge Image Safe", "Giảm concurrency cho ảnh lớn", "Preview", 2, 4, 2, false, BenchmarkWorkload.FirstFrame),
        P("ssd-throughput", "SSD High Throughput", "Đọc song song trên SSD", "Preview", 12, 32, 8, false, BenchmarkWorkload.Sequential),
        P("hdd-conservative", "HDD Conservative", "Giảm seek trên HDD", "Preview", 2, 4, 1, false, BenchmarkWorkload.Sequential),
        P("network-safe", "Network Safe", "Đọc ổ mạng thận trọng", "Preview", 2, 4, 1, false, BenchmarkWorkload.FirstFrame),
        P("low-memory", "Low Memory", "Giữ reserve RAM lớn", "Preview", 2, 4, 2, false, BenchmarkWorkload.Sequential, reserve: 4L * 1024 * 1024 * 1024),
        P("ram-maximizer", "RAM Maximizer", "Tận dụng RAM trong ngưỡng an toàn", "Preview", 12, 64, 16, true, BenchmarkWorkload.Preload),
        P("rapid-key-press", "Rapid Key Press", "Nhấn Next nhanh, không skip", "Preview", 8, 16, 4, false, BenchmarkWorkload.WarmNext),
        P("random-navigation", "Random Navigation", "Điều hướng ngẫu nhiên", "Preview", 6, 16, 8, false, BenchmarkWorkload.Random),
        P("cache-recovery", "Cache Recovery", "Clear và dựng lại cache", "Preview", 4, 8, 2, false, BenchmarkWorkload.Correctness),
        P("explorer-reindex", "Explorer Reindex", "Kiểm tra native order", "Preview", 4, 16, 8, false, BenchmarkWorkload.Correctness),
        P("logging-on", "Logging On", "Đo overhead log chi tiết", "Preview", 4, 16, 8, false, BenchmarkWorkload.Sequential, detailed: true),
        P("logging-off", "Logging Off", "Đo không có log chi tiết", "Preview", 4, 16, 8, false, BenchmarkWorkload.Sequential, detailed: false),
        P("recommended-auto", "Recommended Auto", "Tự chọn theo folder và RAM", "Preview", 8, 32, 8, false, BenchmarkWorkload.WarmNext),
        P("action-move", "Move Race", "Move lúc decode", "Preview", 4, 8, 2, false, BenchmarkWorkload.FileAction),
        P("action-delete", "Delete To Recycle Bin Race", "Delete lúc decode, không retry", "Preview", 4, 8, 2, false, BenchmarkWorkload.FileAction),
        P("action-copy", "Copy During Decode", "Copy lúc decode", "Preview", 4, 8, 2, false, BenchmarkWorkload.FileAction),
        P("action-interleaved", "Interleaved Actions", "Next, Move, Delete, Copy xen kẽ", "Preview", 4, 8, 2, false, BenchmarkWorkload.FileAction),
        new("original-correctness", "Original Correctness", "Kiểm tra chất lượng và cache identity", "Original", 1, 0, 0, false, Reserve, false, true, BenchmarkWorkload.Correctness, 0, 3, true)
    ];

    public static BenchmarkProfile? Find(string id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    private static BenchmarkProfile P(string id, string name, string desc, string mode, int workers, int next, int previous, bool full, BenchmarkWorkload workload, long reserve = Reserve, bool detailed = false)
        => new(id, name, desc, mode, workers, next, previous, full, reserve, false, detailed, workload);
}


