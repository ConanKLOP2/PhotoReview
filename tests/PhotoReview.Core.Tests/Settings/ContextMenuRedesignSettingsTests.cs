using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// PR-C (feat/context-menu-redesign): <see cref="AppSettings.SetZoomAlsoSetsClickLevel"/> and
/// <see cref="AppSettings.ShowFolderMenuItems"/> -- defaults, load of an old config missing them, and round-trip.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ContextMenuRedesignSettingsTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");
    private readonly SettingsStore _store;

    public ContextMenuRedesignSettingsTests()
    {
        _store = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { });
    }

    [Fact]
    public void Defaults_BothOn()
    {
        var settings = new AppSettings();

        Assert.True(settings.SetZoomAlsoSetsClickLevel);
        Assert.True(settings.ShowFolderMenuItems);
    }

    [Fact]
    public void Load_OldConfigWithoutNewSettings_UsesDefaultsAndReportsNoRepair()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """{ "ConfigVersion": 3, "PreloadWorkerCount": 8 }""");

        var loaded = _store.Load();

        Assert.True(loaded.SetZoomAlsoSetsClickLevel);
        Assert.True(loaded.ShowFolderMenuItems);
        Assert.Empty(_store.LastLoadRepairs);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void SaveThenLoad_RoundTripsBothSettings(bool setZoomAlsoSetsClickLevel, bool showFolderMenuItems)
    {
        var settings = new AppSettings
        {
            SetZoomAlsoSetsClickLevel = setZoomAlsoSetsClickLevel,
            ShowFolderMenuItems = showFolderMenuItems,
        };

        _store.Save(settings);
        var reloaded = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { }).Load();

        Assert.Equal(setZoomAlsoSetsClickLevel, reloaded.SetZoomAlsoSetsClickLevel);
        Assert.Equal(showFolderMenuItems, reloaded.ShowFolderMenuItems);
    }

    [Fact]
    public void Normalize_DoesNotTouchEitherFlag()
    {
        var settings = new AppSettings { SetZoomAlsoSetsClickLevel = false, ShowFolderMenuItems = false };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.False(settings.SetZoomAlsoSetsClickLevel);
        Assert.False(settings.ShowFolderMenuItems);
        Assert.DoesNotContain(nameof(AppSettings.SetZoomAlsoSetsClickLevel), repairs);
        Assert.DoesNotContain(nameof(AppSettings.ShowFolderMenuItems), repairs);
    }
}
