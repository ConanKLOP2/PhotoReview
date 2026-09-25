using PhotoReview.Core.Model;

namespace PhotoReview.Core.Settings;

/// <summary>
/// Repairs values of a freshly loaded <see cref="AppSettings"/> that a hand-edited or corrupt <c>config.json</c> can make
/// unusable (R2-F-04): a non-positive cache capacity makes the cache constructor throw, a <c>null</c> entry in
/// <c>Actions</c> throws while building the shortcut router. Every invalid value is replaced by its default so start-up
/// always succeeds; the names of the repaired settings are returned so the caller can warn the user.
/// </summary>
public static class SettingsNormalizer
{
    /// <summary>Normalises <paramref name="settings"/> in place; returns the names of the settings that were reset (empty when all valid).</summary>
    public static IReadOnlyList<string> Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var fixedNames = new List<string>();

        if (settings.ImageCacheCapacityBytes <= 0)
        {
            settings.ImageCacheCapacityBytes = PerformanceOptions.ImageCacheCapacityBytes;
            fixedNames.Add(nameof(AppSettings.ImageCacheCapacityBytes));
        }
        if (settings.SourceBytesCapacityBytes <= 0)
        {
            settings.SourceBytesCapacityBytes = PerformanceOptions.SourceBytesCapacityBytes;
            fixedNames.Add(nameof(AppSettings.SourceBytesCapacityBytes));
        }
        if (settings.MemoryReserveBytes < 0)
        {
            settings.MemoryReserveBytes = PerformanceOptions.MemoryReserveBytes;
            fixedNames.Add(nameof(AppSettings.MemoryReserveBytes));
        }
        if (settings.PreviewDiskCacheCapacityBytes < 0)
        {
            settings.PreviewDiskCacheCapacityBytes = PerformanceOptions.PreviewDiskCacheCapacityBytes;
            fixedNames.Add(nameof(AppSettings.PreviewDiskCacheCapacityBytes));
        }
        if (settings.PreloadWorkerCount <= 0)
        {
            settings.PreloadWorkerCount = PerformanceOptions.PreloadWorkerCount;
            fixedNames.Add(nameof(AppSettings.PreloadWorkerCount));
        }
        if (!(settings.PreloadMemoryLoadLimit > 0 && settings.PreloadMemoryLoadLimit <= 1))
        {
            settings.PreloadMemoryLoadLimit = PerformanceOptions.PreloadMemoryLoadLimit;
            fixedNames.Add(nameof(AppSettings.PreloadMemoryLoadLimit));
        }

        if (!Enum.IsDefined(settings.InitialViewMode)) { settings.InitialViewMode = InitialViewMode.Fit; fixedNames.Add(nameof(AppSettings.InitialViewMode)); }
        if (!Enum.IsDefined(settings.LoadingMode)) { settings.LoadingMode = LoadingMode.Preview; fixedNames.Add(nameof(AppSettings.LoadingMode)); }
        if (!Enum.IsDefined(settings.ImageSortMode)) { settings.ImageSortMode = ImageSortMode.Name; fixedNames.Add(nameof(AppSettings.ImageSortMode)); }
        if (!Enum.IsDefined(settings.ScalingQuality)) { settings.ScalingQuality = ScalingQuality.HighQuality; fixedNames.Add(nameof(AppSettings.ScalingQuality)); }
        if (!Enum.IsDefined(settings.DecoderBackend)) { settings.DecoderBackend = DecoderBackend.Wpf; fixedNames.Add(nameof(AppSettings.DecoderBackend)); }
        if (!Enum.IsDefined(settings.JournalDurability)) { settings.JournalDurability = JournalDurability.Fast; fixedNames.Add(nameof(AppSettings.JournalDurability)); }

        if (settings.Actions is { } actions)
        {
            var removed = actions.RemoveAll(a => a is null);
            var invalidOperation = false;
            foreach (var action in actions)
            {
                action.Name ??= "";
                action.Shortcut ??= "";
                action.Destination ??= "";
                if (!Enum.IsDefined(action.Operation))
                {
                    action.Operation = FileOperationType.Move;
                    invalidOperation = true;
                }
            }
            if (removed > 0 || invalidOperation) fixedNames.Add(nameof(AppSettings.Actions));
        }
        return fixedNames;
    }
}
