using System.IO;
using System.Text.Json;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Settings;

public class AppSettings
{
    public const int CurrentConfigVersion = 3;
    public int ConfigVersion { get; set; } = CurrentConfigVersion;
    public InitialViewMode InitialViewMode { get; set; } = InitialViewMode.Fit;
    public LoadingMode LoadingMode { get; set; } = LoadingMode.Preview;
    public bool LoggingEnabled { get; set; }
    public ImageSortMode ImageSortMode { get; set; } = ImageSortMode.Name;
    public bool CompareHashEnabled { get; set; } = true;
    public bool CompareSizeEnabled { get; set; } = true;
    public ScalingQuality ScalingQuality { get; set; } = ScalingQuality.HighQuality;
    public DecoderBackend DecoderBackend { get; set; } = DecoderBackend.Wpf;
    /// <summary>Preview cache byte budget; only used when physical RAM is unknown (otherwise <see cref="ImageCacheRamPercent"/> wins).</summary>
    public long ImageCacheCapacityBytes { get; set; } = PerformanceOptions.ImageCacheCapacityBytes;

    /// <summary>
    /// In-memory preview (+ source-bytes) cache budget as a percent of physical RAM, [system minimum, 90]. Absent in older
    /// configs, so they load with the default 50 %. Applied when the cache is created, i.e. after a restart.
    /// </summary>
    public int ImageCacheRamPercent { get; set; } = PerformanceOptions.ImageCacheRamPercent;
    public long MemoryReserveBytes { get; set; } = PerformanceOptions.MemoryReserveBytes;
    public int PreloadWorkerCount { get; set; } = PerformanceOptions.PreloadWorkerCount;
    public double PreloadMemoryLoadLimit { get; set; } = PerformanceOptions.PreloadMemoryLoadLimit;
    public long PreviewDiskCacheCapacityBytes { get; set; } = PerformanceOptions.PreviewDiskCacheCapacityBytes;
    public bool UseSourceBytesCache { get; set; } = PerformanceOptions.UseSourceBytesCache;
    public long SourceBytesCapacityBytes { get; set; } = PerformanceOptions.SourceBytesCapacityBytes;
    public List<ReviewAction> Actions { get; set; } = ReviewAction.Defaults();
    public ShortcutMappings Shortcuts { get; set; } = ShortcutMappings.Default();

    /// <summary>
    /// UI language code (<c>en</c>, <c>vi</c>, ...) or <c>auto</c> = follow the Windows UI language (ADR 0006).
    /// Configs written before version 3 are migrated to <c>vi</c> so existing users keep the Vietnamese UI (Q-L1).
    /// </summary>
    public string UiLanguage { get; set; } = PhotoReview.Core.Localization.LanguageLoader.AutoCode;

    /// <summary>
    /// Operation journal durability (ADR 0007, IO03). Absent in older configs, so they load as <see cref="JournalDurability.Fast"/>
    /// (no migration step needed); applies to the next journal write without a restart.
    /// </summary>
    public JournalDurability JournalDurability { get; set; } = JournalDurability.Fast;

    /// <summary>
    /// Q-R8: when true, Delete/Recycle on a drive without a Recycle Bin (removable, network/UNC, unknown) deletes the file
    /// PERMANENTLY after a confirmation. Default false = such deletes are refused (R2-F-05). Absent in older configs = false.
    /// </summary>
    public bool AllowPermanentDeleteWithoutRecycleBin { get; set; }

    public static string ConfigPath => PhotoReview.Core.AppPaths.FromEnvironment().ConfigFile;
    public static Func<string?, AppSettings>? Loader { get; set; }
    public static Action<AppSettings>? Saver { get; set; }
    public static Func<AppSettings, string?>? Validator { get; set; }

    public static AppSettings Load(string? path = null)
    {
        if (path is not null && File.Exists(path))
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) ?? new();
            SettingsStore.Migrate(loaded);
            loaded.Shortcuts ??= ShortcutMappings.Default();
            loaded.Actions ??= ReviewAction.Defaults();
            return loaded;
        }
        return Loader?.Invoke(path) ?? new AppSettings();
    }

    public static void Save(AppSettings settings) => Saver?.Invoke(settings);

    private static readonly SettingsValidator FallbackValidator = new(new SimpleKeyNameValidator());

    public static string? ValidateShortcuts(AppSettings settings) =>
        Validator is not null ? Validator(settings) : FallbackValidator.ValidateShortcuts(settings);

    private sealed class SimpleKeyNameValidator : IKeyNameValidator
    {
        public bool IsValidKeyName(string keyName) => !string.IsNullOrWhiteSpace(keyName);
    }
}
