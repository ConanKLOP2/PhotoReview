using System.Windows;
using System.Windows.Controls.Primitives;
using PhotoReview.App;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Updates;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>Settings > General > Updates: behavior of the manual check with a fake checker (no network).</summary>
[Collection("GlobalState")]
public sealed class SettingsWindowUpdateCheckTests
{
    private sealed class FakeChecker(Func<TaskCompletionSource<UpdateCheckResult>, Task<UpdateCheckResult>>? custom = null) : IUpdateChecker
    {
        public TaskCompletionSource<UpdateCheckResult> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task<UpdateCheckResult> CheckAsync(string? currentVersion, CancellationToken cancellationToken)
        {
            Calls++;
            return custom is null ? Gate.Task : custom(Gate);
        }
    }

    private static void Click(ButtonBase button)
    {
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    }

    private static Task Run(FakeChecker checker, Func<SettingsWindow, Task> body) => StaTestHost.RunAsync(async () =>
    {
        var window = new SettingsWindow(new AppSettings(), updateChecker: checker);
        try { await body(window); }
        finally { window.Close(); }
    });

    [Fact(DisplayName = "Update button is disabled while checking, then shows the up-to-date status without the open-page button")]
    public async Task UpToDate_ShowsStatus_NoOpenButton()
    {
        var checker = new FakeChecker();
        await Run(checker, async window =>
        {
            Assert.True(window.CheckUpdateButton.IsEnabled);
            Assert.Equal(Visibility.Collapsed, window.OpenUpdatePageButton.Visibility);
            Assert.Equal(string.Empty, window.UpdateStatusText.Text);

            Click(window.CheckUpdateButton);
            Assert.False(window.CheckUpdateButton.IsEnabled);
            Assert.Equal(Tr.UpdateCheckChecking, window.UpdateStatusText.Text);
            Assert.Equal(1, checker.Calls);

            Click(window.CheckUpdateButton); // ignored while running
            Assert.Equal(1, checker.Calls);

            checker.Gate.SetResult(UpdateCheckResult.UpToDate("2.0.93"));
            await window.UpdateCheckTask;

            Assert.True(window.CheckUpdateButton.IsEnabled);
            Assert.Equal(Tr.UpdateStatusUpToDate("2.0.93"), window.UpdateStatusText.Text);
            Assert.Equal(Visibility.Collapsed, window.OpenUpdatePageButton.Visibility);
        });
    }

    [Fact(DisplayName = "UpdateAvailable shows the status and the open-page button, which opens only the validated URL")]
    public async Task UpdateAvailable_ShowsOpenButton_AndOpensUrl()
    {
        var checker = new FakeChecker();
        const string url = "https://github.com/ConanKLOP2/PhotoReview/releases/tag/v2.0.94";
        await Run(checker, async window =>
        {
            var opened = new List<string>();
            window.OpenUrl = opened.Add;

            Click(window.CheckUpdateButton);
            checker.Gate.SetResult(UpdateCheckResult.UpdateAvailable("2.0.94", url));
            await window.UpdateCheckTask;

            Assert.Equal(Visibility.Visible, window.OpenUpdatePageButton.Visibility);
            Assert.Contains("2.0.94", window.UpdateStatusText.Text, StringComparison.Ordinal);
            Assert.Empty(opened); // nothing opens until the user clicks

            Click(window.OpenUpdatePageButton);
            Assert.Equal([url], opened);
        });
    }

    [Fact(DisplayName = "An update result carrying a non-project URL never shows or uses the open-page button")]
    public async Task UpdateAvailable_ForeignUrl_NoOpenButton()
    {
        var checker = new FakeChecker();
        await Run(checker, async window =>
        {
            var opened = new List<string>();
            window.OpenUrl = opened.Add;
            Click(window.CheckUpdateButton);
            checker.Gate.SetResult(UpdateCheckResult.UpdateAvailable("2.0.94", "https://evil.example/x.exe"));
            await window.UpdateCheckTask;

            Assert.Equal(Visibility.Collapsed, window.OpenUpdatePageButton.Visibility);
            Click(window.OpenUpdatePageButton);
            Assert.Empty(opened);
        });
    }

    [Fact(DisplayName = "The open-page click re-validates the URL: a foreign URL is never opened even if it got stored")]
    public async Task OpenPage_RevalidatesUrl()
    {
        await Run(new FakeChecker(), window =>
        {
            var opened = new List<string>();
            window.OpenUrl = opened.Add;
            typeof(SettingsWindow).GetField("_updateUrl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(window, "https://evil.example/x.exe");
            Click(window.OpenUpdatePageButton);
            Assert.Empty(opened);
            return Task.CompletedTask;
        });
    }

    [Theory(DisplayName = "Each failure code shows its own message and re-enables the button")]
    [InlineData(UpdateFailure.Offline)]
    [InlineData(UpdateFailure.Timeout)]
    [InlineData(UpdateFailure.RateLimited)]
    [InlineData(UpdateFailure.BadResponse)]
    public async Task Failed_ShowsMessage(UpdateFailure failure)
    {
        var checker = new FakeChecker();
        await Run(checker, async window =>
        {
            Click(window.CheckUpdateButton);
            checker.Gate.SetResult(UpdateCheckResult.Failed(failure));
            await window.UpdateCheckTask;

            var expected = failure switch
            {
                UpdateFailure.Offline => Tr.UpdateStatusFailedOffline,
                UpdateFailure.Timeout => Tr.UpdateStatusFailedTimeout,
                UpdateFailure.RateLimited => Tr.UpdateStatusFailedRateLimited,
                _ => Tr.UpdateStatusFailedBadResponse,
            };
            Assert.Equal(expected, window.UpdateStatusText.Text);
            Assert.True(window.CheckUpdateButton.IsEnabled);
            Assert.Equal(Visibility.Collapsed, window.OpenUpdatePageButton.Visibility);
        });
    }

    [Fact(DisplayName = "A checker that throws is reported as a failure, not a crash")]
    public async Task CheckerThrows_ShowsFailure()
    {
        var checker = new FakeChecker(_ => throw new InvalidOperationException("boom"));
        await Run(checker, async window =>
        {
            Click(window.CheckUpdateButton);
            await window.UpdateCheckTask;
            Assert.Equal(Tr.UpdateStatusFailedBadResponse, window.UpdateStatusText.Text);
            Assert.True(window.CheckUpdateButton.IsEnabled);
        });
    }
}
