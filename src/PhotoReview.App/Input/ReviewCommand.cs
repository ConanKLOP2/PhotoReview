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
    Undo,
    ToggleCompare,
    RunAction,
    Recycle,
    Skip,
    ToggleFit,
    ZoomIn,
    ZoomOut,
    Next,
    Previous
}

/// <summary>
/// Mô tả một lệnh thực thi sau khi định tuyến từ phím tắt.
/// </summary>
public readonly record struct ReviewCommand(ReviewCommandType Type, int ActionIndex = -1)
{
    public static ReviewCommand Fullscreen => new(ReviewCommandType.Fullscreen);
    public static ReviewCommand ExitFullscreen => new(ReviewCommandType.ExitFullscreen);
    public static ReviewCommand Close => new(ReviewCommandType.Close);
    public static ReviewCommand NextFolder => new(ReviewCommandType.NextFolder);
    public static ReviewCommand PreviousFolder => new(ReviewCommandType.PreviousFolder);
    public static ReviewCommand FirstImage => new(ReviewCommandType.FirstImage);
    public static ReviewCommand Undo => new(ReviewCommandType.Undo);
    public static ReviewCommand ToggleCompare => new(ReviewCommandType.ToggleCompare);
    public static ReviewCommand Action(int index) => new(ReviewCommandType.RunAction, index);
    public static ReviewCommand Recycle => new(ReviewCommandType.Recycle);
    public static ReviewCommand Skip => new(ReviewCommandType.Skip);
    public static ReviewCommand ToggleFit => new(ReviewCommandType.ToggleFit);
    public static ReviewCommand ZoomIn => new(ReviewCommandType.ZoomIn);
    public static ReviewCommand ZoomOut => new(ReviewCommandType.ZoomOut);
    public static ReviewCommand Next => new(ReviewCommandType.Next);
    public static ReviewCommand Previous => new(ReviewCommandType.Previous);
}