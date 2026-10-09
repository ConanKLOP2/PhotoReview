using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// Q-TOUCHPAD-REFRESH settings: touchpad swipe switch + distance per photo, and the Refresh shortcut (default R, never F5 which
/// is the default Backup action's key): defaults, old-config compatibility, round trip, clamping, key conflicts.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class TouchpadRefreshSettingsTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");
    private readonly SettingsStore _store;

    public TouchpadRefreshSettingsTests()
    {
        _store = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { });
    }

    [Fact]
    public void Defaults_SwipeOn_200Units_RefreshOnR()
    {
        var settings = new AppSettings();

        Assert.True(settings.TouchpadSwipeEnabled);
        Assert.Equal(AppSettings.DefaultTouchpadSwipeDistancePerImage, settings.TouchpadSwipeDistancePerImage);
        Assert.Equal(200, AppSettings.DefaultTouchpadSwipeDistancePerImage);
        Assert.Equal("R", settings.Shortcuts.Refresh);
        Assert.Contains(nameof(ShortcutMappings.Refresh), ShortcutMappings.OptionalNames);
    }

    [Fact]
    public void Defaults_RefreshDoesNotTakeAnyDefaultActionOrShortcutKey()
    {
        var settings = new AppSettings();

        Assert.DoesNotContain(settings.Actions, a => string.Equals(a.Shortcut, settings.Shortcuts.Refresh, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(settings.Actions, a => a.Shortcut == "F5"); // the reason Refresh is not on F5
        Assert.Null(new SettingsValidator(new AlwaysValidKeyNameValidator()).ValidateShortcuts(settings));
    }

    [Fact]
    public void Load_OldConfigWithoutTheNewSettings_UsesDefaultsAndReportsNoRepair()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """{ "ConfigVersion": 3, "MouseWheelAction": "Navigate" }""");

        var loaded = _store.Load();

        Assert.Equal(MouseWheelAction.Navigate, loaded.MouseWheelAction);
        Assert.True(loaded.TouchpadSwipeEnabled);
        Assert.Equal(AppSettings.DefaultTouchpadSwipeDistancePerImage, loaded.TouchpadSwipeDistancePerImage);
        Assert.Equal("R", loaded.Shortcuts.Refresh);
        Assert.Empty(_store.LastLoadRepairs);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheNewSettings()
    {
        var settings = new AppSettings { TouchpadSwipeEnabled = false, TouchpadSwipeDistancePerImage = 480 };
        settings.Shortcuts.Refresh = "F12";

        _store.Save(settings);
        var reloaded = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { }).Load();

        Assert.False(reloaded.TouchpadSwipeEnabled);
        Assert.Equal(480, reloaded.TouchpadSwipeDistancePerImage);
        Assert.Equal("F12", reloaded.Shortcuts.Refresh);
    }

    [Theory]
    [InlineData(0, AppSettings.MinTouchpadSwipeDistancePerImage)]
    [InlineData(-5, AppSettings.MinTouchpadSwipeDistancePerImage)]
    [InlineData(29, AppSettings.MinTouchpadSwipeDistancePerImage)]
    [InlineData(2401, AppSettings.MaxTouchpadSwipeDistancePerImage)]
    [InlineData(int.MaxValue, AppSettings.MaxTouchpadSwipeDistancePerImage)]
    public void Normalize_OutOfRangeDistance_IsClampedAndReported(int saved, int expected)
    {
        var settings = new AppSettings { TouchpadSwipeDistancePerImage = saved };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(expected, settings.TouchpadSwipeDistancePerImage);
        Assert.Contains(nameof(AppSettings.TouchpadSwipeDistancePerImage), repairs);
    }

    [Theory]
    [InlineData(AppSettings.MinTouchpadSwipeDistancePerImage)]
    [InlineData(AppSettings.MaxTouchpadSwipeDistancePerImage)]
    public void Normalize_DistanceAtTheBounds_IsKeptAndNotReported(int saved)
    {
        var settings = new AppSettings { TouchpadSwipeDistancePerImage = saved };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(saved, settings.TouchpadSwipeDistancePerImage);
        Assert.DoesNotContain(nameof(AppSettings.TouchpadSwipeDistancePerImage), repairs);
    }

    [Fact]
    public void Load_ExistingActionAlreadyOnR_DisablesRefreshInsteadOfFailingValidation()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """
            { "ConfigVersion": 3, "Actions": [ { "Name": "Reject", "Shortcut": "R", "Operation": "Move", "Destination": "Rejects" } ] }
            """);

        var loaded = _store.Load();

        Assert.Equal("", loaded.Shortcuts.Refresh);
        Assert.Equal("R", loaded.Actions[0].Shortcut); // the user's own binding wins
        Assert.Null(new SettingsValidator(new AlwaysValidKeyNameValidator()).ValidateShortcuts(loaded));
    }

    [Fact]
    public void ValidateShortcuts_RefreshOnTheBackupActionsF5_IsADuplicate()
    {
        var settings = new AppSettings();
        settings.Shortcuts.Refresh = "F5";

        Assert.NotNull(new SettingsValidator(new AlwaysValidKeyNameValidator()).ValidateShortcuts(settings));
    }

    [Fact]
    public void ValidateShortcuts_RefreshOnF5WithoutAnActionThere_IsAllowed()
    {
        var settings = new AppSettings();
        settings.Actions.RemoveAll(a => a.Shortcut == "F5");
        settings.Shortcuts.Refresh = "F5";

        Assert.Null(new SettingsValidator(new AlwaysValidKeyNameValidator()).ValidateShortcuts(settings));
    }

    [Fact]
    public void ValidateShortcuts_EmptyRefresh_IsAllowed()
    {
        var settings = new AppSettings();
        settings.Shortcuts.Refresh = "";

        Assert.Null(new SettingsValidator(new AlwaysValidKeyNameValidator()).ValidateShortcuts(settings));
    }

    [Fact]
    public void Normalize_NullRefreshShortcut_BecomesEmpty()
    {
        var settings = new AppSettings();
        settings.Shortcuts.Refresh = null!;

        SettingsNormalizer.Normalize(settings);

        Assert.Equal("", settings.Shortcuts.Refresh);
    }

    private sealed class AlwaysValidKeyNameValidator : PhotoReview.Core.Abstractions.IKeyNameValidator
    {
        public bool IsValidKeyName(string name) => !string.IsNullOrWhiteSpace(name);
    }
}
