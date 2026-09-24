using PhotoReview.App.Input;
using Xunit;

namespace PhotoReview.App.Tests.Input;

public sealed class ReviewCommandAutoRepeatTests
{
    [Theory(DisplayName = "File-changing commands ignore keyboard auto-repeat")]
    [InlineData(ReviewCommandType.Recycle)]
    [InlineData(ReviewCommandType.RunAction)]
    [InlineData(ReviewCommandType.Undo)]
    public void FileChangingCommandsIgnoreAutoRepeat(ReviewCommandType type) =>
        Assert.True(type.IgnoresAutoRepeat());

    [Theory(DisplayName = "Navigation and view commands keep auto-repeat for fast browsing")]
    [InlineData(ReviewCommandType.Next)]
    [InlineData(ReviewCommandType.Previous)]
    [InlineData(ReviewCommandType.ZoomIn)]
    [InlineData(ReviewCommandType.ZoomOut)]
    [InlineData(ReviewCommandType.FirstImage)]
    [InlineData(ReviewCommandType.Skip)]
    [InlineData(ReviewCommandType.NextFolder)]
    [InlineData(ReviewCommandType.PreviousFolder)]
    public void NavigationCommandsKeepAutoRepeat(ReviewCommandType type) =>
        Assert.False(type.IgnoresAutoRepeat());
}
