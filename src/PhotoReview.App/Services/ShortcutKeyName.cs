using PhotoReview.App.Input;

namespace PhotoReview.App.Services;

/// <summary>
/// The one place that turns a configured shortcut string into a <see cref="KeyId"/> (the WPF-free mirror of
/// <c>System.Windows.Input.Key</c>, C-06). <c>Enum.TryParse</c> alone accepts numbers ("999") and comma lists ("A,B"), and
/// names that can never be pressed as a command (Escape closes the app in the router, Tab moves focus, ImeProcessed/None/
/// modifiers are not commands), so a saved shortcut could silently never fire.
/// </summary>
public static class ShortcutKeyName
{
    public static bool TryParse(string? name, out KeyId key)
    {
        key = KeyId.None;
        var text = name?.Trim();
        if (!string.IsNullOrEmpty(text)) text = PhotoReview.Core.Settings.ShortcutKeyCanonical.Canonicalize(text);
        if (string.IsNullOrEmpty(text) || text.Contains(',', StringComparison.Ordinal) || char.IsDigit(text[0]) || text[0] is '-' or '+') return false;
        if (!Enum.TryParse(text, ignoreCase: true, out KeyId parsed) || !Enum.IsDefined(parsed)) return false;
        if (IsReserved(parsed)) return false;
        key = parsed;
        return true;
    }

    /// <summary>Keys the capture box must let through (focus movement, cancel) and that can never be a shortcut.</summary>
    public static bool IsReserved(KeyId key) => key is KeyId.None or KeyId.Escape or KeyId.Tab or KeyId.ImeProcessed or KeyId.DeadCharProcessed
        or KeyId.System or KeyId.LeftCtrl or KeyId.RightCtrl or KeyId.LeftAlt or KeyId.RightAlt or KeyId.LeftShift or KeyId.RightShift or KeyId.LWin or KeyId.RWin;
}
