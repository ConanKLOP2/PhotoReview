using System.Reflection;
using PhotoReview.App;
using PhotoReview.App.Services;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// WP-19b (C-13): MainWindow no longer constructs <c>ClickZoomCustomDialog</c> itself; the "Custom..." zoom prompt goes through the
/// injected <see cref="IZoomPromptService"/> (the Win32 shell supplies its own), so the answer, not the dialog, drives the zoom.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class ZoomPromptSeamTests
{
    private sealed class FakePrompt(int? answer) : IZoomPromptService
    {
        public List<int> Asked { get; } = [];

        public int? PromptCustomZoom(int currentPercent)
        {
            Asked.Add(currentPercent);
            return answer;
        }
    }

    private static Task InvokeCustomZoom(MainWindow window)
        => (Task)typeof(MainWindow).GetMethod("OpenClickZoomCustomDialogAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;

    [Fact]
    public async Task CustomZoom_AsksTheInjectedPromptWithTheCurrentClickLevel_AndAppliesTheAnswer()
    {
        var prompt = new FakePrompt(250);
        await StaTestHost.RunAsync(async () =>
        {
            var window = TestAppHost.CreateMainWindow(initialPath: null, new TestHostHooks { ZoomPrompt = prompt });
            var before = window.Settings.ClickZoomPercent;

            await InvokeCustomZoom(window);

            Assert.Equal([before], prompt.Asked);
            Assert.Equal(250, window.Settings.ClickZoomPercent); // SetZoomAlsoSetsClickLevel is on by default
            window.Close();
        });
    }

    [Fact]
    public async Task CustomZoom_CancelledPrompt_ChangesNothing()
    {
        var prompt = new FakePrompt(null);
        await StaTestHost.RunAsync(async () =>
        {
            var window = TestAppHost.CreateMainWindow(initialPath: null, new TestHostHooks { ZoomPrompt = prompt });
            var before = window.Settings.ClickZoomPercent;

            await InvokeCustomZoom(window);

            Assert.Single(prompt.Asked);
            Assert.Equal(before, window.Settings.ClickZoomPercent);
            window.Close();
        });
    }
}
