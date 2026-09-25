using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using PhotoReview.App;
using PhotoReview.Core.Model; // InstanceMode, MouseWheelAction, ExifInfoFields
using PhotoReview.Core.Settings;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Settings redesign (feat/settings-redesign): the structural bug fix (Save no longer silently resets a property
/// the window has no control for), the new left-nav paging, and the new controls this redesign adds.
/// </summary>
[Collection("GlobalState")]
public sealed class SettingsWindowRedesignTests
{
    private static readonly MethodInfo SaveClick =
        typeof(SettingsWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void InvokeSave(SettingsWindow window) => SaveClick.Invoke(window, [window, new RoutedEventArgs()]);

    /// <summary>
    /// Properties the Settings UI has a control for (and therefore Save legitimately overwrites with whatever the
    /// control shows). Everything else on <see cref="AppSettings"/> must survive an open/Save untouched -- this is
    /// exactly the structural guarantee <see cref="AppSettings.Clone"/> replaces the old hand-copy with, and it is
    /// reflection-driven so a future property is covered automatically without editing this test.
    /// </summary>
    private static readonly HashSet<string> UiControlledProperties = new(StringComparer.Ordinal)
    {
        nameof(AppSettings.ConfigVersion), // fixed to AppSettings.CurrentConfigVersion by the type itself
        nameof(AppSettings.InitialViewMode), nameof(AppSettings.LoadingMode), nameof(AppSettings.ImageSortMode),
        nameof(AppSettings.CompareHashEnabled), nameof(AppSettings.CompareSizeEnabled), nameof(AppSettings.ScalingQuality),
        nameof(AppSettings.DecoderBackend), nameof(AppSettings.ImageCacheRamPercent), nameof(AppSettings.LoggingEnabled),
        nameof(AppSettings.JournalDurability), nameof(AppSettings.AllowPermanentDeleteWithoutRecycleBin),
        nameof(AppSettings.Actions), nameof(AppSettings.Shortcuts), nameof(AppSettings.UiLanguage),
        nameof(AppSettings.InstanceMode), nameof(AppSettings.ShowInfoOverlay), nameof(AppSettings.ShowFileInfo),
        nameof(AppSettings.ShowFolderInfo), nameof(AppSettings.MouseWheelAction), nameof(AppSettings.ClickToZoomEnabled),
        nameof(AppSettings.ClickZoomPercent), nameof(AppSettings.KineticPanEnabled), nameof(AppSettings.MoveCopyReuseLastFolder),
        nameof(AppSettings.ShowExifInfo), nameof(AppSettings.ExifInfoFields),
    };

    [Fact(DisplayName = "Every AppSettings property without a Settings control survives open + Save (the SAVE-01 bug this branch fixes)")]
    public async Task PropertiesNotShownInUi_SurviveOpenAndSave()
    {
        var properties = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && !UiControlledProperties.Contains(p.Name))
            .ToList();
        Assert.NotEmpty(properties); // the walker itself must find candidates, or this test would vacuously pass

        var failures = new List<string>();
        await StaTestHost.RunAsync(() =>
        {
            var defaults = new AppSettings();
            foreach (var property in properties)
            {
                var mutated = Mutate(property.GetValue(defaults), property.Name);
                if (mutated is null) { failures.Add($"{property.Name}: unsupported type {property.PropertyType} (extend Mutate)"); continue; }

                var source = new AppSettings();
                property.SetValue(source, mutated);

                var window = new SettingsWindow(source) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowInTaskbar = false };
                try
                {
                    window.Loaded += (_, _) => InvokeSave(window);
                    Assert.True(window.ShowDialog(), $"{property.Name}: Save was rejected");
                    var actual = property.GetValue(window.Settings);
                    if (!Equals(actual, mutated))
                        failures.Add($"{property.Name}: expected {mutated}, got {actual}");
                }
                finally { if (window.IsLoaded) window.Close(); }
            }
            return Task.CompletedTask;
        });
        Assert.True(failures.Count == 0, "Settings.Save dropped these properties:\n" + string.Join('\n', failures));
    }

    private static object? Mutate(object? current, string propertyName) => current switch
    {
        bool b => !b,
        int i => i + 12345,
        long l => l + 987654321L,
        double d => d + 0.125,
        string s => "probe-" + propertyName + "-" + s,
        Enum e => MutateEnum(e),
        null => "probe-" + propertyName, // nullable string, currently null
        _ => null,
    };

    /// <summary>Picks a defined value distinct from <paramref name="current"/> (a flags combination for [Flags] enums).</summary>
    private static object MutateEnum(Enum current)
    {
        var type = current.GetType();
        if (type.IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            var all = Enum.GetValues(type).Cast<Enum>().Aggregate(0L, (acc, v) => acc | Convert.ToInt64(v));
            var distinct = (Convert.ToInt64(current) ^ all) & all; // flip every defined bit -> always != current when all != 0
            return Enum.ToObject(type, distinct);
        }
        var values = Enum.GetValues(type).Cast<Enum>().ToList();
        return values.First(v => !v.Equals(current));
    }

    [Fact(DisplayName = "Clearing an optional shortcut is accepted; clearing a mandatory one is rejected")]
    public async Task OptionalShortcutClears_MandatoryShortcutCannotBeEmptied()
    {
        await StaTestHost.RunAsync(() =>
        {
            // Optional: LastImage (ShortcutMappings.OptionalNames) clears via its "✕" button and Save accepts it.
            var window = new SettingsWindow(new AppSettings()) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowInTaskbar = false };
            try
            {
                window.Loaded += (_, _) =>
                {
                    Assert.False(string.IsNullOrEmpty(window.LastImageText.Text)); // has a default to begin with
                    typeof(SettingsWindow).GetMethod("ClearLastImage_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, [window, new RoutedEventArgs()]);
                    Assert.Equal(string.Empty, window.LastImageText.Text);
                    InvokeSave(window);
                };
                Assert.True(window.ShowDialog());
                Assert.Equal(string.Empty, window.Settings.Shortcuts.LastImage);
            }
            finally { if (window.IsLoaded) window.Close(); }

            // Mandatory: emptying Next must be rejected. Not shown as a dialog (Save's early-return path never touches
            // DialogResult), so no ShowDialog/modal loop is needed; InvalidSettingsWarning replaces the MessageBox seam.
            var warnings = new List<string>();
            var window2 = new SettingsWindow(new AppSettings()) { InvalidSettingsWarning = warnings.Add };
            try
            {
                window2.NextText.Text = string.Empty;
                InvokeSave(window2);
                Assert.Single(warnings);
                Assert.Null(window2.DialogResult);
                Assert.Equal("Right", window2.Settings.Shortcuts.Next); // untouched: Save returned before writing it back
            }
            finally { window2.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "Two shortcuts sharing the same key show a live duplicate warning that clears once they differ again")]
    public async Task DuplicateShortcuts_ShowLiveWarning()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings());
            try
            {
                Assert.Equal(Visibility.Collapsed, window.ShortcutDuplicateWarningText.Visibility);

                window.PreviousText.Text = window.NextText.Text; // now a duplicate of Next
                Assert.Equal(Visibility.Visible, window.ShortcutDuplicateWarningText.Visibility);
                Assert.False(string.IsNullOrWhiteSpace(window.ShortcutDuplicateWarningText.Text));

                window.PreviousText.Text = "Left"; // back to the default, distinct again
                Assert.Equal(Visibility.Collapsed, window.ShortcutDuplicateWarningText.Visibility);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "New Mouse & zoom / Display / General controls round-trip through Save into AppSettings")]
    public async Task NewControls_RoundTripThroughSave()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings()) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowInTaskbar = false };
            try
            {
                window.Loaded += (_, _) =>
                {
                    window.InstanceModeCombo.SelectedIndex = 1; // PerFolder
                    window.ShowInfoOverlayCheck.IsChecked = false;
                    window.ShowFileInfoCheck.IsChecked = false;
                    window.ShowFolderInfoCheck.IsChecked = false;
                    window.MouseWheelActionCombo.SelectedIndex = 1; // Navigate
                    window.ClickToZoomCheck.IsChecked = false;
                    window.ClickZoomPercentBox.Text = "222";
                    window.KineticPanCheck.IsChecked = false;
                    window.MoveCopyReuseLastFolderCheck.IsChecked = true;
                    window.LastImageText.Text = "End";
                    window.ZoomActualSizeText.Text = "D2";
                    window.ToggleInfoOverlayText.Text = "J";
                    window.MoveToFolderText.Text = "K";
                    window.CopyToFolderText.Text = "L";
                    window.ShowExifInfoCheck.IsChecked = true;
                    window.ExifFieldCameraCheck.IsChecked = true;
                    window.ExifFieldLensCheck.IsChecked = false;
                    window.ExifFieldFileNameCheck.IsChecked = false;
                    window.ExifFieldDateTakenCheck.IsChecked = false;
                    window.ExifFieldDimensionsCheck.IsChecked = false;
                    window.ExifFieldIsoCheck.IsChecked = false;
                    window.ExifFieldFocalLengthCheck.IsChecked = false;
                    window.ExifFieldApertureCheck.IsChecked = false;
                    window.ExifFieldShutterSpeedCheck.IsChecked = false;
                    InvokeSave(window);
                };
                Assert.True(window.ShowDialog());

                Assert.Equal(InstanceMode.PerFolder, window.Settings.InstanceMode);
                Assert.False(window.Settings.ShowInfoOverlay);
                Assert.False(window.Settings.ShowFileInfo);
                Assert.False(window.Settings.ShowFolderInfo);
                Assert.Equal(MouseWheelAction.Navigate, window.Settings.MouseWheelAction);
                Assert.False(window.Settings.ClickToZoomEnabled);
                Assert.Equal(222, window.Settings.ClickZoomPercent);
                Assert.False(window.Settings.KineticPanEnabled);
                Assert.True(window.Settings.MoveCopyReuseLastFolder);
                Assert.Equal("End", window.Settings.Shortcuts.LastImage);
                Assert.Equal("D2", window.Settings.Shortcuts.ZoomActualSize);
                Assert.Equal("J", window.Settings.Shortcuts.ToggleInfoOverlay);
                Assert.Equal("K", window.Settings.Shortcuts.MoveToFolder);
                Assert.Equal("L", window.Settings.Shortcuts.CopyToFolder);
                Assert.True(window.Settings.ShowExifInfo);
                Assert.Equal(ExifInfoFields.Camera, window.Settings.ExifInfoFields);
            }
            finally { if (window.IsLoaded) window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "Out-of-range click-zoom percent is rejected by Save")]
    public async Task ClickZoomPercent_OutOfRange_IsRejected()
    {
        await StaTestHost.RunAsync(() =>
        {
            var warnings = new List<string>();
            var window = new SettingsWindow(new AppSettings()) { InvalidSettingsWarning = warnings.Add };
            try
            {
                window.ClickZoomPercentBox.Text = "5000"; // > AppSettings.MaxClickZoomPercent
                InvokeSave(window);
                Assert.Single(warnings);
                Assert.Null(window.DialogResult);
                Assert.Equal(AppSettings.DefaultClickZoomPercent, window.Settings.ClickZoomPercent); // untouched
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "EXIF per-field checkboxes are disabled while the ShowExifInfo master switch is off")]
    public async Task ExifFields_DisabledWhileShowExifInfoIsOff()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings { ShowExifInfo = true });
            try
            {
                Assert.True(window.ExifFieldCameraCheck.IsEnabled);
                window.ShowExifInfoCheck.IsChecked = false;
                Assert.False(window.ExifFieldCameraCheck.IsEnabled);
                Assert.False(window.ExifFieldFileNameCheck.IsEnabled);
                window.ShowExifInfoCheck.IsChecked = true;
                Assert.True(window.ExifFieldCameraCheck.IsEnabled);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "Left navigation selects the matching page and remembers the last page for the session")]
    public async Task Navigation_SelectsMatchingPage_AndRemembersLastPageForTheSession()
    {
        var lastPageField = typeof(SettingsWindow).GetField("s_lastPageKey", BindingFlags.Static | BindingFlags.NonPublic)!;
        var original = lastPageField.GetValue(null);
        try
        {
            await StaTestHost.RunAsync(() =>
            {
                var window = new SettingsWindow(new AppSettings());
                try
                {
                    var cases = new (ListBoxItem Item, ScrollViewer Page)[]
                    {
                        (window.NavGeneral, window.GeneralScrollViewer), (window.NavDisplay, window.DisplayScrollViewer),
                        (window.NavMouse, window.MouseScrollViewer), (window.NavPerformance, window.PerformanceScrollViewer),
                        (window.NavShortcuts, window.ShortcutsScrollViewer), (window.NavFiles, window.FilesScrollViewer),
                        (window.NavDiagnostics, window.DiagnosticsScrollViewer),
                    };
                    foreach (var (item, page) in cases)
                    {
                        window.NavList.SelectedItem = item;
                        Assert.Equal(Visibility.Visible, page.Visibility);
                        foreach (var (otherItem, otherPage) in cases)
                        {
                            if (!ReferenceEquals(otherItem, item)) Assert.Equal(Visibility.Collapsed, otherPage.Visibility);
                        }
                        Assert.False(string.IsNullOrWhiteSpace(window.PageTitleText.Text));
                    }

                    window.NavList.SelectedItem = window.NavFiles;
                }
                finally { window.Close(); }

                // A new window instance opens on the page the previous one left on (session memory, not persisted).
                var reopened = new SettingsWindow(new AppSettings());
                try { Assert.Equal(Visibility.Visible, reopened.FilesScrollViewer.Visibility); }
                finally { reopened.Close(); }
                return Task.CompletedTask;
            });
        }
        finally { lastPageField.SetValue(null, original); }
    }
}
