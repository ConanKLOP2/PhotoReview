using System.IO;
using System.Text.Json;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Settings;

public class AppSettings
{
    public const int CurrentConfigVersion = 2;
    public int ConfigVersion { get; set; } = CurrentConfigVersion;
    public InitialViewMode InitialViewMode { get; set; } = InitialViewMode.Fit;
    public LoadingMode LoadingMode { get; set; } = LoadingMode.Preview;
    public bool LoggingEnabled { get; set; } = false;
    public ImageSortMode ImageSortMode { get; set; } = ImageSortMode.Name;
    public bool CompareHashEnabled { get; set; } = true;
    public bool CompareSizeEnabled { get; set; } = true;
    public ScalingQuality ScalingQuality { get; set; } = ScalingQuality.HighQuality;
    public List<ReviewAction> Actions { get; set; } = ReviewAction.Defaults();
    public ShortcutMappings Shortcuts { get; set; } = ShortcutMappings.Default();

    public static string ConfigPath => PhotoReview.Core.AppPaths.FromEnvironment().ConfigFile;
    public static Func<string?, AppSettings>? Loader { get; set; }
    public static Action<AppSettings>? Saver { get; set; }
    public static Func<AppSettings, string?>? Validator { get; set; }

    public static AppSettings Load(string? path = null)
    {
        if (path is not null && File.Exists(path))
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
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
