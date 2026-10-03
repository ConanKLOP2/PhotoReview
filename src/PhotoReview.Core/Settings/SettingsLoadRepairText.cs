using PhotoReview.Core.Localization;

namespace PhotoReview.Core.Settings;

/// <summary>
/// Builds the start-up dialog text for <see cref="SettingsStore.LastLoadRepairs"/>. Entries named <c>Shortcuts.&lt;Name&gt;</c>
/// are optional shortcuts that were TURNED OFF (not reset) because their key collided, so they get their own message. <paramref name="forImport"/> words the reset line for Settings > Import instead of config.json.
/// </summary>
public static class SettingsLoadRepairText
{
    private const string ShortcutPrefix = "Shortcuts.";

    public static string Build(IReadOnlyList<string> repairs, bool forImport = false)
    {
        ArgumentNullException.ThrowIfNull(repairs);
        var reset = repairs.Where(r => !r.StartsWith(ShortcutPrefix, StringComparison.Ordinal)).ToList();
        var lines = new List<string>();
        if (reset.Count > 0) lines.Add(forImport ? Tr.DialogImportSettingsRepaired(string.Join(", ", reset)) : Tr.SettingsLoadRepaired(string.Join(", ", reset)));
        foreach (var repair in repairs.Where(r => r.StartsWith(ShortcutPrefix, StringComparison.Ordinal)))
            lines.Add(Tr.SettingsLoadShortcutDisabled(repair[ShortcutPrefix.Length..]));
        return string.Join(Environment.NewLine, lines);
    }
}
