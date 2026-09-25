using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>feat/mouse-zoom: wheel action, click-to-zoom and kinetic pan settings load, clamp and round-trip.</summary>
[Trait("Category", "HotPath")]
public sealed class MouseSettingsTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");
    private readonly SettingsStore _store;

    public MouseSettingsTests()
    {
        _store = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { });
    }

    [Fact]
    public void Defaults_KeepTodaysWheelAndKinetic_ClickZoomOff()
    {
        var settings = new AppSettings();

        Assert.Equal(MouseWheelAction.Zoom, settings.MouseWheelAction);
        Assert.Equal(0, (int)MouseWheelAction.Zoom);
        // Off by default: the click-to-zoom gesture must be opted into.
        Assert.False(settings.ClickToZoomEnabled);
        Assert.Equal(100, settings.ClickZoomPercent);
        Assert.True(settings.KineticPanEnabled);
        Assert.Equal(10, AppSettings.MinClickZoomPercent);
        Assert.Equal(800, AppSettings.MaxClickZoomPercent);
    }

    [Fact]
    public void Load_OldConfigWithoutMouseSettings_UsesDefaultsAndReportsNoRepair()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """{ "ConfigVersion": 3, "PreloadWorkerCount": 8 }""");

        var loaded = _store.Load();

        Assert.Equal(MouseWheelAction.Zoom, loaded.MouseWheelAction);
        Assert.False(loaded.ClickToZoomEnabled);
        Assert.Equal(100, loaded.ClickZoomPercent);
        Assert.True(loaded.KineticPanEnabled);
        Assert.Empty(_store.LastLoadRepairs);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-5, 10)]
    [InlineData(9, 10)]
    [InlineData(801, 800)]
    [InlineData(100000, 800)]
    public void Load_OutOfRangeClickZoom_IsClampedAndReported(int stored, int expected)
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, $$"""{ "ConfigVersion": 3, "ClickZoomPercent": {{stored}} }""");

        var loaded = _store.Load();

        Assert.Equal(expected, loaded.ClickZoomPercent);
        Assert.Contains(nameof(AppSettings.ClickZoomPercent), _store.LastLoadRepairs);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(250)]
    [InlineData(800)]
    public void Normalize_InRangeClickZoom_IsKeptUnreported(int percent)
    {
        var settings = new AppSettings { ClickZoomPercent = percent };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(percent, settings.ClickZoomPercent);
        Assert.Empty(repairs);
    }

    [Fact]
    public void Normalize_UndefinedWheelAction_IsResetToZoomAndReported()
    {
        var settings = new AppSettings { MouseWheelAction = (MouseWheelAction)42 };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(MouseWheelAction.Zoom, settings.MouseWheelAction);
        Assert.Contains(nameof(AppSettings.MouseWheelAction), repairs);
    }

    [Theory]
    [InlineData("\"Navigate\"", MouseWheelAction.Navigate)]
    [InlineData("\"navigate\"", MouseWheelAction.Navigate)]
    [InlineData("1", MouseWheelAction.Navigate)]
    [InlineData("\"Scroll\"", MouseWheelAction.Zoom)] // unknown text -> default
    public void Load_WheelActionText_IsReadLeniently(string json, MouseWheelAction expected)
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, $$"""{ "ConfigVersion": 3, "MouseWheelAction": {{json}} }""");

        Assert.Equal(expected, _store.Load().MouseWheelAction);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllMouseSettings()
    {
        var settings = new AppSettings
        {
            MouseWheelAction = MouseWheelAction.Navigate,
            ClickToZoomEnabled = false,
            ClickZoomPercent = 250,
            KineticPanEnabled = false,
        };

        _store.Save(settings);
        var json = _fileSystem.ReadAllText(_appPaths.ConfigFile);
        var reloaded = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { }).Load();

        Assert.Contains("\"MouseWheelAction\": \"Navigate\"", json, StringComparison.Ordinal);
        Assert.Equal(MouseWheelAction.Navigate, reloaded.MouseWheelAction);
        Assert.False(reloaded.ClickToZoomEnabled);
        Assert.Equal(250, reloaded.ClickZoomPercent);
        Assert.False(reloaded.KineticPanEnabled);
    }
}
