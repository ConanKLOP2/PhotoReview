using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;
using PhotoReview.TestSupport;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// PR-C (feat/context-menu-redesign): the folder group's visibility (<see cref="AppSettings.ShowFolderMenuItems"/>),
/// the zoom cluster's visibility (<see cref="AppSettings.ShowZoomMenuItems"/>), the "Zoom" submenu's "also set as
/// click zoom level" toggle (<see cref="AppSettings.SetZoomAlsoSetsClickLevel"/>) and "set current zoom as click
/// level". Vietnamese is the ambient language (LocalizationModuleInit).
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class ContextMenuRedesignTests
{
    private static void OpenMenu(MainWindow window) =>
        window.ImageContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, window.ImageContextMenu));

    private static void OpenSubmenu(MainWindow window) =>
        window.ZoomMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, window.ZoomMenu));

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));

    /// <summary>The only IsCheckable item in the Zoom submenu with no preset Tag (see MainWindow.BuildZoomMenu).</summary>
    private static MenuItem AlsoSetClickLevelItem(MainWindow window) =>
        window.ZoomMenu.Items.OfType<MenuItem>().Single(i => i.IsCheckable && i.Tag is null);

    /// <summary>The last item BuildZoomMenu adds.</summary>
    private static MenuItem SetCurrentAsClickLevelItem(MainWindow window) =>
        window.ZoomMenu.Items.OfType<MenuItem>().Last();

    private static MenuItem Preset(MainWindow window, int percent) =>
        window.ZoomMenu.Items.OfType<MenuItem>().Single(i => i.Tag is int tag && tag == percent);

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

    /// <summary>
    /// Q-R38 structural layout: Undo first, Move to Recycle Bin (primary destructive action), then the togglable zoom cluster (Fit/Zoom-to-N%/Zoom submenu), then
    /// the togglable folder group, then Settings last -- always in this fixed order and always present in
    /// <c>Items</c> (only Move to Recycle Bin, the zoom cluster's four elements and the folder group's four elements toggle
    /// <see cref="Visibility"/>; nothing is added/removed from the menu). Move to Recycle Bin and the zoom cluster both default
    /// to hidden (<see cref="AppSettings.ShowRecycleMenuItem"/> / <see cref="AppSettings.ShowZoomMenuItems"/> = false),
    /// unlike the folder group, which defaults to visible. Complements
    /// <see cref="ContextMenuOpen_FolderGroupVisible_WhenSettingOn"/>, <see cref="ZoomCluster_Visible_WhenSettingOn"/>
    /// and <see cref="RecycleMenuItem_Visible_WhenSettingOn"/>, which only check visibility.
    /// </summary>
    [Fact]
    public async Task ContextMenuStructure_UndoFirst_RecycleSecond_FolderGroupTogglable_SettingsAlwaysLast()
    {
        await WithWindowAsync(null, window =>
        {
            var items = window.ImageContextMenu.Items;
            Assert.Equal(12, items.Count);

            var undo = Assert.IsType<MenuItem>(items[0]);
            Assert.False(string.IsNullOrEmpty(undo.Header as string));
            Assert.Same(window.RecycleMenuItem, items[1]);
            Assert.Same(window.ZoomGroupSeparator, items[2]);
            Assert.Same(window.FitMenuItem, items[3]);
            Assert.Same(window.ZoomToLevelMenuItem, items[4]);
            Assert.Same(window.ZoomMenu, items[5]);
            Assert.Same(window.FolderGroupSeparator, items[6]);
            Assert.Same(window.OpenFolderMenuItem, items[7]);
            Assert.Same(window.NextFolderMenuItem, items[8]);
            Assert.Same(window.PreviousFolderMenuItem, items[9]);
            Assert.IsType<Separator>(items[10]);
            var settings = Assert.IsType<MenuItem>(items[11]);
            Assert.False(string.IsNullOrEmpty(settings.Header as string));

            // Settings is always visible, whichever way Move to Recycle Bin, the zoom cluster and the folder group are toggled --
            // including the all-hidden case where only Undo and Settings remain (no dangling double separator).
            window.Settings.ShowRecycleMenuItem = false;
            window.Settings.ShowZoomMenuItems = false;
            window.Settings.ShowFolderMenuItems = false;
            OpenMenu(window);
            Assert.Equal(Visibility.Visible, undo.Visibility);
            Assert.Equal(Visibility.Collapsed, window.RecycleMenuItem.Visibility);
            Assert.Equal(Visibility.Visible, settings.Visibility);
            window.Settings.ShowRecycleMenuItem = true;
            window.Settings.ShowZoomMenuItems = true;
            window.Settings.ShowFolderMenuItems = true;
            OpenMenu(window);
            Assert.Equal(Visibility.Visible, undo.Visibility);
            Assert.Equal(Visibility.Visible, window.RecycleMenuItem.Visibility);
            Assert.Equal(Visibility.Visible, settings.Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RecycleMenuItem_Visible_WhenSettingOn()
    {
        await WithWindowAsync(null, window =>
        {
            window.Settings.ShowRecycleMenuItem = true;
            OpenMenu(window);

            Assert.Equal(Visibility.Visible, window.RecycleMenuItem.Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RecycleMenuItem_Hidden_WhenSettingOff_ByDefault()
    {
        await WithWindowAsync(null, window =>
        {
            Assert.False(window.Settings.ShowRecycleMenuItem);
            OpenMenu(window);

            Assert.Equal(Visibility.Collapsed, window.RecycleMenuItem.Visibility);

            // Refreshed on every open, not just the first.
            window.Settings.ShowRecycleMenuItem = true;
            OpenMenu(window);
            Assert.Equal(Visibility.Visible, window.RecycleMenuItem.Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ContextMenuOpen_FolderGroupVisible_WhenSettingOn()
    {
        await WithWindowAsync(null, window =>
        {
            window.Settings.ShowFolderMenuItems = true;
            OpenMenu(window);

            Assert.Equal(Visibility.Visible, window.FolderGroupSeparator.Visibility);
            Assert.Equal(Visibility.Visible, window.OpenFolderMenuItem.Visibility);
            Assert.Equal(Visibility.Visible, window.NextFolderMenuItem.Visibility);
            Assert.Equal(Visibility.Visible, window.PreviousFolderMenuItem.Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ContextMenuOpen_FolderGroupHidden_AsOneUnit_WhenSettingOff()
    {
        await WithWindowAsync(null, window =>
        {
            window.Settings.ShowFolderMenuItems = false;
            OpenMenu(window);

            Assert.Equal(Visibility.Collapsed, window.FolderGroupSeparator.Visibility);
            Assert.Equal(Visibility.Collapsed, window.OpenFolderMenuItem.Visibility);
            Assert.Equal(Visibility.Collapsed, window.NextFolderMenuItem.Visibility);
            Assert.Equal(Visibility.Collapsed, window.PreviousFolderMenuItem.Visibility);

            // Refreshed on every open, not just the first.
            window.Settings.ShowFolderMenuItems = true;
            OpenMenu(window);
            Assert.Equal(Visibility.Visible, window.OpenFolderMenuItem.Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ZoomCluster_Visible_WhenSettingOn()
    {
        await WithWindowAsync(null, window =>
        {
            window.Settings.ShowZoomMenuItems = true;
            OpenMenu(window);

            Assert.Equal(Visibility.Visible, window.ZoomGroupSeparator.Visibility);
            Assert.Equal(Visibility.Visible, window.FitMenuItem.Visibility);
            Assert.Equal(Visibility.Visible, window.ZoomToLevelMenuItem.Visibility);
            Assert.Equal(Visibility.Visible, window.ZoomMenu.Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ZoomCluster_Hidden_AsOneUnit_WhenSettingOff()
    {
        await WithWindowAsync(null, window =>
        {
            window.Settings.ShowZoomMenuItems = false;
            OpenMenu(window);

            Assert.Equal(Visibility.Collapsed, window.ZoomGroupSeparator.Visibility);
            Assert.Equal(Visibility.Collapsed, window.FitMenuItem.Visibility);
            Assert.Equal(Visibility.Collapsed, window.ZoomToLevelMenuItem.Visibility);
            Assert.Equal(Visibility.Collapsed, window.ZoomMenu.Visibility);

            // Refreshed on every open, not just the first.
            window.Settings.ShowZoomMenuItems = true;
            OpenMenu(window);
            Assert.Equal(Visibility.Visible, window.FitMenuItem.Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ZoomPreset_WhenAlsoSetIsOn_ZoomsAndPersistsClickZoomPercent()
    {
        using var folder = new TempRoot("context-menu-redesign");
        var png = Path.Combine(folder.Path, "square.png");
        WriteImage(png);
        await WithWindowAsync(folder.Path, async window =>
        {
            window.Settings.SetZoomAlsoSetsClickLevel = true;
            window.Settings.ClickZoomPercent = 100;
            OpenSubmenu(window);

            Click(Preset(window, 200));

            Assert.True(await StaTestHost.WaitForAsync(() => Math.Abs(window.ViewModel.Viewer.Zoom - 2.0) < 0.001 && !window.ViewModel.Viewer.IsFit,
                TimeSpan.FromSeconds(5)), $"Zoom={window.ViewModel.Viewer.Zoom}");
            Assert.Equal(200, window.Settings.ClickZoomPercent);
        });
    }

    [Fact]
    public async Task ZoomPreset_WhenAlsoSetIsOff_ZoomsOnly_LeavesClickZoomPercentUntouched()
    {
        using var folder = new TempRoot("context-menu-redesign");
        var png = Path.Combine(folder.Path, "square.png");
        WriteImage(png);
        await WithWindowAsync(folder.Path, async window =>
        {
            window.Settings.SetZoomAlsoSetsClickLevel = false;
            window.Settings.ClickZoomPercent = 100;
            OpenSubmenu(window);

            Click(Preset(window, 200));

            Assert.True(await StaTestHost.WaitForAsync(() => Math.Abs(window.ViewModel.Viewer.Zoom - 2.0) < 0.001 && !window.ViewModel.Viewer.IsFit,
                TimeSpan.FromSeconds(5)), $"Zoom={window.ViewModel.Viewer.Zoom}");
            Assert.Equal(100, window.Settings.ClickZoomPercent); // one-off zoom: the saved level did not change
        });
    }

    [Fact]
    public async Task AlsoSetToggle_Click_FlipsAndPersistsTheSetting()
    {
        await WithWindowAsync(null, window =>
        {
            window.Settings.SetZoomAlsoSetsClickLevel = true;
            OpenSubmenu(window);
            var toggle = AlsoSetClickLevelItem(window);
            Assert.True(toggle.IsChecked);

            toggle.IsChecked = false; // MenuItem.Click fires after the checkable state has already flipped
            Click(toggle);

            Assert.False(window.Settings.SetZoomAlsoSetsClickLevel);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SetCurrentZoomAsClickLevel_DisabledInFit_EnabledWhenZoomed()
    {
        using var folder = new TempRoot("context-menu-redesign");
        var png = Path.Combine(folder.Path, "square.png");
        WriteImage(png);
        await WithWindowAsync(folder.Path, window =>
        {
            Assert.True(window.ViewModel.Viewer.IsFit);
            OpenSubmenu(window);
            Assert.False(SetCurrentAsClickLevelItem(window).IsEnabled);

            window.ViewModel.Viewer.SetZoom(2.5);
            OpenSubmenu(window);
            Assert.True(SetCurrentAsClickLevelItem(window).IsEnabled);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SetCurrentZoomAsClickLevel_Click_SavesRoundedPercent_WithoutReZooming()
    {
        using var folder = new TempRoot("context-menu-redesign");
        var png = Path.Combine(folder.Path, "square.png");
        WriteImage(png);
        await WithWindowAsync(folder.Path, async window =>
        {
            window.Settings.ClickZoomPercent = 100;
            window.ViewModel.Viewer.SetZoom(2.505);
            await StaTestHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            OpenSubmenu(window);
            var item = SetCurrentAsClickLevelItem(window);
            Assert.True(item.IsEnabled);

            Click(item);

            Assert.Equal(251, window.Settings.ClickZoomPercent); // 2.505 * 100 rounded away-from-zero
            Assert.Equal(2.505, window.ViewModel.Viewer.Zoom, 6); // unchanged: no re-zoom needed
        });
    }

    [Fact]
    [Trait("Category", "UI")]
    public async Task RecycleMenuItem_Click_RecyclesCurrentImage()
    {
        using var folder = new TempRoot("context-menu-delete");
        var png = Path.Combine(folder.Path, "test.png");
        WriteImage(png);
        await WithWindowAsync(folder.Path, async window =>
        {
            Assert.True(await StaTestHost.WaitForAsync(() => window.ViewModel.HasImages && File.Exists(png), TimeSpan.FromSeconds(5)));
            window.Settings.ShowRecycleMenuItem = true;
            OpenMenu(window);
            Click(window.RecycleMenuItem);
            // RecycleAsync should move the file; verify the current image index changed or folder is empty
            Assert.True(await StaTestHost.WaitForAsync(() => !window.ViewModel.HasImages || window.ViewModel.CurrentIndex != 0, TimeSpan.FromSeconds(5)),
                "File should have been moved to Recycle or folder emptied");
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
