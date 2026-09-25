using PhotoReview.App;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// CORE-03 / Q-R2, Settings side: Apply refuses a relative destination that escapes the photo folder, so the
/// window stays open (DialogResult unset) and nothing reaches the settings.
/// </summary>
[Collection("GlobalState")]
public sealed class ActionProfilesDestinationTests
{
    [Fact]
    public async Task Apply_WithEscapingDestination_ShowsReasonAndKeepsWindowOpen()
    {
        await StaTestHost.RunAsync(() =>
        {
            var bad = new ReviewAction { Name = "Loai-9", Shortcut = "F9", Operation = FileOperationType.Move, Destination = @"..\x" };
            var window = new ActionProfilesWindow([bad]);
            try
            {
                Assert.Equal(Tr.ActionProfilesErrorDestinationEscapes("Loai-9"), ActionProfilesWindow.FindDestinationProblem(window.Actions));
                Assert.Null(window.DialogResult);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DefaultActions_PassDestinationValidation()
    {
        await StaTestHost.RunAsync(() =>
        {
            Assert.Null(ActionProfilesWindow.FindDestinationProblem(ReviewAction.Defaults()));
            Assert.NotNull(ActionProfilesWindow.FindDestinationProblem([new ReviewAction { Name = "x", Destination = "a|b", Operation = FileOperationType.Copy }]));
            Assert.Null(ActionProfilesWindow.FindDestinationProblem([new ReviewAction { Name = "r", Destination = "", Operation = FileOperationType.Recycle }]));
            return Task.CompletedTask;
        });
    }
}
