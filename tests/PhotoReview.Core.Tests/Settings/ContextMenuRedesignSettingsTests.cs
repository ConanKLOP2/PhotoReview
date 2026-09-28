using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// PR-C (feat/context-menu-redesign): <see cref="AppSettings.SetZoomAlsoSetsClickLevel"/>,
/// <see cref="AppSettings.ShowFolderMenuItems"/>, <see cref="AppSettings.ShowZoomMenuItems"/> and
/// <see cref="AppSettings.ShowDeleteMenuItem"/> -- defaults, load of an old config missing them, and round-trip.
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
    public void Defaults_ShowZoomMenuItemsIsOff()
    {
        var settings = new AppSettings();

        Assert.False(settings.ShowZoomMenuItems);
    }

    [Fact]
    public void Defaults_ShowDeleteMenuItemIsOff()
    {
        var settings = new AppSettings();

        Assert.False(settings.ShowDeleteMenuItem);
    }

    [Fact]
    public void Load_OldConfigWithoutNewSettings_UsesDefaultsAndReportsNoRepair()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """{ "ConfigVersion": 3, "PreloadWorkerCount": 8 }""");

        var loaded = _store.Load();

        Assert.True(loaded.SetZoomAlsoSetsClickLevel);
        Assert.True(loaded.ShowFolderMenuItems);
        Assert.False(loaded.ShowZoomMenuItems);
        Assert.False(loaded.ShowDeleteMenuItem);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaveThenLoad_RoundTripsShowZoomMenuItems(bool showZoomMenuItems)
    {
        var settings = new AppSettings { ShowZoomMenuItems = showZoomMenuItems };

        _store.Save(settings);
        var reloaded = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { }).Load();

        Assert.Equal(showZoomMenuItems, reloaded.ShowZoomMenuItems);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaveThenLoad_RoundTripsShowDeleteMenuItem(bool showDeleteMenuItem)
    {
        var settings = new AppSettings { ShowDeleteMenuItem = showDeleteMenuItem };

        _store.Save(settings);
        var reloaded = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { }).Load();

        Assert.Equal(showDeleteMenuItem, reloaded.ShowDeleteMenuItem);
    }

    [Fact]
    public void Normalize_DoesNotTouchEitherFlag()
    {
        var settings = new AppSettings { SetZoomAlsoSetsClickLevel = false, ShowFolderMenuItems = false, ShowZoomMenuItems = true, ShowDeleteMenuItem = true };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.False(settings.SetZoomAlsoSetsClickLevel);
        Assert.False(settings.ShowFolderMenuItems);
        Assert.True(settings.ShowZoomMenuItems);
        Assert.True(settings.ShowDeleteMenuItem);
        Assert.DoesNotContain(nameof(AppSettings.SetZoomAlsoSetsClickLevel), repairs);
        Assert.DoesNotContain(nameof(AppSettings.ShowFolderMenuItems), repairs);
        Assert.DoesNotContain(nameof(AppSettings.ShowZoomMenuItems), repairs);
        Assert.DoesNotContain(nameof(AppSettings.ShowDeleteMenuItem), repairs);
    }
}
