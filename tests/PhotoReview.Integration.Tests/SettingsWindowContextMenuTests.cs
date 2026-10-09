using System.IO;
using System.Reflection;
using System.Windows;
using PhotoReview.App;
using PhotoReview.Core;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Settings > right-click menu list: one tick box per item (<see cref="ContextMenuItems"/>), Settings locked on, saved into
/// <see cref="AppSettings.HiddenContextMenuItems"/>, reset by Restore defaults and carried by Export / Import.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class SettingsWindowContextMenuTests
{
    private static readonly MethodInfo SaveClick = typeof(SettingsWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo DefaultsClick = typeof(SettingsWindow).GetMethod("Defaults_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact(DisplayName = "The list has a tick box per menu item, ticked unless hidden; Settings is locked on with a tooltip")]
    public async Task Checks_ReflectTheHiddenList_AndSettingsIsLocked()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings { HiddenContextMenuItems = ["Undo", "ZoomFitWidth2"] });
            try
            {
                Assert.Equal(ContextMenuItems.Items.Count, window.ContextMenuChecks.Count);
                Assert.False(window.ContextMenuChecks[ContextMenuItemId.Undo].IsChecked);
                Assert.False(window.ContextMenuChecks[ContextMenuItemId.ZoomFitWidth2].IsChecked);
                Assert.True(window.ContextMenuChecks[ContextMenuItemId.ZoomFitWidth].IsChecked);
                Assert.True(window.ContextMenuChecks[ContextMenuItemId.MoveToRecycleBin].IsChecked);

                var locked = window.ContextMenuChecks[ContextMenuItemId.Settings];
                Assert.True(locked.IsChecked);
                Assert.False(locked.IsEnabled);
                Assert.False(string.IsNullOrWhiteSpace(locked.ToolTip as string));
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "An old config (legacy flags only) shows the migrated state in the tick boxes")]
    public async Task Checks_FromLegacyFlags()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings { ShowFolderMenuItems = false, ShowZoomMenuItems = true, ShowRecycleMenuItem = true });
            try
            {
                Assert.False(window.ContextMenuChecks[ContextMenuItemId.NextFolder].IsChecked);
                Assert.True(window.ContextMenuChecks[ContextMenuItemId.Fit].IsChecked);
                Assert.True(window.ContextMenuChecks[ContextMenuItemId.MoveToRecycleBin].IsChecked);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "Save stores the unticked items in the settings file and keeps Settings out of the list")]
    public async Task Save_WritesTheUntickedItems()
    {
        using var root = new TempRoot("ctx-menu-save");
        var appPaths = new AppPaths(root.Path);
        var store = new SettingsStore(appPaths, new PhysicalFileSystem(), NullLog.Instance);
        store.Load();

        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(store)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            window.Loaded += (_, _) =>
            {
                window.ContextMenuChecks[ContextMenuItemId.ExternalEditor].IsChecked = false;
                window.ContextMenuChecks[ContextMenuItemId.ZoomFitWidth2].IsChecked = false;
                window.ContextMenuChecks[ContextMenuItemId.Fit].IsChecked = true;
                SaveClick.Invoke(window, [window, new RoutedEventArgs()]);
            };
            Assert.True(window.ShowDialog());
            return Task.CompletedTask;
        });

        var expected = new[] { "MoveToRecycleBin", "ZoomToLevel", "ZoomSubmenu", "ExternalEditor", "ZoomFitWidth2" };
        Assert.Equal(expected, store.Current.HiddenContextMenuItems);
        Assert.Equal(expected, new SettingsStore(appPaths, new PhysicalFileSystem(), NullLog.Instance).Load().HiddenContextMenuItems);
    }

    [Fact(DisplayName = "Restore defaults brings back the default hidden set (zoom cluster and Recycle Bin)")]
    public async Task RestoreDefaults_ResetsTheList()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings { HiddenContextMenuItems = ["OpenFolder"] });
            try
            {
                DefaultsClick.Invoke(window, [null, new RoutedEventArgs()]);

                Assert.True(ContextMenuItems.DefaultHidden.SetEquals(ContextMenuItems.EffectiveHidden(window.Settings)));
                Assert.True(window.ContextMenuChecks[ContextMenuItemId.OpenFolder].IsChecked);
                Assert.False(window.ContextMenuChecks[ContextMenuItemId.Fit].IsChecked);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "Export then Import carries the hidden list into the tick boxes of the other window")]
    public async Task ExportImport_RoundTripsTheList()
    {
        var dir = Directory.CreateTempSubdirectory("pr-ctx-menu-rt-").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            await StaTestHost.RunAsync(() =>
            {
                var source = new SettingsWindow(new AppSettings { UiLanguage = "en", HiddenContextMenuItems = ["Undo", "CopyFullPath", "ZoomFitHeight"] });
                var target = new SettingsWindow(new AppSettings { UiLanguage = "en", HiddenContextMenuItems = [] });
                try
                {
                    source.ExportSettingsTo(path);
                    target.ImportSettingsFrom(path);

                    Assert.Equal(["Undo", "CopyFullPath", "ZoomFitHeight"], target.Settings.HiddenContextMenuItems);
                    Assert.False(target.ContextMenuChecks[ContextMenuItemId.Undo].IsChecked);
                    Assert.False(target.ContextMenuChecks[ContextMenuItemId.ZoomFitHeight].IsChecked);
                    Assert.True(target.ContextMenuChecks[ContextMenuItemId.NextFolder].IsChecked);
                }
                finally { source.Close(); target.Close(); }
                return Task.CompletedTask;
            });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact(DisplayName = "Import of an old-format file (legacy flags only) migrates them into the list")]
    public async Task Import_OldFormat_MigratesLegacyFlags()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings());
            try
            {
                var ok = window.TryApplyImportedJson("""{"ConfigVersion":3,"UiLanguage":"en","ShowFolderMenuItems":false,"ShowZoomMenuItems":true,"ShowRecycleMenuItem":true}""", out var message);

                Assert.True(ok);
                Assert.Null(message);
                Assert.Equal(["OpenFolder", "NextFolder", "PreviousFolder"], window.Settings.HiddenContextMenuItems);
                Assert.False(window.ContextMenuChecks[ContextMenuItemId.OpenFolder].IsChecked);
                Assert.True(window.ContextMenuChecks[ContextMenuItemId.Fit].IsChecked);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }
}
