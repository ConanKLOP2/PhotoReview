using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// Second Fit width command (FitWidthAnchor2 + FitWidth2 shortcut, default key D4) and the middle-click action:
/// defaults, old-config compatibility, round trip, unparsable-value repair (RV-D2), key conflicts.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class FitWidth2MiddleClickSettingsTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");
    private readonly SettingsStore _store;

    public FitWidth2MiddleClickSettingsTests()
    {
        _store = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { });
    }

    [Fact]
    public void Defaults_SecondFitWidthIsBottomThirdOnD4_FirstStaysCentre_MiddleClickIsFullsize()
    {
        var settings = new AppSettings();

        Assert.Equal(FitWidthAnchor.BottomThird, settings.FitWidthAnchor2);
        Assert.Equal(FitWidthAnchor.Centre, settings.FitWidthAnchor);
        Assert.Equal("D4", settings.Shortcuts.FitWidth2);
        Assert.Equal("W", settings.Shortcuts.FitWidth);
        Assert.Equal(MiddleClickAction.ActualSize, settings.MiddleClickAction);
        Assert.Contains(nameof(ShortcutMappings.FitWidth2), ShortcutMappings.OptionalNames);
    }

    [Fact]
    public void Load_OldConfigWithoutTheNewSettings_UsesDefaultsAndReportsNoRepair()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """{ "ConfigVersion": 3, "FitWidthAnchor": "TopThird" }""");

        var loaded = _store.Load();

        Assert.Equal(FitWidthAnchor.TopThird, loaded.FitWidthAnchor); // the old setting keeps meaning "shows 1"
        Assert.Equal(FitWidthAnchor.BottomThird, loaded.FitWidthAnchor2);
        Assert.Equal(MiddleClickAction.ActualSize, loaded.MiddleClickAction);
        Assert.Equal("D4", loaded.Shortcuts.FitWidth2);
        Assert.Empty(_store.LastLoadRepairs);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheNewSettingsByName()
    {
        var settings = new AppSettings { FitWidthAnchor2 = FitWidthAnchor.TopThird, MiddleClickAction = MiddleClickAction.PreviousFolder };
        settings.Shortcuts.FitWidth2 = "Q";

        _store.Save(settings);
        var json = _fileSystem.ReadAllText(_appPaths.ConfigFile);
        var reloaded = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { }).Load();

        Assert.Contains("\"FitWidthAnchor2\": \"TopThird\"", json, StringComparison.Ordinal);
        Assert.Contains("\"MiddleClickAction\": \"PreviousFolder\"", json, StringComparison.Ordinal);
        Assert.Equal(FitWidthAnchor.TopThird, reloaded.FitWidthAnchor2);
        Assert.Equal(MiddleClickAction.PreviousFolder, reloaded.MiddleClickAction);
        Assert.Equal("Q", reloaded.Shortcuts.FitWidth2);
    }

    [Fact]
    public void Load_BottomThirdForTheFirstAnchor_IsAccepted()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """{ "ConfigVersion": 3, "FitWidthAnchor": "BottomThird", "FitWidthAnchor2": "Centre" }""");

        var loaded = _store.Load();

        Assert.Equal(FitWidthAnchor.BottomThird, loaded.FitWidthAnchor);
        Assert.Equal(FitWidthAnchor.Centre, loaded.FitWidthAnchor2);
        Assert.Empty(_store.LastLoadRepairs);
    }

    [Fact]
    public void Normalize_UndefinedNewEnums_AreResetToTheirDefaultsAndReported()
    {
        var settings = new AppSettings { FitWidthAnchor2 = (FitWidthAnchor)77, MiddleClickAction = (MiddleClickAction)77 };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(FitWidthAnchor.BottomThird, settings.FitWidthAnchor2);
        Assert.Equal(MiddleClickAction.ActualSize, settings.MiddleClickAction);
        Assert.Contains(nameof(AppSettings.FitWidthAnchor2), repairs);
        Assert.Contains(nameof(AppSettings.MiddleClickAction), repairs);
    }

    [Fact]
    public void Load_ExistingActionAlreadyOnD4_DisablesFitWidth2InsteadOfFailingValidation()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """
            { "ConfigVersion": 3, "Actions": [ { "Name": "Pick", "Shortcut": "D4", "Operation": "Copy", "Destination": "Picks" } ] }
            """);

        var loaded = _store.Load();

        Assert.Equal("", loaded.Shortcuts.FitWidth2);
        Assert.Equal("D4", loaded.Actions[0].Shortcut); // the user's own binding wins
        Assert.Null(new SettingsValidator(new AlwaysValidKeyNameValidator()).ValidateShortcuts(loaded));
    }

    [Fact]
    public void ValidateShortcuts_FitWidth2SameKeyAsFitWidth_IsADuplicate()
    {
        var settings = new AppSettings();
        settings.Shortcuts.FitWidth2 = settings.Shortcuts.FitWidth;

        Assert.NotNull(new SettingsValidator(new AlwaysValidKeyNameValidator()).ValidateShortcuts(settings));
    }

    [Fact]
    public void ValidateShortcuts_EmptyFitWidth2_IsAllowed()
    {
        var settings = new AppSettings();
        settings.Shortcuts.FitWidth2 = "";

        Assert.Null(new SettingsValidator(new AlwaysValidKeyNameValidator()).ValidateShortcuts(settings));
    }

    private sealed class AlwaysValidKeyNameValidator : PhotoReview.Core.Abstractions.IKeyNameValidator
    {
        public bool IsValidKeyName(string name) => !string.IsNullOrWhiteSpace(name);
    }
}
