using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;
using PhotoReview.TestSupport;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// The right-click zoom items of the real MainWindow ("Fit", "Zoom to N%", the "Zoom levels" submenu) and the
/// Settings zoom card's shortcut summary. Vietnamese is the ambient language (LocalizationModuleInit).
/// </summary>
[Collection("GlobalState")]
public sealed class ZoomContextMenuTests
{
    private static void OpenMenu(MainWindow window) =>
        window.ImageContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, window.ImageContextMenu));

    private static void OpenSubmenu(MainWindow window) =>
        window.ClickZoomMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, window.ClickZoomMenu));

    private static async Task WithWindowAsync(string? folder, Func<MainWindow, Task> body)
    {
        using var dataRoot = new DataRootFixture();
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                var presented = new List<string>();
                window = TestAppHost.CreateMainWindow(folder, new TestHostHooks { OnPresented = presented.Add });
                if (folder is not null)
                    Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, TimeSpan.FromSeconds(10)), "Image never presented");
                await body(window);
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

    [Fact]
    public async Task ContextMenuOpen_ShowsConfiguredLevelAndShortcuts_AndEmptyWhenCleared()
    {
        await WithWindowAsync(null, window =>
        {
            window.Settings.ClickZoomPercent = 150;
            window.Settings.Shortcuts.ClickZoom = "Z";
            window.Settings.Shortcuts.ToggleFit = "F";
            OpenMenu(window);
            Assert.Equal("Thu phóng về 150%", window.ZoomToLevelMenuItem.Header);
            Assert.Equal("Z", window.ZoomToLevelMenuItem.InputGestureText);
            Assert.Equal("F", window.FitMenuItem.InputGestureText);

            // Refreshed on every open, not just the first.
            window.Settings.ClickZoomPercent = 75;
            window.Settings.Shortcuts.ClickZoom = "";
            window.Settings.Shortcuts.ToggleFit = "";
            OpenMenu(window);
            Assert.Equal("Thu phóng về 75%", window.ZoomToLevelMenuItem.Header);
            Assert.Equal("", window.ZoomToLevelMenuItem.InputGestureText);
            Assert.Equal("", window.FitMenuItem.InputGestureText);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ZoomToLevelClick_ZoomsToConfiguredLevel_AndDoesNotChangeTheSetting()
    {
        using var folder = new TempRoot("zoom-menu");
        var png = Path.Combine(folder.Path, "square.png");
        WriteImage(png);
        await WithWindowAsync(folder.Path, async window =>
        {
            window.Settings.ClickZoomPercent = 150;
            Assert.True(window.ViewModel.Viewer.IsFit);

            window.ZoomToLevelMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, window.ZoomToLevelMenuItem));

            Assert.True(await StaTestHost.WaitForAsync(() => Math.Abs(window.ViewModel.Viewer.Zoom - 1.5) < 0.001 && !window.ViewModel.Viewer.IsFit,
                TimeSpan.FromSeconds(5)), $"Zoom={window.ViewModel.Viewer.Zoom}");
            Assert.Equal(150, window.Settings.ClickZoomPercent);
        });
    }

    [Fact]
    public async Task ZoomLevelsSubmenu_IsPopulatedFromTheConstructor_AndChecksTheCurrentLevelOnOpen()
    {
        await WithWindowAsync(null, window =>
        {
            Assert.True(window.ClickZoomMenu.HasItems);
            var presets = window.ClickZoomMenu.Items.OfType<MenuItem>().Where(i => i.Tag is int).ToList();
            Assert.NotEmpty(presets);

            window.Settings.ClickZoomPercent = 150;
            OpenSubmenu(window);
            Assert.Equal([150], presets.Where(i => i.IsChecked).Select(i => (int)i.Tag!));

            window.Settings.ClickZoomPercent = 30;
            OpenSubmenu(window);
            Assert.Equal([30], presets.Where(i => i.IsChecked).Select(i => (int)i.Tag!));

            // A custom level that is not a preset checks nothing.
            window.Settings.ClickZoomPercent = 175;
            OpenSubmenu(window);
            Assert.DoesNotContain(presets, i => i.IsChecked);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ZoomLevelsSubmenu_RefreshesTextsOnOpen_AfterALanguageSwitch()
    {
        try
        {
            await WithWindowAsync(null, async window =>
            {
                OpenSubmenu(window);
                var custom = window.ClickZoomMenu.Items.OfType<MenuItem>().Last();
                var preset150 = window.ClickZoomMenu.Items.OfType<MenuItem>().Single(i => i.Tag is 150);
                Assert.Equal("Tùy chỉnh…", custom.Header);
                Assert.Equal("Thu phóng đến 150 phần trăm", AutomationProperties.GetName(preset150));

                using (TestLocalization.Use(TestLocalization.English))
                {
                    await StaTestHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                    OpenSubmenu(window);
                    Assert.Equal("Custom…", custom.Header);
                    Assert.Equal("Zoom to 150 percent", AutomationProperties.GetName(preset150));
                }
            });
        }
        finally
        {
            TestLocalization.UseVietnamese();
        }
    }

    [Fact]
    public async Task SettingsZoomShortcutSummary_FollowsTheTypedKeys_AndShowsNoneWhenEmpty()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings());
            try
            {
                window.ClickZoomText.Text = "Z";
                window.ToggleFitText.Text = "F";
                var lines = window.ZoomShortcutsSummary.Text.Split(Environment.NewLine);
                Assert.Contains("Bật/tắt thu phóng khi nhấn: Z", lines);
                Assert.Contains("Vừa khung ảnh: F", lines);

                window.ClickZoomText.Text = "";
                window.ToggleFitText.Text = "  ";
                lines = window.ZoomShortcutsSummary.Text.Split(Environment.NewLine);
                Assert.Contains("Bật/tắt thu phóng khi nhấn: chưa đặt", lines);
                Assert.Contains("Vừa khung ảnh: chưa đặt", lines);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    private static void WriteImage(string path)
    {
        var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
