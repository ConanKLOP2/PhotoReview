using System.IO;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core;

/// <summary>
/// Quản lý tập trung toàn bộ đường dẫn cấu hình và dữ liệu của ứng dụng PhotoReview.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public const string DataRootEnvironmentVariable = "PHOTOREVIEW_DATA_ROOT";

    /// <summary>
    /// "1"/"true" + <see cref="DataRootEnvironmentVariable"/>: config.json also moves under the data root. Set by test
    /// fixtures so a test that saves settings never overwrites the user's real %LOCALAPPDATA%\PhotoReview\config.json.
    /// </summary>
    public const string IsolateConfigEnvironmentVariable = "PHOTOREVIEW_ISOLATE_CONFIG";

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
        // Resolve once: a relative override would otherwise follow the (mutable) current directory at every later use.
        // An unusable value (e.g. NUL) must not crash startup or logger init: ignore it and use the default root.
        var overrideRoot = ResolveOverride(dataRootOverride);
        var hasOverride = overrideRoot is not null;
        var dataRoot = overrideRoot ?? Path.Combine(appRoot, "Data");

        // ConfigFile: Giữ nguyên hành vi cũ là nằm tại %LOCALAPPDATA%\PhotoReview\config.json.
        // Chỉ ghi vào <override>\config.json khi isolateConfig = true và có dataRootOverride (dành riêng cho test).
        ConfigFile = isolateConfig && hasOverride
            ? Path.Combine(overrideRoot!, "config.json")
            : Path.Combine(appRoot, "config.json");

        // JournalFile: <dataRoot>\operations.jsonl
        JournalFile = Path.Combine(dataRoot, "operations.jsonl");

        // SessionsDir: <dataRoot>\Sessions
        SessionsDir = Path.Combine(dataRoot, "Sessions");

        // LogFile: (override ?? %LOCALAPPDATA%\PhotoReview)\logs\app.log
        // Ghi chú: FileLog dùng root trực tiếp dưới PhotoReview (hoặc override),
        // khác với Journal và Sessions vốn nằm trong thư mục con Data khi không có override.
        var logRoot = overrideRoot ?? appRoot;
        LogFile = Path.Combine(logRoot, "logs", "app.log");

        // Caches và WindowPlacementFile luôn nằm dưới appRoot (%LOCALAPPDATA%\PhotoReview)
        PreviewCacheDir = Path.Combine(appRoot, "cache");
        ThumbnailCacheDir = Path.Combine(appRoot, "thumbnails");
        WindowPlacementFile = Path.Combine(appRoot, "window-placement.json");
    }

    private static string? ResolveOverride(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.GetFullPath(value.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>
    /// Factory là nơi DUY NHẤT trong toàn bộ ứng dụng đọc biến môi trường PHOTOREVIEW_DATA_ROOT / PHOTOREVIEW_ISOLATE_CONFIG.
    /// </summary>
    public static AppPaths FromEnvironment() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable(DataRootEnvironmentVariable),
        IsTruthy(Environment.GetEnvironmentVariable(IsolateConfigEnvironmentVariable)));

    private static bool IsTruthy(string? value) =>
        value is not null && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
}
