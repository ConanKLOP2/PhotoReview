namespace PhotoReview.Core.Settings;

/// <summary>
/// Outcome of <see cref="SettingsStore.ParseText"/>. <see cref="Salvaged"/> are the properties whose value could not be read
/// (reset to default); <see cref="ResetSettings"/> are those plus the ones <see cref="SettingsNormalizer"/> reset;
/// <see cref="DisabledShortcuts"/> are optional shortcuts turned off for a key collision.
/// </summary>
public sealed record SettingsParseResult(
    AppSettings Settings,
    IReadOnlyList<string> Salvaged,
    IReadOnlyList<string> ResetSettings,
    IReadOnlyList<string> DisabledShortcuts,
    System.Text.Json.JsonException? SalvageCause,
    bool IsJsonNull)
{
    /// <summary>What the user is told: reset names first, then <c>Shortcuts.&lt;name&gt;</c> for each disabled shortcut (the order <c>LastLoadRepairs</c> uses).</summary>
    public IReadOnlyList<string> Repairs => [.. ResetSettings, .. DisabledShortcuts.Select(name => "Shortcuts." + name)];
}