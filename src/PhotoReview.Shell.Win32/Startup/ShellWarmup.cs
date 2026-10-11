using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Startup;

/// <summary>
/// perf(startup): việc khởi động không tránh được nhưng chạy được trên thread pool, song song với runtime/cửa sổ.
/// Không đọc/ghi gì của người dùng; lỗi chỉ mất phần làm nóng.
/// </summary>
internal static class ShellWarmup
{
    /// <summary>Dispatcher rỗng cho cache làm nóng (không prefetch nên không bao giờ được đăng việc).</summary>
    private sealed class NoDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();

        public void Post(Action action, UiPriority priority) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Action action, UiPriority priority) => InvokeAsync(action);

        public ValueTask YieldAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask YieldAsync(UiPriority priority, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

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
            using var warm = D2DRenderSurface.CreateOffscreenSurface(1024, 768, 1.0, useHardware: true);
            // Vẽ một ảnh nhỏ phóng to bằng đúng chế độ nội suy của ảnh đầu (HighQualityCubic): lần vẽ đầu tiên của hiệu ứng
            // này tốn hàng trăm ms (khởi tạo hiệu ứng/shader của driver) và trước đây rơi vào lượt gán ảnh trên UI thread.
            using var cache = new GpuImageCache(warm, new NoDispatcher());
            using var pixels = PixelBuffer.Allocate(64, 64, PixelLayout.Bgr32);
            var image = cache.GetOrUpload(pixels);
            var context = warm.BeginDrawContext();
            context.DrawImage(image, new PhotoReview.App.Input.RectD(0, 0, 1000, 700), null, ImageInterpolation.HighQualityCubic);
            warm.EndDrawAndPresent();
            PhotoReviewPerf.StartupMark("graphicsWarm");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Không GPU/RDP: thiết bị thật rơi về WARP ở bước tạo cửa sổ.
        }
    });
}
