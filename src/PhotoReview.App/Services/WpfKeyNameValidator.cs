using System.Windows.Input;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Settings;

namespace PhotoReview.App.Services;

public sealed class WpfKeyNameValidator : IKeyNameValidator
{
    public bool IsValidKeyName(string keyName) =>
        ShortcutKeyName.TryParse(keyName, out _);

    public static void WireUp()
    {
        AppSettings.Validator = new SettingsValidator(new WpfKeyNameValidator()).ValidateShortcuts;
    }
}
