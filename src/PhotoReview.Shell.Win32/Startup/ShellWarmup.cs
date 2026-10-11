using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Startup;

/// <summary>
/// perf(startup): việc khởi động không tránh được nhưng chạy được trên thread pool, song song với runtime/cửa sổ.
/// Không đọc/ghi gì của người dùng; lỗi chỉ mất phần làm nóng.
/// </summary>
internal static class ShellWarmup
{
    /// <summary>
    /// P-1: chạy đường parse config.json một lần trên mặc định, từ rất sớm (~100-150 ms chi phí một lần: metadata
    /// System.Text.Json của ~100 thuộc tính AppSettings + JIT converter), để <c>SettingsStore.Load</c> thật thấy metadata đã sẵn.
    /// </summary>
    public static Task WarmSettingsParser() => Task.Run(() =>
    {
        try
        {
            var defaults = System.Text.Json.JsonSerializer.Serialize(new AppSettings(), AppSettingsJsonContext.Default.AppSettings);
            _ = SettingsStore.ParseText(defaults);
            PhotoReviewPerf.StartupMark("settingsParserWarm");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Chỉ là làm nóng: lần load thật tự báo lỗi của nó.
        }
    });

    /// <summary>
    /// Nạp d3d11/d2d1/dxgi + khởi tạo driver GPU (chi phí theo tiến trình, không theo thiết bị) trên thread pool bằng một
    /// thiết bị offscreen 1x1 rồi huỷ ngay; thiết bị thật của cửa sổ tạo sau trên UI thread thấy mọi DLL/driver đã nạp.
    /// </summary>
    public static Task WarmGraphics() => Task.Run(() =>
    {
        try
        {
            using var warm = D2DRenderSurface.CreateOffscreenSurface(1, 1, 1.0, useHardware: true);
            PhotoReviewPerf.StartupMark("graphicsWarm");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Không GPU/RDP: thiết bị thật rơi về WARP ở bước tạo cửa sổ.
        }
    });
}
