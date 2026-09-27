using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using PhotoReview.App;
using PhotoReview.Core.Localization;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// R11 (full-code-review-2026-09-27): <see cref="BenchmarkWindow.RunAsync"/> used to enumerate/stat the folder
/// synchronously, on the dialog's own dispatcher, before its first await -- so Cancel could not interrupt a slow
/// scan and a folder error path never went through the window's normal run/cancel button lifecycle. It now scans
/// on a background thread (see <c>EnumerateAndStat</c>) with the run's <c>CancellationTokenSource</c> created
/// before the scan starts (not after), so Cancel/Close can interrupt it. These tests drive the real window through
/// <see cref="StaTestHost"/> to check the status text and button lifecycle are unchanged for the error paths, and
/// that Cancel clicked immediately after Run can actually interrupt an in-progress scan.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class BenchmarkWindowEnumerationUiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact(DisplayName = "A missing folder reports BenchmarkStatusFolderMissing and leaves the run controls re-enabled")]
    public async Task RunAsync_FolderMissing_ReportsStatusAndReenablesControls()
    {
        using var temp = new TempRoot("bench-ui-missing");
        var missing = Path.Combine(temp.Path, "does-not-exist");

        await StaTestHost.RunAsync(async () =>
        {
            var window = new BenchmarkWindow(missing) { };
            window.RunButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var finished = await StaTestHost.WaitForAsync(() => window.RunButton.IsEnabled, Timeout);

            Assert.True(finished, $"The run did not finish. Status={window.StatusText.Text}");
            Assert.Equal(Tr.BenchmarkStatusFolderMissing, window.StatusText.Text);
            Assert.True(window.QuickCheckButton.IsEnabled);
            Assert.True(window.BrowseButton.IsEnabled);
            Assert.False(window.CancelButton.IsEnabled);
            window.Dispose();
        });
    }

    [Fact(DisplayName = "A folder with no supported images reports BenchmarkStatusNoImages and leaves the run controls re-enabled")]
    public async Task RunAsync_NoSupportedImages_ReportsStatusAndReenablesControls()
    {
        using var temp = new TempRoot("bench-ui-noimages");
        var folder = temp.Dir("empty");
        File.WriteAllBytes(Path.Combine(folder, "notes.txt"), [1, 2, 3]);

        await StaTestHost.RunAsync(async () =>
        {
            var window = new BenchmarkWindow(folder) { };
            window.RunButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var finished = await StaTestHost.WaitForAsync(() => window.RunButton.IsEnabled, Timeout);

            Assert.True(finished, $"The run did not finish. Status={window.StatusText.Text}");
            Assert.Equal(Tr.BenchmarkStatusNoImages, window.StatusText.Text);
            window.Dispose();
        });
    }

    [Fact(DisplayName = "Clicking Cancel immediately after Run can interrupt an in-progress folder scan instead of only the profile run that used to follow it")]
    public async Task RunAsync_CancelClickedDuringScan_EndsAsCanceled()
    {
        using var temp = new TempRoot("bench-ui-cancel-scan");
        var folder = temp.Dir("many-files");
        // Enough top-level entries that the background enumeration takes a real, non-zero (if small) amount of
        // wall-clock time -- giving the Cancel click queued right after Run a genuine chance to land first,
        // without asserting on any specific duration.
        for (var i = 0; i < 6000; i++) File.WriteAllBytes(Path.Combine(folder, $"f{i:00000}.dat"), []);

        await StaTestHost.RunAsync(async () =>
        {
            var window = new BenchmarkWindow(folder) { };
            window.RunButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.CancelButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var finished = await StaTestHost.WaitForAsync(() => window.RunButton.IsEnabled, Timeout);

            Assert.True(finished, $"The run did not finish. Status={window.StatusText.Text}");
            Assert.Equal(Tr.BenchmarkStatusCanceled, window.StatusText.Text);
            window.Dispose();
        });
    }
}
