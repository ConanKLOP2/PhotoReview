using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// PR-B (feat/fit-width-height-keep-zoom): InitialViewMode.FitWidth/FitHeight/ClickZoomLevel, the Percent400 ->
/// Percent200 migration, FitWidthAnchor, KeepZoomAcrossImages and the new shortcuts (W/H/K).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class FitWidthHeightKeepZoomSettingsTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");
    private readonly SettingsStore _store;

    public FitWidthHeightKeepZoomSettingsTests()
    {
        _store = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { });
    }

    [Fact]
    public void Defaults_KeepZoomOff_FitWidthAnchorCentre_NewShortcutsAreWHK()
    {
        var settings = new AppSettings();

        Assert.False(settings.KeepZoomAcrossImages);
        Assert.Equal(FitWidthAnchor.Centre, settings.FitWidthAnchor);
        Assert.Equal(0, (int)FitWidthAnchor.Centre); // default value is the enum's zero member
        Assert.Equal("W", settings.Shortcuts.FitWidth);
        Assert.Equal("H", settings.Shortcuts.FitHeight);
        Assert.Equal("K", settings.Shortcuts.ToggleKeepZoom);
    }

    [Fact]
    public void Load_OldConfigWithoutNewSettings_UsesDefaultsAndReportsNoRepair()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """{ "ConfigVersion": 3, "PreloadWorkerCount": 8 }""");

        var loaded = _store.Load();

        Assert.False(loaded.KeepZoomAcrossImages);
        Assert.Equal(FitWidthAnchor.Centre, loaded.FitWidthAnchor);
        Assert.Equal("W", loaded.Shortcuts.FitWidth);
        Assert.Equal("H", loaded.Shortcuts.FitHeight);
        Assert.Equal("K", loaded.Shortcuts.ToggleKeepZoom);
        Assert.Empty(_store.LastLoadRepairs);
    }

    [Fact]
    public void Normalize_Percent400_IsMigratedToPercent200AndReported()
    {
        var settings = new AppSettings { InitialViewMode = InitialViewMode.Percent400 };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(InitialViewMode.Percent200, settings.InitialViewMode);
        Assert.Contains(nameof(AppSettings.InitialViewMode), repairs);
    }

    [Theory]
    [InlineData(InitialViewMode.Fit)]
    [InlineData(InitialViewMode.FitWidth)]
    [InlineData(InitialViewMode.FitHeight)]
    [InlineData(InitialViewMode.ClickZoomLevel)]
    [InlineData(InitialViewMode.Percent100)]
    [InlineData(InitialViewMode.Percent200)]
    public void Normalize_SupportedInitialViewModes_AreKeptUnreported(InitialViewMode mode)
    {
        var settings = new AppSettings { InitialViewMode = mode };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(mode, settings.InitialViewMode);
        Assert.DoesNotContain(nameof(AppSettings.InitialViewMode), repairs);
    }

    [Fact]
    public void Normalize_UndefinedInitialViewMode_IsResetToFitAndReported()
    {
        var settings = new AppSettings { InitialViewMode = (InitialViewMode)99 };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(InitialViewMode.Fit, settings.InitialViewMode);
        Assert.Contains(nameof(AppSettings.InitialViewMode), repairs);
    }

    [Fact]
    public void Normalize_UndefinedFitWidthAnchor_IsResetToCentreAndReported()
    {
        var settings = new AppSettings { FitWidthAnchor = (FitWidthAnchor)77 };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(FitWidthAnchor.Centre, settings.FitWidthAnchor);
        Assert.Contains(nameof(AppSettings.FitWidthAnchor), repairs);
    }

    [Theory]
    [InlineData("\"fit-width\"", InitialViewMode.FitWidth)]
    [InlineData("\"fit-height\"", InitialViewMode.FitHeight)]
    [InlineData("\"click-zoom\"", InitialViewMode.ClickZoomLevel)]
    [InlineData("\"400%\"", InitialViewMode.Percent400)] // legacy alias still parses; normalizer migrates it afterwards
    public void Load_InitialViewModeAliases_AreReadLeniently(string json, InitialViewMode expectedBeforeNormalize)
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, $$"""{ "ConfigVersion": 3, "InitialViewMode": {{json}} }""");

        var loaded = _store.Load();

        var expected = expectedBeforeNormalize == InitialViewMode.Percent400 ? InitialViewMode.Percent200 : expectedBeforeNormalize;
        Assert.Equal(expected, loaded.InitialViewMode);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsFitWidthHeightAndKeepZoomSettings()
    {
        var settings = new AppSettings
        {
            InitialViewMode = InitialViewMode.FitWidth,
            FitWidthAnchor = FitWidthAnchor.TopThird,
            KeepZoomAcrossImages = true,
        };
        settings.Shortcuts.FitWidth = "W";
        settings.Shortcuts.FitHeight = "H";
        settings.Shortcuts.ToggleKeepZoom = "K";

        _store.Save(settings);
        var json = _fileSystem.ReadAllText(_appPaths.ConfigFile);
        var reloaded = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { }).Load();

        // Unlike InitialViewMode's legacy Fit/100%/200%/400% strings, the new values write as their plain (PascalCase)
        // enum name; "fit-width" etc. are read-only JsonAlias spellings kept for forward-compat / hand-edited configs.
        Assert.Contains("\"InitialViewMode\": \"FitWidth\"", json, StringComparison.Ordinal);
        Assert.Equal(InitialViewMode.FitWidth, reloaded.InitialViewMode);
        Assert.Equal(FitWidthAnchor.TopThird, reloaded.FitWidthAnchor);
        Assert.True(reloaded.KeepZoomAcrossImages);
        Assert.Equal("W", reloaded.Shortcuts.FitWidth);
        Assert.Equal("H", reloaded.Shortcuts.FitHeight);
        Assert.Equal("K", reloaded.Shortcuts.ToggleKeepZoom);
    }

    [Fact]
    public void Normalize_BlankOptionalShortcuts_AreTrimmedToEmptyNotReset()
    {
        var settings = new AppSettings();
        settings.Shortcuts.FitWidth = "   ";
        settings.Shortcuts.FitHeight = null!;
        settings.Shortcuts.ToggleKeepZoom = " K ";

        SettingsNormalizer.Normalize(settings);

        Assert.Equal("", settings.Shortcuts.FitWidth);
        Assert.Equal("", settings.Shortcuts.FitHeight);
        Assert.Equal("K", settings.Shortcuts.ToggleKeepZoom);
    }

    [Fact]
    public void DisableConflictingOptionalShortcuts_NewShortcutCollidingWithAMandatoryOne_IsDisabled()
    {
        var settings = new AppSettings();
        settings.Shortcuts.FitWidth = "Z"; // collides with the mandatory Undo shortcut ("Z")

        var disabled = SettingsNormalizer.DisableConflictingOptionalShortcuts(settings);

        Assert.Contains(nameof(ShortcutMappings.FitWidth), disabled);
        Assert.Equal("", settings.Shortcuts.FitWidth);
    }

    [Fact]
    public void ValidateShortcuts_DefaultSettings_HaveNoDuplicatesAmongThemselvesOrTheActionDefaults()
    {
        var settings = new AppSettings(); // Shortcuts.Default() + ReviewAction.Defaults() (Enter/F3/F4/F5)
        var error = new SettingsValidator(new AlwaysValidKeyNameValidator()).ValidateShortcuts(settings);

        Assert.Null(error);
    }

    private sealed class AlwaysValidKeyNameValidator : PhotoReview.Core.Abstractions.IKeyNameValidator
    {
        public bool IsValidKeyName(string name) => !string.IsNullOrWhiteSpace(name);
    }
}
