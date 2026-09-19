using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Settings;

public sealed class SettingsValidator
{
    private readonly IKeyNameValidator _keyValidator;

    public SettingsValidator(IKeyNameValidator keyValidator)
    {
        _keyValidator = keyValidator ?? throw new ArgumentNullException(nameof(keyValidator));
    }

    public string? ValidateShortcuts(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var bindings = new List<(string Name, string Value)>();
        foreach (var property in typeof(ShortcutMappings).GetProperties())
        {
            if (property.Name == nameof(ShortcutMappings.MoveToFolder2)) continue; // Legacy alias; Enter is owned by ReviewAction.
            var value = property.GetValue(settings.Shortcuts)?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(value) || !_keyValidator.IsValidKeyName(value))
                return $"Shortcut {property.Name} không hợp lệ.";
            bindings.Add((property.Name, value));
        }
        foreach (var action in settings.Actions ?? [])
        {
            if (string.IsNullOrWhiteSpace(action.Name) || string.IsNullOrWhiteSpace(action.Shortcut) || !_keyValidator.IsValidKeyName(action.Shortcut.Trim()))
                return "Action phải có tên và phím tắt hợp lệ.";
            bindings.Add(($"Action: {action.Name}", action.Shortcut.Trim()));
        }
        var duplicate = bindings.GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        return duplicate is null ? null : $"Phím {duplicate.Key} bị dùng trùng bởi: {string.Join(", ", duplicate.Select(item => item.Name))}.";
    }
}
