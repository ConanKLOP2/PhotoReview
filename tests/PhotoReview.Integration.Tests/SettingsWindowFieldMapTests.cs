using System.Reflection;
using System.Windows;
using PhotoReview.App;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Guard for the "hand-mapped field list" shape in <c>SettingsWindow.xaml.cs</c>: <c>LoadFields</c> (settings ->
/// UI), <c>Save_Click</c> (UI -> settings) and <c>BuildProbeSettings</c> each hand-list the same ~70
/// <see cref="AppSettings"/> properties, and nothing enforces they stay in sync -- exactly the shape that
/// previously caused a real persistence bug (see <see cref="AppSettings.Clone"/>'s own comment). Existing tests
/// (<c>SettingsWindowRedesignTests</c>, <c>SettingsWindowRoundTripTests</c>) prove Save_Click doesn't drop a
/// property, but neither ever constructs a window from a *non-default* <see cref="AppSettings"/> and checks that
/// <c>LoadFields</c> actually wrote the value into the control -- a property missing from <c>LoadFields</c> would
/// not be caught by either.
///
/// This test does exactly that, generically, by reflection: for every public settable property not in
/// <see cref="ExcludedFromSettingsWindow"/>, it builds a *source* <see cref="AppSettings"/> with only that
/// property set to a valid non-default value, constructs the window from it (running the real <c>LoadFields</c>),
/// then resets just that one property back to its type default directly on <c>window.Settings</c> -- so the only
/// way the property can end up mutated again is if <c>LoadFields</c> really did write it into a control AND
/// <c>Save_Click</c> really did read that control back. Removing either the <c>LoadFields</c> line or the
/// <c>Save_Click</c> line for any covered property fails this test.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class SettingsWindowFieldMapTests
{
    private static readonly MethodInfo SaveClick =
        typeof(SettingsWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void InvokeSave(SettingsWindow window) => SaveClick.Invoke(window, [window, new RoutedEventArgs()]);

    /// <summary>
    /// Properties the Settings window intentionally does not field-map, with the reason for each -- keep this
    /// list explicit rather than inferring exclusions, so a new property defaults to being COVERED (and a
    /// maintainer who genuinely doesn't want it covered has to say why here).
    /// </summary>
    private static readonly HashSet<string> ExcludedFromSettingsWindow = new(StringComparer.Ordinal)
    {
        // Fixed to AppSettings.CurrentConfigVersion by the type itself; never shown or editable.
        nameof(AppSettings.ConfigVersion),
        // Nested objects, not a single scalar value: Shortcuts is 24 individual shortcut text boxes and Actions
        // is a JSON editor; both have their own dedicated coverage (SettingsWindowRedesignTests /
        // SettingsWindowUserFeedbackBatchTests), and a generic single-property mutation doesn't apply to them.
        nameof(AppSettings.Shortcuts), nameof(AppSettings.Actions),
        // Needs a LocalizationService, which the plain-AppSettings SettingsWindow constructor used by this test
        // does not provide (LanguageGroup collapses without one -- see LoadLanguages). Covered instead by
        // SettingsWindowLanguageRefreshTests.
        nameof(AppSettings.UiLanguage),
        // Performance internals with NO Settings-window control at all (Defaults_Click resets them to
        // PerformanceOptions.* alongside the real fields, but LoadFields/Save_Click never read or write them --
        // they are hand-edit-config.json-only knobs).
        nameof(AppSettings.ImageCacheCapacityBytes), nameof(AppSettings.MemoryReserveBytes),
        nameof(AppSettings.PreloadWorkerCount), nameof(AppSettings.PreloadMemoryLoadLimit),
        nameof(AppSettings.PreviewDiskCacheCapacityBytes), nameof(AppSettings.UseSourceBytesCache),
        nameof(AppSettings.SourceBytesCapacityBytes),
        // Internal "last used folder" state written by Move-to/Copy-to, not a user-facing setting.
        nameof(AppSettings.LastMoveToFolder), nameof(AppSettings.LastCopyToFolder),
        // Defaults_Click resets it, but LoadFields/Save_Click have no control for it -- a pre-existing gap this
        // refactor does not introduce or fix; flagged here rather than silently mutated by the generic walker.
        nameof(AppSettings.SetZoomAlsoSetsClickLevel),
    };

    /// <summary>
    /// A value distinct from the type default that <c>Save_Click</c>'s own validation accepts. Most properties
    /// don't need an entry here (the generic <see cref="Mutate"/> below is safe for them); a property gets one
    /// only when a generic +1/flip would land outside its accepted range or outside what its combo can represent.
    /// </summary>
    private static object MutateValid(string propertyName, object? current) => propertyName switch
    {
        // The combo collapses Percent400 into Percent200 (PR-B); Percent100 is a safe, uncollapsed choice.
        nameof(AppSettings.InitialViewMode) => InitialViewMode.Percent100,
        nameof(AppSettings.ImageSortMode) => ImageSortMode.SizeAscending,
        nameof(AppSettings.ClickZoomPercent) => AppSettings.MaxClickZoomPercent,
        nameof(AppSettings.ArrowPanStepPercent) => AppSettings.MaxArrowPanStepPercent,
        nameof(AppSettings.KeyboardZoomStepPercent) => AppSettings.MaxKeyboardZoomStepPercent,
        nameof(AppSettings.ImageTransitionMs) => AppSettings.MaxImageTransitionMs,
        nameof(AppSettings.PreloadForwardCount) => PerformanceOptions.MaxPreloadCount,
        nameof(AppSettings.PreloadBackwardCount) => PerformanceOptions.MaxPreloadCount,
        nameof(AppSettings.ToolbarAutoHideDelayMs) => AppSettings.MaxToolbarAutoHideDelayMs,
        nameof(AppSettings.InfoOverlayAutoHideDelayMs) => AppSettings.MaxInfoOverlayAutoHideDelayMs,
        nameof(AppSettings.InfoOverlayFontSize) => AppSettings.MaxInfoOverlayFontSize,
        nameof(AppSettings.ImageCacheRamPercent) => PerformanceOptions.MaxImageCacheRamPercent,
        nameof(AppSettings.ToolbarOpacityPercent) => AppSettings.MinToolbarOpacityPercent,
        _ => Mutate(current, propertyName)!,
    };

    /// <summary>Generic non-default mutation for a property with no range restriction (booleans, enums, plain strings).</summary>
    private static object? Mutate(object? current, string propertyName) => current switch
    {
        bool b => !b,
        int i => i + 1,
        long l => l + 1,
        double d => d + 1,
        string s => "probe-" + propertyName + "-" + s,
        Enum e => MutateEnum(e),
        null => "probe-" + propertyName,
        _ => null,
    };

    /// <summary>Picks a defined value distinct from <paramref name="current"/> (every bit flipped for a [Flags] enum).</summary>
    private static object MutateEnum(Enum current)
    {
        var type = current.GetType();
        if (type.IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            var all = Enum.GetValues(type).Cast<Enum>().Aggregate(0L, (acc, v) => acc | Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture));
            var distinct = (Convert.ToInt64(current, System.Globalization.CultureInfo.InvariantCulture) ^ all) & all;
            return Enum.ToObject(type, distinct);
        }
        var values = Enum.GetValues(type).Cast<Enum>().ToList();
        return values.First(v => !v.Equals(current));
    }

    [Fact(DisplayName = "Tripwire: every allow-list entry is a real AppSettings property (catches a stale exclusion after a rename/removal)")]
    public void ExcludedFromSettingsWindow_ListsOnlyRealProperties()
    {
        var all = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var stale = ExcludedFromSettingsWindow.Except(all).ToList();
        Assert.True(stale.Count == 0, "Remove these stale allow-list entries (property no longer exists): " + string.Join(", ", stale));
    }

    [Fact(DisplayName = "Every field-mapped AppSettings property round-trips through LoadFields then Save_Click")]
    public async Task FieldMappedProperties_RoundTripThroughLoadFieldsAndSave()
    {
        var properties = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && !ExcludedFromSettingsWindow.Contains(p.Name))
            .ToList();
        // A new AppSettings property is covered automatically unless someone explicitly excludes it above; if the
        // walker itself finds nothing, this test would vacuously pass, so guard against that too.
        Assert.NotEmpty(properties);

        var failures = new List<string>();
        await StaTestHost.RunAsync(() =>
        {
            foreach (var property in properties)
            {
                var defaultValue = property.GetValue(new AppSettings());
                var mutated = MutateValid(property.Name, defaultValue);
                if (mutated is null || Equals(mutated, defaultValue))
                {
                    failures.Add($"{property.Name}: could not produce a valid non-default value (extend MutateValid)");
                    continue;
                }

                var source = new AppSettings();
                property.SetValue(source, mutated);

                var window = new SettingsWindow(source)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowInTaskbar = false,
                };
                try
                {
                    window.Loaded += (_, _) =>
                    {
                        // The constructor already ran LoadFields with `source` (== `mutated` for this property),
                        // so if LoadFields is wired correctly the control now shows `mutated`. window.Settings
                        // itself, however, is AppSettings.Clone(source) -- it already carries `mutated` too,
                        // regardless of whether Save_Click's mapping for this property exists. Reset just this
                        // one property back to the type default so the assertion below can only pass if
                        // Save_Click genuinely reads the control back into Settings.
                        property.SetValue(window.Settings, defaultValue);
                        InvokeSave(window);
                    };
                    if (window.ShowDialog() != true)
                    {
                        failures.Add($"{property.Name}: Save was rejected");
                        continue;
                    }
                    var actual = property.GetValue(window.Settings);
                    if (!Equals(actual, mutated))
                        failures.Add($"{property.Name}: expected {mutated}, got {actual} (LoadFields or Save_Click is missing this property)");
                }
                finally { if (window.IsLoaded) window.Close(); }
            }
            return Task.CompletedTask;
        });
        Assert.True(failures.Count == 0, "LoadFields/Save_Click field mapping is broken for:\n" + string.Join('\n', failures));
    }
}
