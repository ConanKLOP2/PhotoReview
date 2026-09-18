using System.IO;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core;

/// <summary>
/// Quản lý tập trung toàn bộ đường dẫn cấu hình và dữ liệu của ứng dụng PhotoReview.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public string ConfigFile { get; }
    public string JournalFile { get; }
    public string SessionsDir { get; }
    public string LogFile { get; }
    public string PreviewCacheDir { get; }
    public string ThumbnailCacheDir { get; }
    public string WindowPlacementFile { get; }

    public AppPaths(string localAppData, string? dataRootOverride = null, bool isolateConfig = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);

        var appRoot = Path.Combine(localAppData, "PhotoReview");
        var hasOverride = !string.IsNullOrWhiteSpace(dataRootOverride);
        var dataRoot = hasOverride ? dataRootOverride! : Path.Combine(appRoot, "Data");

        // ConfigFile: Giữ nguyên hành vi cũ là nằm tại %LOCALAPPDATA%\PhotoReview\config.json.
        // Chỉ ghi vào <override>\config.json khi isolateConfig = true và có dataRootOverride (dành riêng cho test).
        ConfigFile = isolateConfig && hasOverride
            ? Path.Combine(dataRootOverride!, "config.json")
            : Path.Combine(appRoot, "config.json");

        // JournalFile: <dataRoot>\operations.jsonl
        JournalFile = Path.Combine(dataRoot, "operations.jsonl");

        // SessionsDir: <dataRoot>\Sessions
        SessionsDir = Path.Combine(dataRoot, "Sessions");

        // LogFile: (override ?? %LOCALAPPDATA%\PhotoReview)\logs\app.log
        // Ghi chú: FileLog dùng root trực tiếp dưới PhotoReview (hoặc override),
        // khác với Journal và Sessions vốn nằm trong thư mục con Data khi không có override.
        var logRoot = hasOverride ? dataRootOverride! : appRoot;
        LogFile = Path.Combine(logRoot, "logs", "app.log");

        // Caches và WindowPlacementFile luôn nằm dưới appRoot (%LOCALAPPDATA%\PhotoReview)
        PreviewCacheDir = Path.Combine(appRoot, "cache");
        ThumbnailCacheDir = Path.Combine(appRoot, "thumbnails");
        WindowPlacementFile = Path.Combine(appRoot, "window-placement.json");
    }

    /// <summary>
    /// Factory là nơi DUY NHẤT trong toàn bộ ứng dụng đọc biến môi trường PHOTOREVIEW_DATA_ROOT.
    /// </summary>
    public static AppPaths FromEnvironment() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("PHOTOREVIEW_DATA_ROOT"));
}
