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
}