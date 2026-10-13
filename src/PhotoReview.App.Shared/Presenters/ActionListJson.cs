using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoReview.App.Presenters;

/// <summary>
/// WP-35 G0: the JSON form of an action list as the Settings dialog (raw JSON box) and the Action Profiles editor
/// (import/export) use it. Source-generated (App.Shared is AOT-compatible); the options are the ones the old code-behind
/// built by hand: relaxed escaping for the text box (Vietnamese names stay readable), default escaping for the export file.
/// </summary>
internal static class ActionListJson
{
    private static readonly ActionListJsonContext TextBoxContext = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>The indented text shown in the Settings JSON box.</summary>
    public static string Serialize(IEnumerable<ReviewAction> actions) =>
        JsonSerializer.Serialize(actions.ToList(), TextBoxContext.ListReviewAction);

    /// <summary>The exported file content (indented, default escaping).</summary>
    public static string SerializeForExport(IEnumerable<ReviewAction> actions) =>
        JsonSerializer.Serialize(actions.ToList(), ActionListJsonContext.Default.ListReviewAction);

    /// <summary>Parses a list; the JSON literal <c>null</c> is an empty list. Throws <see cref="JsonException"/> on malformed text.</summary>
    public static List<ReviewAction> Deserialize(string text) =>
        JsonSerializer.Deserialize(text, ActionListJsonContext.Default.ListReviewAction) ?? [];

    /// <summary>Like <see cref="Deserialize"/> but the literal <c>null</c> stays null (the importer rejects it as "invalid file").</summary>
    public static List<ReviewAction>? DeserializeOrNull(string text) =>
        JsonSerializer.Deserialize(text, ActionListJsonContext.Default.ListReviewAction);

    /// <summary>
    /// The Save-time checks of a typed action list: every action has a name, a parsable shortcut and a defined operation,
    /// and no two actions resolve to the same canonical shortcut. Throws (never shown) when a check fails.
    /// </summary>
    public static void ThrowIfInvalid(IReadOnlyList<ReviewAction> actions)
    {
        if (actions.Any(action => string.IsNullOrWhiteSpace(action.Name) || string.IsNullOrWhiteSpace(action.Shortcut) ||
            !ShortcutKeyName.TryParse(action.Shortcut, out _) ||
            !Enum.IsDefined(action.Operation)))
            throw new JsonException("An action has no name, an invalid shortcut or an invalid operation.");
        if (actions.GroupBy(action => ShortcutKeyCanonical.Canonicalize(action.Shortcut), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new JsonException("Two actions use the same shortcut.");
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<ReviewAction>))]
internal sealed partial class ActionListJsonContext : JsonSerializerContext;
