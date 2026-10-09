using PhotoReview.App.Input;

namespace PhotoReview.App.Tests.Input;

public sealed class ReviewCommandAutoRepeatTests
{
    [Theory(DisplayName = "File-changing commands ignore keyboard auto-repeat")]
    [InlineData(ReviewCommandType.Recycle)]
    [InlineData(ReviewCommandType.RunAction)]
    [InlineData(ReviewCommandType.Undo)]
    [InlineData(ReviewCommandType.MoveToFolder)]
    [InlineData(ReviewCommandType.CopyToFolder)]
    [InlineData(ReviewCommandType.ToggleInfoOverlay)] // flips and saves a setting
    [InlineData(ReviewCommandType.Fullscreen)] // pure toggles: holding the key must not flicker them
    [InlineData(ReviewCommandType.ToggleCompare)]
    [InlineData(ReviewCommandType.ClickZoom)]
    [InlineData(ReviewCommandType.ToggleKeepZoom)] // PR-B: flips and saves a setting, same as ToggleInfoOverlay
    public void FileChangingCommandsIgnoreAutoRepeat(ReviewCommandType type) =>
        Assert.True(type.IgnoresAutoRepeat());

    [Theory(DisplayName = "Navigation and view commands keep auto-repeat for fast browsing")]
    [InlineData(ReviewCommandType.Next)]
    [InlineData(ReviewCommandType.Previous)]
    [InlineData(ReviewCommandType.ZoomIn)]
    [InlineData(ReviewCommandType.ZoomOut)]
    [InlineData(ReviewCommandType.FirstImage)]
    [InlineData(ReviewCommandType.LastImage)]
    [InlineData(ReviewCommandType.ZoomActualSize)]
    [InlineData(ReviewCommandType.Skip)]
    [InlineData(ReviewCommandType.NextFolder)]
    [InlineData(ReviewCommandType.PreviousFolder)]
    [InlineData(ReviewCommandType.FitWidth)] // PR-B: like ToggleFit, harmless to re-apply on repeat
    [InlineData(ReviewCommandType.FitWidth2)]
    [InlineData(ReviewCommandType.FitHeight)]
    public void NavigationCommandsKeepAutoRepeat(ReviewCommandType type) =>
        Assert.False(type.IgnoresAutoRepeat());
}
