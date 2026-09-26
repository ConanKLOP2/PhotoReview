using System.Windows.Input;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.App.Services;

public sealed class WpfKeyNameValidator : IKeyNameValidator
{
    public bool IsValidKeyName(string keyName) =>
        ShortcutKeyName.TryParse(keyName, out _);
}
