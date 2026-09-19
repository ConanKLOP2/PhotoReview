using System;
using System.Threading.Tasks;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// Adapter kết nối IPreloadController với PreloadScheduler thực tế.
/// </summary>
public sealed class PreloadControllerAdapter : IPreloadController, IDisposable
{
    private readonly Func<PreloadScheduler> _getScheduler;

    public PreloadControllerAdapter(Func<PreloadScheduler> getScheduler)
    {
        _getScheduler = getScheduler ?? throw new ArgumentNullException(nameof(getScheduler));
    }

    public Task PreloadAroundAsync(int center) => _getScheduler().PreloadAroundAsync(center);

    public bool TryConsumePreloadedKey(ImageCacheKey key) => _getScheduler().TryConsumePreloadedKey(key);

    public void Cancel() => _getScheduler().Cancel();

    public void RemovePreloadedKeysForPath(string normalizedPath) => _getScheduler().RemovePreloadedKeysForPath(normalizedPath);

    public void ClearPreloadedKeys() => _getScheduler().ClearPreloadedKeys();

    public void Dispose() => _getScheduler().Dispose();
}
