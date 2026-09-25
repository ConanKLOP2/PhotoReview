namespace PhotoReview.Core.Settings;

public class ShortcutMappings
{
    public string Next { get; set; } = "Right";
    public string Previous { get; set; } = "Left";
    public string MoveToFolder2 { get; set; } = "Enter";
    public string SendToRecycleBin { get; set; } = "Delete";
    public string Compare { get; set; } = "C";
    public string NextFolder { get; set; } = "PageDown";
    public string PreviousFolder { get; set; } = "PageUp";
    public string FirstImage { get; set; } = "Home";
    public string ZoomIn { get; set; } = "Add";
    public string ZoomOut { get; set; } = "Subtract";
    public string ToggleFit { get; set; } = "F";
    public string Skip { get; set; } = "Space";
    public string Undo { get; set; } = "Z";
    public string Fullscreen { get; set; } = "F11";

    /// <summary>Go to the last image. Empty = disabled (see <see cref="OptionalNames"/>).</summary>
    public string LastImage { get; set; } = "End";

    /// <summary>Zoom to 100 % = one source pixel per device pixel (ADR 0008). Empty = disabled. <c>D1</c> is the "1" key.</summary>
    public string ZoomActualSize { get; set; } = "D1";

    /// <summary>Show/hide the on-image info overlays (<see cref="AppSettings.ShowInfoOverlay"/>). Empty = disabled.</summary>
    public string ToggleInfoOverlay { get; set; } = "I";

    /// <summary>
    /// Shortcuts that may be empty (= feature disabled). The older shortcuts are mandatory: an empty value is invalid.
    /// A field, not a property: code that reflects over the shortcut PROPERTIES (validator) must not see it.
    /// </summary>
    public static readonly IReadOnlyList<string> OptionalNames = [nameof(LastImage), nameof(ZoomActualSize), nameof(ToggleInfoOverlay)];

    public static bool IsOptional(string propertyName) => OptionalNames.Contains(propertyName, StringComparer.Ordinal);

    public static ShortcutMappings Default() => new();
}
