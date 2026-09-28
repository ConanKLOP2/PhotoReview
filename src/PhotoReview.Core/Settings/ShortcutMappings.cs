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

    /// <summary>"Move to…": asks for a folder (or reuses the last one) and moves the current photo there. Empty = disabled.</summary>
    public string MoveToFolder { get; set; } = "M";

    /// <summary>"Copy to…": asks for a folder (or reuses the last one) and copies the current photo there. Empty = disabled.</summary>
    public string CopyToFolder { get; set; } = "Y";

    /// <summary>Go to the last image. Empty = disabled (see <see cref="OptionalNames"/>).</summary>
    public string LastImage { get; set; } = "End";

    /// <summary>Zoom to 100 % = one source pixel per device pixel (ADR 0008). Empty = disabled. <c>D1</c> is the "1" key.</summary>
    public string ZoomActualSize { get; set; } = "D1";

    /// <summary>Show/hide the on-image info overlays (<see cref="AppSettings.ShowInfoOverlay"/>). Empty = disabled.</summary>
    public string ToggleInfoOverlay { get; set; } = "I";

    /// <summary>
    /// Toggles between Fit and <see cref="AppSettings.ClickZoomPercent"/>, anchored at the viewport centre -- the
    /// keyboard equivalent of a mouse click-to-zoom (works regardless of <see cref="AppSettings.ClickToZoomEnabled"/>,
    /// which only governs the mouse click). Empty = disabled. <c>D2</c> is the "2" key.
    /// </summary>
    public string ClickZoom { get; set; } = "D2";

    /// <summary>Fits the viewport width exactly (<see cref="PhotoReview.Core.Model.InitialViewMode.FitWidth"/>). Empty = disabled.</summary>
    public string FitWidth { get; set; } = "W";

    /// <summary>Fits the viewport height exactly (<see cref="PhotoReview.Core.Model.InitialViewMode.FitHeight"/>). Empty = disabled.</summary>
    public string FitHeight { get; set; } = "H";

    /// <summary>Toggles <see cref="AppSettings.KeepZoomAcrossImages"/> and persists it (same pattern as <see cref="ToggleInfoOverlay"/>). Empty = disabled.</summary>
    public string ToggleKeepZoom { get; set; } = "K";

    /// <summary>
    /// Q-R42: opens the folder picker (same action as the "Open folder…" menu item). Held together with Ctrl (the
    /// router requires the Control modifier, like <see cref="Undo"/> does for its own key) so a bare "O" keeps
    /// working for anything else. Empty = disabled. Default <c>Ctrl+O</c>.
    /// </summary>
    public string OpenFolder { get; set; } = "O";

    /// <summary>
    /// Q-R43: opens the "Custom zoom…" dialog (same action as the Zoom submenu's Custom… item). <c>Z</c> (the key the
    /// user suggested) is already <see cref="Undo"/>'s Ctrl+Z, so this uses <c>D3</c> ("3") instead, next to
    /// <see cref="ZoomActualSize"/> (<c>D1</c>) and <see cref="ClickZoom"/> (<c>D2</c>). Empty = disabled.
    /// </summary>
    public string CustomZoom { get; set; } = "D3";

    /// <summary>
    /// Shortcuts that may be empty (= feature disabled). The older shortcuts are mandatory: an empty value is invalid.
    /// A field, not a property: code that reflects over the shortcut PROPERTIES (validator) must not see it.
    /// </summary>
    public static readonly IReadOnlyList<string> OptionalNames =
        [nameof(LastImage), nameof(ZoomActualSize), nameof(ToggleInfoOverlay), nameof(MoveToFolder), nameof(CopyToFolder), nameof(ClickZoom),
         nameof(FitWidth), nameof(FitHeight), nameof(ToggleKeepZoom), nameof(OpenFolder), nameof(CustomZoom)];

    public static bool IsOptional(string propertyName) => OptionalNames.Contains(propertyName, StringComparer.Ordinal);

    public static ShortcutMappings Default() => new();
}
