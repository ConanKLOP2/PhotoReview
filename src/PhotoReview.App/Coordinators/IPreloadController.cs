using System;
using System.Threading.Tasks;
using PhotoReview.Core.Caching;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// Giao diện điều phối tác vụ nạp trước ảnh (preload) xung quanh vị trí hiện tại.
/// </summary>
public interface IPreloadController
{
    /// <summary>Kích hoạt preload các ảnh lân cận vị trí center.</summary>
    Task PreloadAroundAsync(int center);

    /// <summary>Kiểm tra và tiêu thụ cache key đã được preload làm ấm (ghi nhận preload hit).</summary>
    bool TryConsumePreloadedKey(ImageCacheKey key);

    /// <summary>Hủy các tác vụ preload đang thực thi dở dang.</summary>
    void Cancel();

    /// <summary>Gỡ các key preload cho đường dẫn chỉ định.</summary>
    void RemovePreloadedKeysForPath(string normalizedPath);

    /// <summary>Xóa toàn bộ các key đã preload.</summary>
    void ClearPreloadedKeys();

    /// <summary>
    /// True when no preload work is in flight for the current lifetime. Best-effort diagnostics signal
    /// (used by the perf harness to know a navigation has settled); default true for implementations
    /// that don't track preload state (e.g. test fakes), so this member is source- and binary-compatible
    /// with every existing implementer.
    /// </summary>
    bool IsIdle => true;

    /// <summary>
    /// perf(preload): called at the start of every navigation, before the image's own decode, so preload
    /// can track direction and key rate and re-center immediately. Default no-op (test fakes).
    /// </summary>
    void NotifyNavigation(int index) { }

    /// <summary>
    /// perf(preload): delay before the viewer starts its own decode of a not-yet-cached image (non-zero
    /// only during a key-held burst). Default zero (test fakes).
    /// </summary>
    TimeSpan GetViewerDecodeDelay() => TimeSpan.Zero;
}

/// <summary>
/// Bộ chuyển đổi mặc định nối IPreloadController tới PreloadScheduler của Imaging.
/// </summary>
public sealed class PreloadSchedulerAdapter : IPreloadController
{
    private readonly Func<PreloadScheduler?> _getScheduler;

    public PreloadSchedulerAdapter(Func<PreloadScheduler?> getScheduler)
    {
        _getScheduler = getScheduler ?? throw new ArgumentNullException(nameof(getScheduler));
    }

    public PreloadSchedulerAdapter(PreloadScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        _getScheduler = () => scheduler;
    }

    public Task PreloadAroundAsync(int center) => _getScheduler()?.PreloadAroundAsync(center) ?? Task.CompletedTask;

    public bool TryConsumePreloadedKey(ImageCacheKey key) => _getScheduler()?.TryConsumePreloadedKey(key) ?? false;

    public void Cancel() => _getScheduler()?.Cancel();

    public void RemovePreloadedKeysForPath(string normalizedPath) => _getScheduler()?.RemovePreloadedKeysForPath(normalizedPath);

    public void ClearPreloadedKeys() => _getScheduler()?.ClearPreloadedKeys();

    public bool IsIdle => _getScheduler()?.IsIdle ?? true;

    public void NotifyNavigation(int index) => _getScheduler()?.NotifyNavigation(index);

    public TimeSpan GetViewerDecodeDelay() => _getScheduler()?.GetViewerDecodeDelay() ?? TimeSpan.Zero;
}
