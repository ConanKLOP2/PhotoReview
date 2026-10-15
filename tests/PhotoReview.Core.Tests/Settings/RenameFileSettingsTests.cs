using System.Text.Json;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// FEAT-RENAME (PR3): the settings side of "Rename...": the stored enum name of the menu item (never renumbered, added at the end),
/// the optional shortcut (default F2, may be cleared), its place in the conflict rules, and old configs that know nothing about it.
/// </summary>
public sealed class RenameFileSettingsTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly AppPaths _paths = new(@"C:\Users\test\AppData\Local");

    private SettingsStore NewStore() => new(_paths, _fs, NullLog.Instance, (_, _) => { });

    [Fact]
    public void TheMenuItemId_IsAppendedAtTheEnd_SoStoredNamesAndValuesOfTheOlderItemsDoNotMove()
    {
        var ids = Enum.GetValues<ContextMenuItemId>();

        Assert.Equal(ContextMenuItemId.RenameFile, ids[^1]);
        Assert.Equal(18, (int)ContextMenuItemId.RenameFile);
        Assert.Equal(17, (int)ContextMenuItemId.Refresh); // the previous last one keeps its value
        Assert.Equal("RenameFile", ContextMenuItemId.RenameFile.ToString()); // what HiddenContextMenuItems stores
    }

    [Fact]
    public void TheMenuItem_NeedsAPhoto_IsTopLevel_SitsBesideTheCopyItems_AndIsShownByDefault()
    {
        var info = ContextMenuItems.Info(ContextMenuItemId.RenameFile);

        Assert.True(info.NeedsPhoto);
        Assert.False(info.Locked);
        Assert.Null(info.Parent);
        Assert.Equal(ContextMenuItems.Info(ContextMenuItemId.CopyFileName).Group, info.Group);
        Assert.DoesNotContain(ContextMenuItemId.RenameFile, ContextMenuItems.DefaultHidden);
        Assert.DoesNotContain(ContextMenuItemId.RenameFile, ContextMenuItems.EffectiveHidden(new AppSettings()));
        // A config saved before the item existed lists other hidden items only: the new item shows.
        var old = new AppSettings { HiddenContextMenuItems = ["MoveToRecycleBin", "Fit"] };
        Assert.DoesNotContain(ContextMenuItemId.RenameFile, ContextMenuItems.EffectiveHidden(old));
    }

    [Fact]
    public void TheMenuItem_WithAndWithoutAPhoto_ComputesIntoTheLayoutAccordingly()
    {
        var withPhoto = ContextMenuItems.Compute(new HashSet<ContextMenuItemId>(), new ContextMenuContext(HasPhoto: true, IncludeShellOnly: true));
        var without = ContextMenuItems.Compute(new HashSet<ContextMenuItemId>(), new ContextMenuContext(HasPhoto: false, IncludeShellOnly: true));
        var hidden = ContextMenuItems.Compute(new HashSet<ContextMenuItemId> { ContextMenuItemId.RenameFile },
            new ContextMenuContext(true, IncludeShellOnly: true));

        Assert.True(withPhoto.IsVisible(ContextMenuItemId.RenameFile));
        Assert.False(without.IsVisible(ContextMenuItemId.RenameFile));
        Assert.False(hidden.IsVisible(ContextMenuItemId.RenameFile));
        Assert.True(hidden.IsVisible(ContextMenuItemId.CopyFileName)); // hiding one does not hide the others of the group
    }

    [Fact]
    public void TheWpfLayout_NeverCountsTheWin32OnlyItem_NorLeavesItsSeparatorBehind()
    {
        // The frozen WPF window computes its layout with the default context: its menu has no Rename element, so a lone Rename must
        // not make the copy group "exist" (a stray separator in a menu that cannot show the item).
        var copyHidden = new HashSet<ContextMenuItemId> { ContextMenuItemId.CopyFileName, ContextMenuItemId.CopyFullPath };

        var wpf = ContextMenuItems.Compute(copyHidden, new ContextMenuContext(HasPhoto: true));
        var win32 = ContextMenuItems.Compute(copyHidden, new ContextMenuContext(HasPhoto: true, IncludeShellOnly: true));

        Assert.False(wpf.IsVisible(ContextMenuItemId.RenameFile));
        Assert.False(wpf.HasSeparatorBefore(5));
        Assert.True(win32.IsVisible(ContextMenuItemId.RenameFile));
        Assert.True(win32.HasSeparatorBefore(5));
        Assert.True(ContextMenuItems.Info(ContextMenuItemId.RenameFile).ShellOnly);
        Assert.Equal(1, ContextMenuItems.Items.Count(i => i.ShellOnly)); // the only Win32-only item
    }

    [Fact]
    public void TheHiddenNameRoundTrips_ThroughParseAndStore()
    {
        var stored = ContextMenuItems.ToStored([ContextMenuItemId.RenameFile, ContextMenuItemId.Fit]);

        Assert.Equal(["Fit", "RenameFile"], stored);
        Assert.Equal([ContextMenuItemId.Fit, ContextMenuItemId.RenameFile], ContextMenuItems.ParseHidden(stored).Order());
    }

    [Fact]
    public void TheShortcut_DefaultsToF2_IsOptional_AndAClearedKeyStaysCleared()
    {
        Assert.Equal("F2", new AppSettings().Shortcuts.RenameFile);
        Assert.Equal("F2", ShortcutMappings.Default().RenameFile);
        Assert.True(ShortcutMappings.IsOptional(nameof(ShortcutMappings.RenameFile)));

        var cleared = new AppSettings();
        cleared.Shortcuts.RenameFile = "  ";
        SettingsNormalizer.Normalize(cleared);
        Assert.Equal(string.Empty, cleared.Shortcuts.RenameFile);
    }

    [Fact]
    public void AnOldConfigWithoutTheKey_GetsF2_AndAClearedKeyIsKeptThroughSaveAndLoad()
    {
        _fs.AddFile(_paths.ConfigFile, """{ "ConfigVersion": 4, "UiLanguage": "en", "Shortcuts": { "Refresh": "R", "Next": "Right" } }""");
        var store = NewStore();

        var loaded = store.Load();

        Assert.Equal("F2", loaded.Shortcuts.RenameFile);

        loaded.Shortcuts.RenameFile = string.Empty; // the user cleared it in Settings
        store.Save(loaded);
        using var doc = JsonDocument.Parse(_fs.ReadAllText(_paths.ConfigFile));
        Assert.Equal(string.Empty, doc.RootElement.GetProperty("Shortcuts").GetProperty("RenameFile").GetString());
        Assert.Equal(string.Empty, NewStore().Load().Shortcuts.RenameFile);
    }

    [Fact]
    public void AUserActionOnF2_WinsOverTheNewDefault_AndTheRenameKeyIsSilentlyDisabled()
    {
        // The Q2 risk: a config that already binds F2 to an Action keeps working; only the new optional key gives way.
        var settings = new AppSettings();
        settings.Actions[0].Shortcut = "F2";

        var disabled = SettingsNormalizer.DisableConflictingOptionalShortcuts(settings);

        Assert.Contains(nameof(ShortcutMappings.RenameFile), disabled);
        Assert.Equal(string.Empty, settings.Shortcuts.RenameFile);
        Assert.Equal("F2", settings.Actions[0].Shortcut);
    }

    [Fact]
    public void AKeyAlsoUsedByAnotherOptionalShortcut_IsAConflictInTheValidator()
    {
        var validator = new SettingsValidator(new AnyKeyName());
        var settings = new AppSettings();
        Assert.Null(validator.ValidateShortcuts(settings)); // the defaults are consistent

        settings.Shortcuts.RenameFile = settings.Actions[0].Shortcut;
        Assert.NotNull(validator.ValidateShortcuts(settings));

        settings.Shortcuts.RenameFile = settings.Shortcuts.Refresh;
        Assert.NotNull(validator.ValidateShortcuts(settings));

        settings.Shortcuts.RenameFile = "F8";
        Assert.Null(validator.ValidateShortcuts(settings));
    }

    private sealed class AnyKeyName : PhotoReview.Core.Abstractions.IKeyNameValidator
    {
        public bool IsValidKeyName(string keyName) => true;
    }
}
