using System.Reflection;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Presenters;

/// <summary>
/// WP-35 G0 guard for the Settings field table (<see cref="SettingsFieldMap"/>): every public settable
/// <see cref="AppSettings"/> property is either mapped both ways or listed (with a reason) as deliberately not editable
/// in the dialog, and every mapped property survives Load -> Save. This is the presenter-level twin of the WPF
/// <c>SettingsWindowFieldMapTests</c>: it needs no window, so it also protects a future native view.
/// </summary>
[Collection("GlobalState")]
public sealed class SettingsFieldMapTests
{
    private const long PhysicalMemory = 32L * 1024 * 1024 * 1024;

    private static IEnumerable<PropertyInfo> SettableProperties() =>
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite);

    [Fact(DisplayName = "Every public settable AppSettings property is mapped or listed as not editable in the dialog")]
    public void EveryProperty_IsMappedOrExplicitlyNotEditable()
    {
        var unaccounted = SettableProperties().Select(p => p.Name)
            .Where(name => !SettingsFieldMap.CoveredProperties.Contains(name) && !SettingsFieldMap.NotEditedInDialog.ContainsKey(name))
            .ToList();
        Assert.True(unaccounted.Count == 0, "Add a field row (or a reasoned exclusion) for: " + string.Join(", ", unaccounted));
    }

    [Fact(DisplayName = "Tripwire: the mapped and excluded lists name only real properties and never overlap")]
    public void Lists_NameOnlyRealPropertiesAndDoNotOverlap()
    {
        var all = SettableProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(SettingsFieldMap.CoveredProperties.Except(all));
        Assert.Empty(SettingsFieldMap.NotEditedInDialog.Keys.Except(all));
        Assert.Empty(SettingsFieldMap.CoveredProperties.Intersect(SettingsFieldMap.NotEditedInDialog.Keys));
        Assert.All(SettingsFieldMap.NotEditedInDialog, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value)));
    }

    [Fact(DisplayName = "Every row of the table owns at least one property and no property is owned by two rows")]
    public void Bindings_OwnDistinctProperties()
    {
        var owned = SettingsFieldMap.Bindings.SelectMany(b => b.Properties).ToList();
        Assert.NotEmpty(SettingsFieldMap.Bindings);
        Assert.All(SettingsFieldMap.Bindings, b => Assert.NotEmpty(b.Properties));
        Assert.Equal(owned.Count, owned.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact(DisplayName = "The shortcut accessors cover every ShortcutMappings property except the legacy MoveToFolder2 alias, without duplicates")]
    public void ShortcutAccessors_CoverEveryShortcutProperty()
    {
        var expected = typeof(ShortcutMappings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanWrite && p.Name != nameof(ShortcutMappings.MoveToFolder2))
            .Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        var mapped = SettingsFieldMap.Shortcuts.Select(a => a.Property).ToList();
        Assert.Equal(mapped.Count, mapped.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expected, mapped.Order(StringComparer.Ordinal).ToList());
    }

    [Fact(DisplayName = "Every accessor reads and writes its own ShortcutMappings property")]
    public void ShortcutAccessors_ReadAndWriteTheirOwnProperty()
    {
        foreach (var accessor in SettingsFieldMap.Shortcuts)
        {
            var property = typeof(ShortcutMappings).GetProperty(accessor.Property)!;
            var shortcuts = new ShortcutMappings();
            accessor.Set(shortcuts, "probe-" + accessor.Property);
            Assert.Equal("probe-" + accessor.Property, property.GetValue(shortcuts));
            property.SetValue(shortcuts, "other-" + accessor.Property);
            Assert.Equal("other-" + accessor.Property, accessor.Get(shortcuts));
        }
    }

    [Fact(DisplayName = "Every shortcut round-trips through Load and Save (canonical name kept, MoveToFolder2 carried over)")]
    public void Shortcuts_RoundTripEachProperty()
    {
        // Distinct valid key names for the 28 boxes (a duplicate would be refused by the validator).
        var keys = new[] { "A", "B", "C", "D", "E", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "F1", "F2", "F3" };
        Assert.Equal(SettingsFieldMap.Shortcuts.Count, keys.Length);
        var source = new AppSettings { Actions = [] };
        for (var i = 0; i < keys.Length; i++) SettingsFieldMap.Shortcuts[i].Set(source.Shortcuts, keys[i]);
        source.Shortcuts.MoveToFolder2 = "F9";

        var presenter = new SettingsPresenter(source, physicalMemoryBytes: PhysicalMemory);
        var state = presenter.LoadFields();
        for (var i = 0; i < keys.Length; i++) Assert.Equal(keys[i], state.ShortcutTexts[SettingsFieldMap.Shortcuts[i].Property]);

        foreach (var accessor in SettingsFieldMap.Shortcuts) accessor.Set(presenter.Settings.Shortcuts, ""); // prove Save writes every box back
        var result = presenter.TrySave(state);
        Assert.Equal(SettingsSaveOutcome.Saved, result.Outcome);
        for (var i = 0; i < keys.Length; i++) Assert.Equal(keys[i], SettingsFieldMap.Shortcuts[i].Get(presenter.Settings.Shortcuts));
        Assert.Equal("F9", presenter.Settings.Shortcuts.MoveToFolder2);
    }

    // ---- Generic per-property round trip (twin of SettingsWindowFieldMapTests) ----

    /// <summary>A value that is not the type default and that Save's own validation accepts (ranges, combo-representable).</summary>
    private static object MutateValid(string name, object? current) => name switch
    {
        nameof(AppSettings.InitialViewMode) => InitialViewMode.Percent100, // the combo collapses Percent400 into Percent200
        nameof(AppSettings.ImageSortMode) => ImageSortMode.SizeAscending,
        nameof(AppSettings.ClickZoomPercent) => AppSettings.MaxClickZoomPercent,
        nameof(AppSettings.ArrowPanStepPercent) => AppSettings.MaxArrowPanStepPercent,
        nameof(AppSettings.KeyboardZoomStepPercent) => AppSettings.MaxKeyboardZoomStepPercent,
        nameof(AppSettings.ImageTransitionMs) => AppSettings.MaxImageTransitionMs,
        nameof(AppSettings.TouchpadSwipeDistancePerImage) => AppSettings.MaxTouchpadSwipeDistancePerImage,
        nameof(AppSettings.PreloadForwardCount) => PerformanceOptions.MaxPreloadCount,
        nameof(AppSettings.PreloadBackwardCount) => PerformanceOptions.MaxPreloadCount,
        nameof(AppSettings.ToolbarAutoHideDelayMs) => AppSettings.MaxToolbarAutoHideDelayMs,
        nameof(AppSettings.InfoOverlayAutoHideDelayMs) => AppSettings.MaxInfoOverlayAutoHideDelayMs,
        nameof(AppSettings.InfoOverlayFontSize) => AppSettings.MaxInfoOverlayFontSize,
        nameof(AppSettings.ImageCacheRamPercent) => PerformanceOptions.MaxImageCacheRamPercent,
        nameof(AppSettings.ToolbarOpacityPercent) => AppSettings.MinToolbarOpacityPercent,
        _ => Mutate(current, name)!,
    };

    private static object? Mutate(object? current, string name) => current switch
    {
        bool b => !b,
        int i => i + 1,
        long l => l + 1,
        double d => d + 1,
        string s => "probe-" + name + "-" + s,
        Enum e => MutateEnum(e),
        null => "probe-" + name,
        _ => null,
    };

    private static object MutateEnum(Enum current)
    {
        var type = current.GetType();
        if (type.IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            var all = Enum.GetValues(type).Cast<Enum>().Aggregate(0L, (acc, v) => acc | Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture));
            var distinct = (Convert.ToInt64(current, System.Globalization.CultureInfo.InvariantCulture) ^ all) & all;
            return Enum.ToObject(type, distinct);
        }
        return Enum.GetValues(type).Cast<Enum>().First(v => !v.Equals(current));
    }

    [Fact(DisplayName = "Every mapped scalar AppSettings property survives Load then Save (only the field row can put it back)")]
    public void MappedProperties_RoundTripThroughLoadAndSave()
    {
        // Shortcuts/Actions/UiLanguage and the context-menu list have dedicated tests (nested or not a scalar).
        var special = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(AppSettings.Shortcuts), nameof(AppSettings.Actions), nameof(AppSettings.UiLanguage), nameof(AppSettings.HiddenContextMenuItems),
            nameof(AppSettings.ExternalEditors), // a list of objects: ExternalEditorsSettingsPresenterTests round-trips it
        };
        var properties = SettableProperties().Where(p => SettingsFieldMap.CoveredProperties.Contains(p.Name) && !special.Contains(p.Name)).ToList();
        Assert.NotEmpty(properties);

        var failures = new List<string>();
        foreach (var property in properties)
        {
            var baseline = new AppSettings();
            var wanted = MutateValid(property.Name, property.GetValue(baseline));
            property.SetValue(baseline, wanted);

            var presenter = new SettingsPresenter(baseline, physicalMemoryBytes: PhysicalMemory);
            var state = presenter.LoadFields();
            // Reset just this property on the working copy: only Save writing the loaded control back can restore it.
            property.SetValue(presenter.Settings, property.GetValue(new AppSettings()));
            var result = presenter.TrySave(state);
            if (result.Outcome != SettingsSaveOutcome.Saved)
            {
                failures.Add($"{property.Name}: Save refused: {result.Message}");
                continue;
            }
            var actual = property.GetValue(presenter.Settings);
            if (!Equals(actual, wanted)) failures.Add($"{property.Name}: expected {wanted} but Save left {actual}");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact(DisplayName = "The right-click menu tick boxes round-trip: hidden items stay hidden, locked items can never be hidden")]
    public void ContextMenu_RoundTripsHiddenSet_AndKeepsLockedItemsShown()
    {
        var all = ContextMenuItems.Items.Select(i => i.Id).ToList();
        var hidden = all.Where(id => !ContextMenuItems.Info(id).Locked).Take(3).ToList();
        var source = new AppSettings();
        ContextMenuItems.ApplyHidden(source, hidden);

        var presenter = new SettingsPresenter(source, physicalMemoryBytes: PhysicalMemory);
        var state = presenter.LoadFields();
        Assert.Equal(all.Count - hidden.Count, state.ContextMenuShown.Count);
        Assert.All(hidden, id => Assert.DoesNotContain(id, state.ContextMenuShown));
        Assert.All(all.Where(id => ContextMenuItems.Info(id).Locked), id => Assert.Contains(id, state.ContextMenuShown));

        presenter.Settings.HiddenContextMenuItems = null;
        presenter.Settings.ShowFolderMenuItems = !source.ShowFolderMenuItems;
        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);
        Assert.Equal(hidden.Order().ToList(), ContextMenuItems.EffectiveHidden(presenter.Settings).Order().ToList());

        // Un-ticking one more item in the form saves it as hidden too.
        var extra = all.First(id => !ContextMenuItems.Info(id).Locked && !hidden.Contains(id));
        state.ContextMenuShown.Remove(extra);
        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);
        Assert.Contains(extra, ContextMenuItems.EffectiveHidden(presenter.Settings));
    }

    [Fact(DisplayName = "Exif and title-bar field flags round-trip as a whole (the view only maps the ticked boxes to flags)")]
    public void FieldFlags_RoundTrip()
    {
        var source = new AppSettings { ExifInfoFields = ExifInfoFields.Camera | ExifInfoFields.Iso, TitleBarFields = TitleBarFields.FileName | TitleBarFields.FolderPath };
        var presenter = new SettingsPresenter(source, physicalMemoryBytes: PhysicalMemory);
        var state = presenter.LoadFields();
        Assert.Equal(ExifInfoFields.Camera | ExifInfoFields.Iso, state.ExifInfoFields);
        Assert.Equal(TitleBarFields.FileName | TitleBarFields.FolderPath, state.TitleBarFields);

        state.ExifInfoFields = ExifInfoFields.Lens;
        state.TitleBarFields = TitleBarFields.None;
        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);
        Assert.Equal(ExifInfoFields.Lens, presenter.Settings.ExifInfoFields);
        Assert.Equal(TitleBarFields.None, presenter.Settings.TitleBarFields);
    }
}
