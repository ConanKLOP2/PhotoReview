using System.Collections.Frozen;

namespace PhotoReview.Core.Settings;

/// <summary>
/// Q-R25: one canonical spelling per physical key. WPF's <c>System.Windows.Input.Key</c> has several names sharing one
/// value (Return == Enter, Prior == PageUp, Next == PageDown ...); comparing configured shortcut strings would treat
/// "Return" and "Enter" as different although pressing the key fires both. Core cannot reference WPF, so the true
/// same-value aliases are listed here (verified against the <c>Key</c> enum by an App test). Different keys that merely
/// look alike (Add vs OemPlus) are NOT aliases. Shortcuts are single key names, no modifier combinations.
/// </summary>
public static class ShortcutKeyCanonical
{
    /// <summary>(alias, canonical) pairs: every alias is another name of the same <c>Key</c> value as its canonical name.</summary>
    public static readonly IReadOnlyList<(string Alias, string Canonical)> AliasPairs =
    [
        ("Return", "Enter"), ("Prior", "PageUp"), ("Next", "PageDown"), ("Capital", "CapsLock"), ("Snapshot", "PrintScreen"),
        ("KanaMode", "HangulMode"), ("HanjaMode", "KanjiMode"),
        ("Oem1", "OemSemicolon"), ("Oem2", "OemQuestion"), ("Oem3", "OemTilde"), ("Oem4", "OemOpenBrackets"),
        ("Oem5", "OemPipe"), ("Oem6", "OemCloseBrackets"), ("Oem7", "OemQuotes"), ("Oem102", "OemBackslash"),
        ("OemAttn", "DbeAlphanumeric"), ("OemFinish", "DbeKatakana"), ("OemCopy", "DbeHiragana"), ("OemAuto", "DbeSbcsChar"),
        ("OemEnlw", "DbeDbcsChar"), ("OemBackTab", "DbeRoman"), ("Attn", "DbeNoRoman"), ("CrSel", "DbeEnterWordRegisterMode"),
        ("ExSel", "DbeEnterImeConfigureMode"), ("EraseEof", "DbeFlushString"), ("Play", "DbeCodeInput"), ("Zoom", "DbeNoCodeInput"),
        ("NoName", "DbeDetermineString"), ("Pa1", "DbeEnterDialogConversionMode"),
    ];

    private static readonly FrozenDictionary<string, string> Map = BuildMap();

    private static FrozenDictionary<string, string> BuildMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, canonical) in AliasPairs)
        {
            map[alias] = canonical;
            map[canonical] = canonical; // fixes the casing of "enter" / "PAGEUP"
        }
        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The canonical spelling of a shortcut name: trimmed, aliases mapped to their canonical name (case-insensitive).
    /// Null/blank gives "" (= no shortcut); unknown names are returned trimmed and otherwise unchanged. Idempotent.
    /// </summary>
    public static string Canonicalize(string? name)
    {
        var text = name?.Trim();
        if (string.IsNullOrEmpty(text)) return "";
        return Map.TryGetValue(text, out var canonical) ? canonical : text;
    }

    /// <summary>True when both names press the same key (canonical form, case-insensitive). Two blank names are not "the same key".</summary>
    public static bool SameKey(string? a, string? b)
    {
        var left = Canonicalize(a);
        return left.Length > 0 && string.Equals(left, Canonicalize(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Canonicalises every shortcut string of <paramref name="settings"/> in place (mandatory, optional and action shortcuts).</summary>
    public static void CanonicalizeAll(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Shortcuts is { } shortcuts)
        {
            foreach (var property in typeof(ShortcutMappings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (property.PropertyType != typeof(string) || !property.CanWrite) continue;
                if (property.GetValue(shortcuts) is string value) property.SetValue(shortcuts, Canonicalize(value));
            }
        }
        foreach (var action in settings.Actions ?? [])
            if (action?.Shortcut is { } shortcut) action.Shortcut = Canonicalize(shortcut);
    }
}
