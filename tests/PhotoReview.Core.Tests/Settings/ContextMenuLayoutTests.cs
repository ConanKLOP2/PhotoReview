using System.Text.Json;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// The right-click menu customisation: the pure visibility rule (<see cref="ContextMenuItems.Compute"/>), the migration of the
/// legacy Show*MenuItems flags into <see cref="AppSettings.HiddenContextMenuItems"/>, normalisation and the config round trip.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ContextMenuLayoutTests
{
    private static readonly ContextMenuContext WithPhoto = new(HasPhoto: true);
    private static readonly ContextMenuContext NoPhoto = new(HasPhoto: false);

    private static HashSet<ContextMenuItemId> Hide(params ContextMenuItemId[] ids) => new HashSet<ContextMenuItemId>(ids);

    private static string Render(ContextMenuLayout layout) =>
        string.Join(",", layout.Entries.Select(e => e.Item?.ToString() ?? "|"));

    private static readonly ContextMenuItemId[] AllTopLevel =
        [.. ContextMenuItems.Items.Where(i => i.Parent is null).Select(i => i.Id)];

    [Fact]
    public void Nothing_hidden_shows_every_item_with_a_separator_between_each_group()
    {
        var layout = ContextMenuItems.Compute(Hide(), WithPhoto);

        Assert.Equal("Undo,MoveToRecycleBin,|,Fit,ZoomToLevel,ZoomSubmenu,Refresh,|,OpenFolder,NextFolder,PreviousFolder,|,ExternalEditor,|,CopyFileName,CopyFullPath,|,Settings",
            Render(layout));
    }

    [Fact]
    public void Item_ids_are_stable_names_and_the_table_has_no_duplicates()
    {
        Assert.Equal(Enum.GetValues<ContextMenuItemId>().Length, ContextMenuItems.Items.Count);
        Assert.Equal(ContextMenuItems.Items.Count, ContextMenuItems.Items.Select(i => i.Id).Distinct().Count());
        // These names are written to config.json: renaming one silently un-hides/hides an item for existing users.
        Assert.Equal(["Undo", "MoveToRecycleBin", "Fit", "ZoomToLevel", "ZoomSubmenu", "OpenFolder", "NextFolder", "PreviousFolder",
            "ExternalEditor", "CopyFileName", "CopyFullPath", "Settings", "ZoomFitWidth", "ZoomFitWidth2", "ZoomFitHeight", "ZoomPresets", "ZoomLevelOptions", "Refresh"],
            Enum.GetNames<ContextMenuItemId>());
    }

    [Fact]
    public void Copy_items_need_an_open_photo_on_top_of_the_user_flag()
    {
        var noPhoto = ContextMenuItems.Compute(Hide(), NoPhoto);

        Assert.False(noPhoto.IsVisible(ContextMenuItemId.CopyFileName));
        Assert.False(noPhoto.IsVisible(ContextMenuItemId.CopyFullPath));
        Assert.False(noPhoto.HasSeparatorBefore(5));
        Assert.True(ContextMenuItems.Compute(Hide(), WithPhoto).IsVisible(ContextMenuItemId.CopyFileName));
        // The user flag still wins when a photo is open.
        var hiddenByUser = ContextMenuItems.Compute(Hide(ContextMenuItemId.CopyFileName), WithPhoto);
        Assert.False(hiddenByUser.IsVisible(ContextMenuItemId.CopyFileName));
        Assert.True(hiddenByUser.IsVisible(ContextMenuItemId.CopyFullPath));
    }

    [Fact]
    public void An_emptied_group_takes_its_separator_with_it()
    {
        var layout = ContextMenuItems.Compute(Hide(ContextMenuItemId.OpenFolder, ContextMenuItemId.NextFolder, ContextMenuItemId.PreviousFolder), WithPhoto);

        Assert.Equal("Undo,MoveToRecycleBin,|,Fit,ZoomToLevel,ZoomSubmenu,Refresh,|,ExternalEditor,|,CopyFileName,CopyFullPath,|,Settings", Render(layout));
    }

    [Fact]
    public void First_group_never_starts_with_a_separator()
    {
        var layout = ContextMenuItems.Compute(Hide(ContextMenuItemId.Undo, ContextMenuItemId.MoveToRecycleBin), NoPhoto);

        Assert.Equal("Fit,ZoomToLevel,ZoomSubmenu,Refresh,|,OpenFolder,NextFolder,PreviousFolder,|,ExternalEditor,|,Settings", Render(layout));
    }

    [Fact]
    public void Hiding_everything_leaves_only_Settings_without_any_separator()
    {
        var layout = ContextMenuItems.Compute(Hide(AllTopLevel), WithPhoto);

        Assert.Equal("Settings", Render(layout));
    }

    [Fact]
    public void Settings_cannot_be_hidden_even_when_listed()
    {
        var layout = ContextMenuItems.Compute(Hide(ContextMenuItemId.Settings), NoPhoto);

        Assert.True(layout.IsVisible(ContextMenuItemId.Settings));
        Assert.Empty(ContextMenuItems.ParseHidden(["Settings"]));
    }

    [Fact]
    public void Menu_always_has_at_least_one_item_for_every_combination_of_flags()
    {
        // 2^11 subsets of the 11 hideable top-level items x both contexts.
        var hideable = AllTopLevel.Where(id => !ContextMenuItems.Info(id).Locked).ToArray();
        for (var mask = 0; mask < 1 << hideable.Length; mask++)
        {
            var hidden = Hide([.. hideable.Where((_, i) => (mask & (1 << i)) != 0)]);
            foreach (var context in new[] { WithPhoto, NoPhoto })
            {
                var entries = ContextMenuItems.Compute(hidden, context).Entries;
                Assert.NotEmpty(entries);
                Assert.NotNull(entries[0].Item);                 // never starts with a separator
                Assert.NotNull(entries[^1].Item);                // never ends with one
                for (var i = 1; i < entries.Count; i++)
                    Assert.False(entries[i].Item is null && entries[i - 1].Item is null); // never two in a row
            }
        }
    }

    [Fact]
    public void Zoom_submenu_collapses_when_the_user_hides_it_or_all_of_its_children()
    {
        Assert.False(ContextMenuItems.Compute(Hide(ContextMenuItemId.ZoomSubmenu), WithPhoto).IsVisible(ContextMenuItemId.ZoomSubmenu));

        var allChildren = Hide(ContextMenuItemId.ZoomFitWidth, ContextMenuItemId.ZoomFitWidth2, ContextMenuItemId.ZoomFitHeight,
            ContextMenuItemId.ZoomPresets, ContextMenuItemId.ZoomLevelOptions);
        Assert.False(ContextMenuItems.Compute(allChildren, WithPhoto).IsVisible(ContextMenuItemId.ZoomSubmenu));
        Assert.Empty(ContextMenuItems.Compute(allChildren, WithPhoto, ContextMenuItemId.ZoomSubmenu).Entries);
    }

    [Fact]
    public void Zoom_submenu_children_are_toggled_individually_and_separators_follow_the_groups()
    {
        var all = ContextMenuItems.Compute(Hide(), WithPhoto, ContextMenuItemId.ZoomSubmenu);
        Assert.Equal("ZoomFitWidth,ZoomFitWidth2,ZoomFitHeight,|,ZoomPresets,|,ZoomLevelOptions", Render(all));

        var noFitWidth2 = ContextMenuItems.Compute(Hide(ContextMenuItemId.ZoomFitWidth2), WithPhoto, ContextMenuItemId.ZoomSubmenu);
        Assert.Equal("ZoomFitWidth,ZoomFitHeight,|,ZoomPresets,|,ZoomLevelOptions", Render(noFitWidth2));

        var onlyOptions = ContextMenuItems.Compute(Hide(ContextMenuItemId.ZoomFitWidth, ContextMenuItemId.ZoomFitWidth2, ContextMenuItemId.ZoomFitHeight, ContextMenuItemId.ZoomPresets),
            WithPhoto, ContextMenuItemId.ZoomSubmenu);
        Assert.Equal("ZoomLevelOptions", Render(onlyOptions));
    }

    [Fact]
    public void Legacy_flags_map_to_the_hidden_set()
    {
        var defaults = ContextMenuItems.LegacyHidden(new AppSettings());
        Assert.True(defaults.SetEquals(ContextMenuItems.DefaultHidden)); // zoom cluster + recycle hidden, folder group shown: today's defaults

        var allOn = ContextMenuItems.LegacyHidden(new AppSettings { ShowZoomMenuItems = true, ShowFolderMenuItems = true, ShowRecycleMenuItem = true });
        Assert.Empty(allOn);

        var folderOff = ContextMenuItems.LegacyHidden(new AppSettings { ShowZoomMenuItems = true, ShowFolderMenuItems = false, ShowRecycleMenuItem = true });
        Assert.True(folderOff.SetEquals([ContextMenuItemId.OpenFolder, ContextMenuItemId.NextFolder, ContextMenuItemId.PreviousFolder]));
    }

    [Fact]
    public void EffectiveHidden_prefers_the_stored_list_and_treats_empty_as_show_everything()
    {
        var withList = new AppSettings { ShowZoomMenuItems = false, HiddenContextMenuItems = [] };
        Assert.Empty(ContextMenuItems.EffectiveHidden(withList));

        var legacyOnly = new AppSettings { ShowZoomMenuItems = false, HiddenContextMenuItems = null };
        Assert.Contains(ContextMenuItemId.Fit, ContextMenuItems.EffectiveHidden(legacyOnly));
    }

    [Fact]
    public void ApplyHidden_stores_names_in_menu_order_and_mirrors_the_legacy_flags()
    {
        var settings = new AppSettings();

        ContextMenuItems.ApplyHidden(settings, [ContextMenuItemId.CopyFullPath, ContextMenuItemId.Fit, ContextMenuItemId.Settings, ContextMenuItemId.Fit]);

        Assert.Equal(["Fit", "CopyFullPath"], settings.HiddenContextMenuItems);
        Assert.True(settings.ShowZoomMenuItems);  // ZoomToLevel and the submenu are still shown
        Assert.True(settings.ShowFolderMenuItems);
        Assert.True(settings.ShowRecycleMenuItem);

        ContextMenuItems.ApplyHidden(settings, ContextMenuItems.DefaultHidden);
        Assert.False(settings.ShowZoomMenuItems);
        Assert.True(settings.ShowFolderMenuItems);
        Assert.False(settings.ShowRecycleMenuItem);
    }

    // ---- store: migration, normalisation, round trip ----

    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");

    private SettingsStore NewStore() => new(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { });

    [Fact]
    public void Load_old_config_without_the_list_migrates_the_legacy_flags_into_it()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile,
            """{ "ConfigVersion": 3, "ShowFolderMenuItems": false, "ShowZoomMenuItems": true, "ShowRecycleMenuItem": true }""");

        var loaded = NewStore().Load();

        Assert.Equal(["OpenFolder", "NextFolder", "PreviousFolder"], loaded.HiddenContextMenuItems);
    }

    [Fact]
    public void Load_config_with_no_menu_settings_at_all_keeps_todays_defaults()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """{ "ConfigVersion": 3 }""");

        var loaded = NewStore().Load();

        Assert.Equal(["MoveToRecycleBin", "Fit", "ZoomToLevel", "ZoomSubmenu"], loaded.HiddenContextMenuItems);
    }

    [Fact]
    public void Load_an_explicit_list_wins_over_the_legacy_flags_and_empty_means_show_all()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile,
            """{ "ConfigVersion": 3, "ShowFolderMenuItems": false, "ShowZoomMenuItems": false, "HiddenContextMenuItems": [] }""");

        var loaded = NewStore().Load();

        Assert.NotNull(loaded.HiddenContextMenuItems);
        Assert.Empty(loaded.HiddenContextMenuItems);
        Assert.Empty(ContextMenuItems.EffectiveHidden(loaded));
    }

    [Fact]
    public void Load_drops_unknown_duplicate_and_locked_names_and_reports_the_repair()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile,
            """{ "ConfigVersion": 3, "HiddenContextMenuItems": ["CopyFullPath", "Bogus", "Settings", "Fit", "Fit", null, "undo"] }""");
        var store = NewStore();

        var loaded = store.Load();

        Assert.Equal(["Fit", "CopyFullPath"], loaded.HiddenContextMenuItems);
        Assert.Contains(nameof(AppSettings.HiddenContextMenuItems), store.LastLoadRepairs);
    }

    [Fact]
    public void Load_a_list_of_the_wrong_json_type_is_salvaged_without_losing_other_settings()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile,
            """{ "ConfigVersion": 3, "PreloadWorkerCount": 7, "HiddenContextMenuItems": "Fit" }""");

        var loaded = NewStore().Load();

        Assert.Equal(7, loaded.PreloadWorkerCount);
        // Unusable value -> default (absent) -> migrated from the legacy defaults.
        Assert.Equal(["MoveToRecycleBin", "Fit", "ZoomToLevel", "ZoomSubmenu"], loaded.HiddenContextMenuItems);
    }

    [Fact]
    public void Save_then_Load_round_trips_the_list()
    {
        var settings = new AppSettings();
        ContextMenuItems.ApplyHidden(settings, [ContextMenuItemId.Undo, ContextMenuItemId.ZoomFitWidth2, ContextMenuItemId.ExternalEditor]);
        NewStore().Save(settings);

        var reloaded = NewStore().Load();

        Assert.Equal(["Undo", "ExternalEditor", "ZoomFitWidth2"], reloaded.HiddenContextMenuItems);
    }

    [Fact]
    public void Clone_and_json_export_keep_the_list_and_an_empty_list_stays_empty()
    {
        var settings = new AppSettings { HiddenContextMenuItems = [] };

        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        var parsed = SettingsStore.ParseText(json).Settings;

        Assert.Empty(Assert.IsType<List<string>>(parsed.HiddenContextMenuItems));
        Assert.Empty(Assert.IsType<List<string>>(AppSettings.Clone(settings).HiddenContextMenuItems));
    }
}
