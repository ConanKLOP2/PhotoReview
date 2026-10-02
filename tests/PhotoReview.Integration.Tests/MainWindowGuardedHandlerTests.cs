using System.Reflection;
using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;
using PhotoReview.TestSupport;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// A4: the async-void handlers of <see cref="MainWindow"/> route their risky awaits through <c>RunGuardedAsync</c>, so a throwing
/// command is reported as the "action failed" status text instead of reaching the dispatcher's unhandled-exception handler.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class MainWindowGuardedHandlerTests
{
    private static readonly MethodInfo RunGuarded =
        typeof(MainWindow).GetMethod("RunGuardedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static Task Invoke(MainWindow window, string name, Func<Task> action) =>
        (Task)RunGuarded.Invoke(window, [name, action])!;

    [Fact(DisplayName = "A4: a throwing guarded command is turned into the action-failed status text, not an exception")]
    public async Task RunGuarded_Throws_SetsActionFailedStatus()
    {
        using var dataRoot = new DataRootFixture();
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                window = TestAppHost.CreateMainWindow(null);
                window.Show();

                await Invoke(window, "Move to folder", () => throw new InvalidOperationException("picker exploded"));

                var status = window.ViewModel.StatusText;
                Assert.Contains("Move to folder", status, StringComparison.Ordinal);
                Assert.Contains("picker exploded", status, StringComparison.Ordinal);
            });
        }
        finally
        {
            var opened = window;
            if (opened is not null)
                await StaTestHost.RunAsync(() =>
                {
                    try { opened.Close(); } catch (InvalidOperationException) { }
                    return Task.CompletedTask;
                });
        }
    }

    [Fact(DisplayName = "A4: a cancelled guarded command is not reported as a failure")]
    public async Task RunGuarded_Cancelled_LeavesStatusUntouched()
    {
        using var dataRoot = new DataRootFixture();
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                window = TestAppHost.CreateMainWindow(null);
                window.Show();
                var before = window.ViewModel.StatusText;

                await Invoke(window, "Open folder", () => Task.FromCanceled(new CancellationToken(true)));

                Assert.Equal(before, window.ViewModel.StatusText);
            });
        }
        finally
        {
            var opened = window;
            if (opened is not null)
                await StaTestHost.RunAsync(() =>
                {
                    try { opened.Close(); } catch (InvalidOperationException) { }
                    return Task.CompletedTask;
                });
        }
    }
}
