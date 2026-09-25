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

    public static ShortcutMappings Default() => new();
}
