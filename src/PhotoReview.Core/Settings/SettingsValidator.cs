using System.Reflection;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;

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
        // Hand-edited/programmatic settings can hold nulls: report them like any other invalid value instead of throwing.
        if (settings.Shortcuts is null) return Tr.CoreSettingsShortcutInvalid(nameof(AppSettings.Shortcuts));
        var bindings = new List<(string Name, string Value)>();
        foreach (var property in typeof(ShortcutMappings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name == nameof(ShortcutMappings.MoveToFolder2)) continue; // Legacy alias; Enter is owned by ReviewAction.
            var value = property.GetValue(settings.Shortcuts)?.ToString()?.Trim();
            // Optional shortcuts (ShortcutMappings.OptionalNames): empty = feature disabled, not an error.
            if (string.IsNullOrWhiteSpace(value) && ShortcutMappings.IsOptional(property.Name)) continue;
            if (string.IsNullOrWhiteSpace(value) || !_keyValidator.IsValidKeyName(value))
                return Tr.CoreSettingsShortcutInvalid(property.Name);
            bindings.Add((property.Name, value));
        }
        foreach (var action in settings.Actions ?? [])
        {
            if (action is null || string.IsNullOrWhiteSpace(action.Name) || string.IsNullOrWhiteSpace(action.Shortcut) || !_keyValidator.IsValidKeyName(action.Shortcut.Trim()))
                return Tr.CoreSettingsActionInvalid;
            bindings.Add((Tr.CoreSettingsActionBindingName(action.Name), action.Shortcut.Trim()));
        }
        var duplicate = bindings.GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        return duplicate is null ? null : Tr.CoreSettingsShortcutDuplicate(duplicate.Key, string.Join(", ", duplicate.Select(item => item.Name)));
    }
}
