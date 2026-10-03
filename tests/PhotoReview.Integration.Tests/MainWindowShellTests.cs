using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using PhotoReview.App;
using PhotoReview.Core.Abstractions;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>Review 2026-10 PR 8: fullscreen state transitions (RV-A02), Settings.Changed unsubscription (RV-A16), batch-review sizes (RV-A13).</summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class MainWindowShellTests
{
    private static readonly JsonSerializerOptions PlacementJson = new() { IncludeFields = true };

    private static Task OnSta(Action body) => StaTestHost.RunAsync(() => { body(); return Task.CompletedTask; });

    /// <summary>Off-screen, never activated: a window the test can put into any WindowState.</summary>
    private static MainWindow CreateShown(string? placementFile = null)
    {
        var window = TestAppHost.CreateMainWindow(null, placementFile: placementFile);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
        return window;
    }

    [Theory(DisplayName = "RV-A02: fullscreen is a borderless Maximized from Normal, Maximized or Minimized, and exit restores the prior state")]
    [InlineData(WindowState.Normal, WindowState.Normal)]
    [InlineData(WindowState.Maximized, WindowState.Maximized)]
    [InlineData(WindowState.Minimized, WindowState.Normal)]
    public async Task Fullscreen_FromEachState_EntersBorderlessMaximizedAndExitRestores(WindowState before, WindowState expectedAfterExit)
    {
        using var dataRoot = new DataRootFixture();
        await OnSta(() =>
        {
            var window = CreateShown();
            try
            {
                window.WindowState = before;

                window.ViewModel.Viewer.IsFullscreen = true;
                Assert.Equal(WindowState.Maximized, window.WindowState);
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Assert.Equal(ResizeMode.NoResize, window.ResizeMode);

                window.ViewModel.Viewer.IsFullscreen = false;
                Assert.Equal(expectedAfterExit, window.WindowState);
                Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
                Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
            }
            finally { window.Close(); }
        });
    }

    [Fact(DisplayName = "RV-A02: closing while fullscreen from a Normal window saves the normal (pre-fullscreen) placement")]
    public async Task Close_WhileFullscreenFromNormal_SavesNormalPlacement()
    {
        using var dataRoot = new DataRootFixture();
        var file = dataRoot.Root.Combine("placement.json");
        await OnSta(() =>
        {
            var window = CreateShown(file);
            window.ViewModel.Viewer.IsFullscreen = true;
            window.Close();
        });
        var placement = JsonSerializer.Deserialize<WindowPlacementService.WindowPlacement>(File.ReadAllText(file),
            PlacementJson)!;
        Assert.Equal(1, placement.ShowCommand); // SW_SHOWNORMAL, not the fullscreen Maximized
    }

    [Fact(DisplayName = "RV-A16: a closed MainWindow no longer reacts to SettingsStore.Changed")]
    public async Task Closed_SettingsChanged_DoesNotReachTheWindow()
    {
        using var dataRoot = new DataRootFixture();
        await OnSta(() =>
        {
            var window = TestAppHost.CreateMainWindow(null);
            var store = (SettingsStore)typeof(MainWindow)
                .GetField("_settingsStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var viewModel = window.ViewModel;
            window.Show();
            window.Close();

            var settingsAfterClose = new AppSettings { ShowExifInfo = !store.Current.ShowExifInfo };
            store.Save(settingsAfterClose);

            Assert.NotSame(settingsAfterClose, viewModel.Settings); // the handler would have assigned it
        });
    }

    [Fact(DisplayName = "RV-A13: BatchReviewWindow lists the supplied sizes without touching the disk (the paths do not exist)")]
    public async Task BatchReviewWindow_SuppliedSizes_ShownWithoutStat()
    {
        using var temp = new TempRoot("batch-sizes");
        await OnSta(() =>
        {
            var missing = Path.Combine(temp.Path, "not-on-disk.jpg");
            var window = new BatchReviewWindow([new BatchReviewItem(missing, 1_234_567), new BatchReviewItem(missing + "2", null)]);
            try
            {
                var lines = window.FilesList.ItemsSource.Cast<string>().ToList();
                Assert.Contains(1_234_567L.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), lines[0], StringComparison.Ordinal);
                Assert.DoesNotContain(1_234_567L.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), lines[1], StringComparison.Ordinal);
            }
            finally { window.Close(); }
        });
    }
}
