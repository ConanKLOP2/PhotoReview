namespace PhotoReview.App.Tests.Services;

public sealed class WindowPlacementServiceTests
{
    [Theory(DisplayName = "Only normal and maximized show commands survive a placement restore")]
    [InlineData(0, 1)]   // SW_HIDE from a damaged file
    [InlineData(1, 1)]
    [InlineData(2, 1)]   // minimized
    [InlineData(3, 3)]
    [InlineData(7, 1)]   // minimize-no-activate
    [InlineData(-5, 1)]
    public void NormalizeShowCommand_AcceptsOnlyNormalAndMaximized(int input, int expected)
        => Assert.Equal(expected, WindowPlacementService.NormalizeShowCommand(input));
}
