using System.Reflection;
using System.Windows;
using PhotoReview.App;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// AR11b (docs/refactoring/arch-review/AR11-settings-persistence.md): every property in
/// <see cref="SettingsWindowRedesignTests.UiControlledProperties"/> is hand-mapped twice in
/// <c>SettingsWindow.xaml.cs</c> -- once in <c>LoadFields</c> (settings -> control) and once in
/// <c>Save_Click</c> (control -> settings). <see cref="SettingsWindowRedesignTests"/> only ever proves the
/// *other* properties survive Save untouched; a line missing from <c>Save_Click</c> for a UI-controlled
/// property (the user's choice silently not saved) had no test catching it. This opens the window with the
/// property already at its default, sets only that property's control to a non-default value (as if the user
/// had changed it), invokes Save without touching anything else, and asserts the saved settings show the new
/// value -- so a missing (or wrong) assignment in <c>Save_Click</c> fails exactly that property's case.
/// </summary>
[Collection("GlobalState")]
public sealed class SettingsWindowRoundTripTests
{
    private static readonly MethodInfo SaveClick =
        typeof(SettingsWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void InvokeSave(SettingsWindow window) => SaveClick.Invoke(window, [window, new RoutedEventArgs()]);

    /// <summary>
    /// Properties this test does not cover: <see cref="AppSettings.ConfigVersion"/> is fixed by the type itself,
    /// <see cref="AppSettings.Actions"/>/<see cref="AppSettings.Shortcuts"/> round-trip through the shortcut text
    /// boxes and the Actions JSON editor (covered by <see cref="SettingsWindowRedesignTests"/>'s alias/duplicate/
    /// JSON tests), and <see cref="AppSettings.UiLanguage"/> needs a <c>LocalizationService</c> the plain
    /// <see cref="AppSettings"/> constructor of <see cref="SettingsWindow"/> does not have.
    /// </summary>
    private static readonly HashSet<string> ExcludedFromThisTest = new(StringComparer.Ordinal)
    {
        nameof(AppSettings.ConfigVersion), nameof(AppSettings.Actions), nameof(AppSettings.Shortcuts), nameof(AppSettings.UiLanguage),
    };

    /// <summary>
    /// One control-mutation per covered property: sets that property's control (only) to a value distinct from
    /// <see cref="AppSettings"/>'s default. A new UI-controlled property that is missing here fails the tripwire
    /// test below instead of silently going untested (the same shape as <c>TestFilterDriftTests</c>).
    /// </summary>
    private static readonly Dictionary<string, Action<SettingsWindow>> ControlMutations = new(StringComparer.Ordinal)
    {
        [nameof(AppSettings.InitialViewMode)] = w => w.ViewModeCombo.SelectedIndex = 1, // Fit -> Percent100
        [nameof(AppSettings.LoadingMode)] = w => w.LoadingModeCombo.SelectedIndex = 2, // Preview -> Original
        [nameof(AppSettings.ImageSortMode)] = w => w.SortModeCombo.SelectedIndex = 1, // Name -> SizeAscending
        [nameof(AppSettings.CompareHashEnabled)] = w => w.CompareHashCheck.IsChecked = false, // true -> false
        [nameof(AppSettings.CompareSizeEnabled)] = w => w.CompareSizeCheck.IsChecked = false, // true -> false
        [nameof(AppSettings.ScalingQuality)] = w => w.ScalingQualityCombo.SelectedIndex = 1, // HighQuality -> Linear
        [nameof(AppSettings.DecoderBackend)] = w => w.DecoderBackendCombo.SelectedIndex = 0, // WicDirect -> Wpf
        [nameof(AppSettings.ImageCacheRamPercent)] = w => w.RamCacheSlider.Value = w.RamCacheSlider.Maximum, // 50 -> device max (90)
        [nameof(AppSettings.LoggingEnabled)] = w => w.LoggingCheck.IsChecked = true, // false -> true
        [nameof(AppSettings.JournalDurability)] = w => w.JournalSafeRadio.IsChecked = true, // Fast -> PowerLossSafe
        [nameof(AppSettings.AllowPermanentDeleteWithoutRecycleBin)] = w => w.AllowPermanentDeleteCheck.IsChecked = true, // false -> true
        [nameof(AppSettings.InstanceMode)] = w => w.InstanceModeCombo.SelectedIndex = 1, // SingleWindow -> PerFolder
        [nameof(AppSettings.ShowInfoOverlay)] = w => w.ShowInfoOverlayCheck.IsChecked = false, // true -> false
        [nameof(AppSettings.ShowFileInfo)] = w => w.ShowFileInfoCheck.IsChecked = false, // true -> false
        [nameof(AppSettings.ShowFolderInfo)] = w => w.ShowFolderInfoCheck.IsChecked = true, // false -> true
        [nameof(AppSettings.MouseWheelAction)] = w => w.MouseWheelActionCombo.SelectedIndex = 1, // Zoom -> Navigate
        [nameof(AppSettings.ClickToZoomEnabled)] = w => w.ClickToZoomCheck.IsChecked = true, // false -> true
        [nameof(AppSettings.ClickZoomPercent)] = w => w.ClickZoomPercentBox.Text = "222", // 100 -> 222
        [nameof(AppSettings.KineticPanEnabled)] = w => w.KineticPanCheck.IsChecked = false, // true -> false
        [nameof(AppSettings.MoveCopyReuseLastFolder)] = w => w.MoveCopyReuseLastFolderCheck.IsChecked = true, // false -> true
        [nameof(AppSettings.ShowExifInfo)] = w => w.ShowExifInfoCheck.IsChecked = true, // false -> true
        // Default is All minus (FileName|Dimensions); flip every field so the result is the complement (FileName|Dimensions).
        [nameof(AppSettings.ExifInfoFields)] = w =>
        {
            w.ExifFieldFileNameCheck.IsChecked = true; w.ExifFieldDateTakenCheck.IsChecked = false;
            w.ExifFieldDimensionsCheck.IsChecked = true; w.ExifFieldCameraCheck.IsChecked = false;
            w.ExifFieldLensCheck.IsChecked = false; w.ExifFieldIsoCheck.IsChecked = false;
            w.ExifFieldFocalLengthCheck.IsChecked = false; w.ExifFieldApertureCheck.IsChecked = false;
            w.ExifFieldShutterSpeedCheck.IsChecked = false;
        },
    };

    /// <summary>What <see cref="ControlMutations"/> above is expected to produce on <see cref="AppSettings"/>.</summary>
    private static readonly Dictionary<string, object> ExpectedValues = new(StringComparer.Ordinal)
    {
        [nameof(AppSettings.InitialViewMode)] = InitialViewMode.Percent100,
        [nameof(AppSettings.LoadingMode)] = LoadingMode.Original,
        [nameof(AppSettings.ImageSortMode)] = ImageSortMode.SizeAscending,
        [nameof(AppSettings.CompareHashEnabled)] = false,
        [nameof(AppSettings.CompareSizeEnabled)] = false,
        [nameof(AppSettings.ScalingQuality)] = ScalingQuality.Linear,
        [nameof(AppSettings.DecoderBackend)] = DecoderBackend.Wpf,
        [nameof(AppSettings.ImageCacheRamPercent)] = 90,
        [nameof(AppSettings.LoggingEnabled)] = true,
        [nameof(AppSettings.JournalDurability)] = JournalDurability.PowerLossSafe,
        [nameof(AppSettings.AllowPermanentDeleteWithoutRecycleBin)] = true,
        [nameof(AppSettings.InstanceMode)] = InstanceMode.PerFolder,
        [nameof(AppSettings.ShowInfoOverlay)] = false,
        [nameof(AppSettings.ShowFileInfo)] = false,
        [nameof(AppSettings.ShowFolderInfo)] = true,
        [nameof(AppSettings.MouseWheelAction)] = MouseWheelAction.Navigate,
        [nameof(AppSettings.ClickToZoomEnabled)] = true,
        [nameof(AppSettings.ClickZoomPercent)] = 222,
        [nameof(AppSettings.KineticPanEnabled)] = false,
        [nameof(AppSettings.MoveCopyReuseLastFolder)] = true,
        [nameof(AppSettings.ShowExifInfo)] = true,
        [nameof(AppSettings.ExifInfoFields)] = ExifInfoFields.All & ~(ExifInfoFields.DateTaken | ExifInfoFields.Camera | ExifInfoFields.Lens |
            ExifInfoFields.Iso | ExifInfoFields.FocalLength | ExifInfoFields.Aperture | ExifInfoFields.ShutterSpeed),
    };

    [Fact(DisplayName = "Tripwire: every UI-controlled property has a round-trip mutation and expected value above")]
    public void EveryUiControlledProperty_HasAMutationAndExpectedValue()
    {
        var covered = SettingsWindowRedesignTests.UiControlledProperties.Except(ExcludedFromThisTest).ToList();
        Assert.NotEmpty(covered);
        var missingMutation = covered.Where(p => !ControlMutations.ContainsKey(p)).ToList();
        var missingExpected = covered.Where(p => !ExpectedValues.ContainsKey(p)).ToList();
        var stale = ControlMutations.Keys.Except(covered).ToList();
        Assert.True(missingMutation.Count == 0,
            "Add a ControlMutations entry for: " + string.Join(", ", missingMutation) + " (or add it to ExcludedFromThisTest with a reason)");
        Assert.True(missingExpected.Count == 0, "Add an ExpectedValues entry for: " + string.Join(", ", missingExpected));
        Assert.True(stale.Count == 0, "Remove the now-stale ControlMutations/ExpectedValues entries for: " + string.Join(", ", stale));
    }

    [Fact(DisplayName = "Every UI-controlled property round-trips: default -> control shows a new value -> Save persists exactly that value")]
    public async Task UiControlledProperties_RoundTripThroughSave()
    {
        var properties = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => ControlMutations.ContainsKey(p.Name))
            .ToList();
        Assert.NotEmpty(properties);

        var failures = new List<string>();
        await StaTestHost.RunAsync(() =>
        {
            foreach (var property in properties)
            {
                var window = new SettingsWindow(new AppSettings())
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowInTaskbar = false,
                };
                try
                {
                    window.Loaded += (_, _) =>
                    {
                        ControlMutations[property.Name](window);
                        InvokeSave(window);
                    };
                    if (window.ShowDialog() != true)
                    {
                        failures.Add($"{property.Name}: Save was rejected");
                        continue;
                    }
                    var actual = property.GetValue(window.Settings);
                    var expected = ExpectedValues[property.Name];
                    if (!Equals(actual, expected))
                        failures.Add($"{property.Name}: expected {expected}, got {actual}");
                }
                finally { if (window.IsLoaded) window.Close(); }
            }
            return Task.CompletedTask;
        });
        Assert.True(failures.Count == 0, "Settings.Save dropped or mis-saved these UI-controlled properties:\n" + string.Join('\n', failures));
    }
}
