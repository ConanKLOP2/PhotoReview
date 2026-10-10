using System.IO;
using PhotoReview.Core.Catalog;

namespace PhotoReview.App.Services;

/// <summary>
/// perf/startup-first-image + P-1: starts the viewer decode of the file the app was launched with before the presenter
/// asks for it. The presenter later builds the same key and joins this decode (in-flight dedup) or finds it in the RAM
/// cache, so the work is never done twice when the key matches; when it does not (another decode box), this is one extra
/// background decode, never a wrong image. Camera RAW files are skipped (their pairing decides which file is shown, and
/// their decode is the costly one).
/// </summary>
internal static class InitialImagePrewarm
{
    /// <param name="targetBox">The decode box to key the decode with; null = the service's current viewport box.</param>
    /// <returns>The started decode, or null when nothing was started.</returns>
    public static Task? Start(PreviewImageService? previews, AppSettings settings, string? path, DecodeBox? targetBox = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (previews is null || string.IsNullOrWhiteSpace(path)) return null;
        if (!ImageFileTypes.IsSupported(path, rawEnabled: false, settings.WebpHeicSupportEnabled)) return null;
        var fullPath = Path.GetFullPath(path);
        // The key needs a file stat: off the calling thread (Q-R29: a slow link must never stall the UI thread).
        var decode = Task.Run(() => previews.GetViewerPreviewAsync(fullPath,
            targetBox is { } box ? previews.GetCurrentCacheKey(fullPath, box) : previews.GetCurrentCacheKey(fullPath),
            CancellationToken.None));
        // A failure (unreadable or damaged file) is reported by the presenter's own request; only observe it here.
        _ = decode.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return decode;
    }
}
