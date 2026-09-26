namespace PhotoReview.App.Input;

/// <summary>
/// Định danh loại lệnh điều hướng và xử lý ảnh.
/// </summary>
public enum ReviewCommandType
{
    Fullscreen,
    ExitFullscreen,
    Close,
    NextFolder,
    PreviousFolder,
    FirstImage,
    LastImage,
    Undo,
    ToggleCompare,
    RunAction,
    Recycle,
    Skip,
    ToggleFit,
    ZoomIn,
    ZoomOut,
    ZoomActualSize,
    ToggleInfoOverlay,
    Next,
    Previous,
    MoveToFolder,
    CopyToFolder,
    ClickZoom
}

/// <summary>Rules about how a resolved command reacts to keyboard auto-repeat.</summary>
public static class ReviewCommandTypeExtensions
{
    /// <summary>
    /// True for commands that change files (Recycle, action profiles, Undo): holding the key must not repeat
    /// them, otherwise photos are sent away faster than they can be looked at (R2-F-06).
    /// Navigation and zoom keys are deliberately not listed so they keep repeating for fast browsing.
    /// ToggleInfoOverlay is listed too: it flips and SAVES a setting, so holding the key would flicker the overlay
    /// and rewrite config.json on every repeat.
    /// </summary>
    public static bool IgnoresAutoRepeat(this ReviewCommandType type) =>
        type is ReviewCommandType.Recycle or ReviewCommandType.RunAction or ReviewCommandType.Undo
            or ReviewCommandType.MoveToFolder or ReviewCommandType.CopyToFolder or ReviewCommandType.ToggleInfoOverlay;
}

/// <summary>
/// Mô tả một lệnh thực thi sau khi định tuyến từ phím tắt.
/// </summary>
/// <param name="ForcePicker">"Move to… / Copy to…" pressed with Shift: always open the folder picker, even when the last folder is reused.</param>
public readonly record struct ReviewCommand(ReviewCommandType Type, int ActionIndex = -1, bool ForcePicker = false)
{
    public static ReviewCommand Fullscreen => new(ReviewCommandType.Fullscreen);
    public static ReviewCommand ExitFullscreen => new(ReviewCommandType.ExitFullscreen);
    public static ReviewCommand Close => new(ReviewCommandType.Close);
    public static ReviewCommand NextFolder => new(ReviewCommandType.NextFolder);
    public static ReviewCommand PreviousFolder => new(ReviewCommandType.PreviousFolder);
    public static ReviewCommand FirstImage => new(ReviewCommandType.FirstImage);
    public static ReviewCommand LastImage => new(ReviewCommandType.LastImage);
    public static ReviewCommand Undo => new(ReviewCommandType.Undo);
    public static ReviewCommand ToggleCompare => new(ReviewCommandType.ToggleCompare);
    public static ReviewCommand Action(int index) => new(ReviewCommandType.RunAction, index);
    public static ReviewCommand Recycle => new(ReviewCommandType.Recycle);
    public static ReviewCommand Skip => new(ReviewCommandType.Skip);
    public static ReviewCommand ToggleFit => new(ReviewCommandType.ToggleFit);
    public static ReviewCommand ZoomIn => new(ReviewCommandType.ZoomIn);
    public static ReviewCommand ZoomOut => new(ReviewCommandType.ZoomOut);
    public static ReviewCommand ZoomActualSize => new(ReviewCommandType.ZoomActualSize);
    public static ReviewCommand ToggleInfoOverlay => new(ReviewCommandType.ToggleInfoOverlay);
    public static ReviewCommand Next => new(ReviewCommandType.Next);
    public static ReviewCommand Previous => new(ReviewCommandType.Previous);
    public static ReviewCommand MoveToFolder(bool forcePicker) => new(ReviewCommandType.MoveToFolder, ForcePicker: forcePicker);
    public static ReviewCommand CopyToFolder(bool forcePicker) => new(ReviewCommandType.CopyToFolder, ForcePicker: forcePicker);
    public static ReviewCommand ClickZoom => new(ReviewCommandType.ClickZoom);
}