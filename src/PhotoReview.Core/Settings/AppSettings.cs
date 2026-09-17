using System.IO;
using System.Text.Json;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Settings;

public class AppSettings
{
    public const int CurrentConfigVersion = 2;
    public int ConfigVersion { get; set; } = CurrentConfigVersion;
    public string Folder2Name { get; set; } = "Loai-2";
    public InitialViewMode InitialViewMode { get; set; } = InitialViewMode.Fit;
    public LoadingMode LoadingMode { get; set; } = LoadingMode.Preview;
    public bool LoggingEnabled { get; set; } = false;
    public ImageSortMode ImageSortMode { get; set; } = ImageSortMode.Name;
    public bool CompareHashEnabled { get; set; } = true;
    public bool CompareSizeEnabled { get; set; } = true;
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

    public static string? ValidateShortcuts(AppSettings settings)
    {
        if (Validator is not null) return Validator(settings);
        ArgumentNullException.ThrowIfNull(settings);
        var bindings = new List<(string Name, string Value)>();
        foreach (var property in typeof(ShortcutMappings).GetProperties())
        {
            if (property.Name == nameof(ShortcutMappings.MoveToFolder2)) continue;
            var value = property.GetValue(settings.Shortcuts)?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                bindings.Add((property.Name, value));
        }
        foreach (var action in settings.Actions ?? [])
        {
            if (!string.IsNullOrWhiteSpace(action.Name) && !string.IsNullOrWhiteSpace(action.Shortcut))
                bindings.Add(($"Action: {action.Name}", action.Shortcut.Trim()));
        }
        var duplicate = bindings.GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        return duplicate is null ? null : $"Phím {duplicate.Key} bị dùng trùng bởi: {string.Join(", ", duplicate.Select(item => item.Name))}.";
    }
}
