using System.Windows.Input;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Settings;

namespace PhotoReview.App;

public static class SettingsValidation
{
    private static readonly Lazy<SettingsStore> DefaultStore = new(() =>
        new SettingsStore(
            PhotoReview.Core.AppPaths.FromEnvironment(),
            new PhysicalFileSystem(),
            new AppLogAdapter(),
            App.LogStartupErrorForced));

    static SettingsValidation()
    {
        InitializeHooks();
    }

    public static void InitializeHooks()
    {
        AppSettings.Validator = ValidateShortcuts;
        AppSettings.Loader = path => DefaultStore.Value.Load(path);
        AppSettings.Saver = settings => DefaultStore.Value.Save(settings);
    }

    public static string? ValidateShortcuts(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var bindings = new List<(string Name, string Value)>();
        foreach (var property in typeof(ShortcutMappings).GetProperties())
        {
            if (property.Name == nameof(ShortcutMappings.MoveToFolder2)) continue; // Legacy alias; Enter is owned by ReviewAction.
            var value = property.GetValue(settings.Shortcuts)?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(value) || !Enum.TryParse<Key>(value, true, out _))
                return $"Shortcut {property.Name} không hợp lệ.";
            bindings.Add((property.Name, value));
        }
        foreach (var action in settings.Actions ?? [])
        {
            if (string.IsNullOrWhiteSpace(action.Name) || !Enum.TryParse<Key>(action.Shortcut, true, out _))
                return "Action phải có tên và phím tắt hợp lệ.";
            bindings.Add(($"Action: {action.Name}", action.Shortcut.Trim()));
        }
        var duplicate = bindings.GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        return duplicate is null ? null : $"Phím {duplicate.Key} bị dùng trùng bởi: {string.Join(", ", duplicate.Select(item => item.Name))}.";
    }
}

internal sealed class AppLogAdapter : ILog
{
    public void Info(string message) => AppLog.Info(message);
    public void Warn(string message) => AppLog.Info($"[WARN] {message}");
    public void Error(string message, Exception? ex = null) => AppLog.Error(message, ex);
}
