using System.Windows.Input;

namespace PhotoReview.App.Services;

/// <summary>
/// The one place that turns a configured shortcut string into a WPF <see cref="Key"/>. <c>Enum.TryParse</c> alone accepts
/// numbers ("999") and comma lists ("A,B"), and names that can never be pressed as a command (Escape closes the app in the
/// router, Tab moves focus, ImeProcessed/None/modifiers are not commands), so a saved shortcut could silently never fire.
/// </summary>
public static class ShortcutKeyName
{
    public static bool TryParse(string? name, out Key key)
    {
        key = Key.None;
        var text = name?.Trim();
        if (string.IsNullOrEmpty(text) || text.Contains(',', StringComparison.Ordinal) || char.IsDigit(text[0]) || text[0] is '-' or '+') return false;
        if (!Enum.TryParse(text, ignoreCase: true, out Key parsed) || !Enum.IsDefined(parsed)) return false;
        if (IsReserved(parsed)) return false;
        key = parsed;
        return true;
    }

    /// <summary>Keys the capture box must let through (focus movement, cancel) and that can never be a shortcut.</summary>
    public static bool IsReserved(Key key) => key is Key.None or Key.Escape or Key.Tab or Key.ImeProcessed or Key.DeadCharProcessed
        or Key.System or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
}
