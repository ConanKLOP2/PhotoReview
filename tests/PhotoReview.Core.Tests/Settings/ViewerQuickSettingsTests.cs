using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// Info-overlay settings (ShowInfoOverlay/ShowFileInfo/ShowFolderInfo) and the optional shortcuts
/// (LastImage/ZoomActualSize/ToggleInfoOverlay; empty = disabled): defaults, round-trip, old configs, validation.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ViewerQuickSettingsTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");

    private SettingsStore NewStore() => new(_appPaths, _fileSystem, NullLog.Instance);

    private sealed class AllKeysValid : IKeyNameValidator
    {
        public bool IsValidKeyName(string keyName) => !string.IsNullOrWhiteSpace(keyName) && keyName.Trim() != "Nope";
    }

    [Fact]
    public void Defaults_OverlaysOn_AndShortcutsEndD1I()
    {
        var settings = new AppSettings();

        Assert.True(settings.ShowInfoOverlay);
        Assert.True(settings.ShowFileInfo);
        // Off by default: the window title already shows the current folder name.
        Assert.False(settings.ShowFolderInfo);
        Assert.Equal("End", settings.Shortcuts.LastImage);
        Assert.Equal("D1", settings.Shortcuts.ZoomActualSize);
        Assert.Equal("I", settings.Shortcuts.ToggleInfoOverlay);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsOffSwitchesAndEmptyShortcuts()
    {
        var settings = new AppSettings { ShowInfoOverlay = false, ShowFileInfo = false, ShowFolderInfo = false };
        settings.Shortcuts.LastImage = "";
        settings.Shortcuts.ZoomActualSize = "";
        settings.Shortcuts.ToggleInfoOverlay = "";
        NewStore().Save(settings);

        var store = NewStore();
        var loaded = store.Load();

        Assert.False(loaded.ShowInfoOverlay);
        Assert.False(loaded.ShowFileInfo);
        Assert.False(loaded.ShowFolderInfo);
        Assert.Equal("", loaded.Shortcuts.LastImage);
        Assert.Equal("", loaded.Shortcuts.ZoomActualSize);
        Assert.Equal("", loaded.Shortcuts.ToggleInfoOverlay);
        Assert.Empty(store.LastLoadRepairs);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsCustomShortcutKeys()
    {
        var settings = new AppSettings();
        settings.Shortcuts.LastImage = "F7";
        settings.Shortcuts.ZoomActualSize = "D0";
        settings.Shortcuts.ToggleInfoOverlay = "O";
        NewStore().Save(settings);

        var loaded = NewStore().Load();

        Assert.Equal("F7", loaded.Shortcuts.LastImage);
        Assert.Equal("D0", loaded.Shortcuts.ZoomActualSize);
        Assert.Equal("O", loaded.Shortcuts.ToggleInfoOverlay);
    }

    [Fact]
    public void Load_OldConfigWithoutNewKeys_GetsDefaults()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """
        {
            "ConfigVersion": 3,
            "LoggingEnabled": true,
            "Shortcuts": { "Next": "Right", "Previous": "Left" },
            "Actions": [ { "Name": "Keep", "Shortcut": "Enter", "Operation": "Move", "Destination": "Keep" } ]
        }
        """);

        var loaded = NewStore().Load();

        Assert.True(loaded.LoggingEnabled);
        Assert.True(loaded.ShowInfoOverlay);
        Assert.True(loaded.ShowFileInfo);
        Assert.False(loaded.ShowFolderInfo);
        Assert.Equal("End", loaded.Shortcuts.LastImage);
        Assert.Equal("D1", loaded.Shortcuts.ZoomActualSize);
        Assert.Equal("I", loaded.Shortcuts.ToggleInfoOverlay);
    }

    [Fact]
    public void Load_OldConfigWhoseActionAlreadyUsesANewDefaultKey_DisablesOnlyThatOptionalShortcut()
    {
        // Written before ZoomActualSize existed: the user's own action on the "1" key must keep working.
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """
        {
            "ConfigVersion": 3,
            "Actions": [ { "Name": "Best", "Shortcut": "D1", "Operation": "Move", "Destination": "Best" } ]
        }
        """);

        var loaded = NewStore().Load();

        Assert.Equal("", loaded.Shortcuts.ZoomActualSize);
        Assert.Equal("End", loaded.Shortcuts.LastImage);
        Assert.Equal("I", loaded.Shortcuts.ToggleInfoOverlay);
        Assert.Equal("D1", loaded.Actions[0].Shortcut);
        Assert.Null(new SettingsValidator(new AllKeysValid()).ValidateShortcuts(loaded));
    }

    [Fact]
    public void DisableConflictingOptionalShortcuts_ConflictWithBuiltInShortcut_DisablesIt()
    {
        var settings = new AppSettings();
        settings.Shortcuts.ToggleInfoOverlay = "c"; // Compare is "C" (keys compare case-insensitively)

        var disabled = SettingsNormalizer.DisableConflictingOptionalShortcuts(settings);

        Assert.Equal(new[] { nameof(ShortcutMappings.ToggleInfoOverlay) }, disabled);
        Assert.Equal("", settings.Shortcuts.ToggleInfoOverlay);
    }

    [Fact]
    public void DisableConflictingOptionalShortcuts_NoConflict_KeepsEverything()
    {
        var settings = new AppSettings();

        Assert.Empty(SettingsNormalizer.DisableConflictingOptionalShortcuts(settings));
        Assert.Equal("End", settings.Shortcuts.LastImage);
        Assert.Equal("D1", settings.Shortcuts.ZoomActualSize);
        Assert.Equal("I", settings.Shortcuts.ToggleInfoOverlay);
    }

    [Fact]
    public void Load_ExplicitNullOptionalShortcut_BecomesDisabled()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """
        { "ConfigVersion": 3, "Shortcuts": { "LastImage": null, "ZoomActualSize": "  D1  " } }
        """);

        var loaded = NewStore().Load();

        Assert.Equal("", loaded.Shortcuts.LastImage);
        Assert.Equal("D1", loaded.Shortcuts.ZoomActualSize);
    }

    [Fact]
    public void Validator_EmptyOptionalShortcuts_AreValid()
    {
        var settings = new AppSettings();
        settings.Shortcuts.LastImage = "";
        settings.Shortcuts.ZoomActualSize = "   ";
        settings.Shortcuts.ToggleInfoOverlay = "";

        Assert.Null(new SettingsValidator(new AllKeysValid()).ValidateShortcuts(settings));
    }

    [Fact]
    public void Validator_EmptyMandatoryShortcut_IsStillInvalid()
    {
        var settings = new AppSettings();
        settings.Shortcuts.FirstImage = "";

        Assert.NotNull(new SettingsValidator(new AllKeysValid()).ValidateShortcuts(settings));
    }

    [Theory]
    [InlineData(nameof(ShortcutMappings.LastImage))]
    [InlineData(nameof(ShortcutMappings.ZoomActualSize))]
    [InlineData(nameof(ShortcutMappings.ToggleInfoOverlay))]
    public void Validator_UnparseableOptionalShortcut_IsInvalid(string name)
    {
        var settings = new AppSettings();
        typeof(ShortcutMappings).GetProperty(name)!.SetValue(settings.Shortcuts, "Nope");

        var error = new SettingsValidator(new AllKeysValid()).ValidateShortcuts(settings);

        Assert.NotNull(error);
        Assert.Contains(name, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(ShortcutMappings.LastImage), "Home")]           // FirstImage
    [InlineData(nameof(ShortcutMappings.ZoomActualSize), "F3")]        // default action "Loại 3"
    [InlineData(nameof(ShortcutMappings.ToggleInfoOverlay), "PageUp")] // PreviousFolder
    public void Validator_OptionalShortcutDuplicatingAnotherBinding_IsReported(string name, string key)
    {
        var settings = new AppSettings();
        typeof(ShortcutMappings).GetProperty(name)!.SetValue(settings.Shortcuts, key);

        var error = new SettingsValidator(new AllKeysValid()).ValidateShortcuts(settings);

        Assert.NotNull(error);
        Assert.Contains(name, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_DefaultSettings_HaveNoConflicts()
    {
        Assert.Null(new SettingsValidator(new AllKeysValid()).ValidateShortcuts(new AppSettings()));
    }
}
