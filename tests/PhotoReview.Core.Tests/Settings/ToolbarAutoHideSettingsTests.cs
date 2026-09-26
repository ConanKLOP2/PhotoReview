using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>feat/ui-dark-chrome-toolbar: toolbar auto-hide and info overlay font size settings load, clamp and round-trip.</summary>
[Trait("Category", "HotPath")]
public sealed class ToolbarAutoHideSettingsTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");
    private readonly SettingsStore _store;

    public ToolbarAutoHideSettingsTests()
    {
        _store = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { });
    }

    [Fact]
    public void Defaults_ToolbarAutoHideOnWith1500msDelayAndFontSize12()
    {
        var settings = new AppSettings();

        Assert.True(settings.ToolbarAutoHide);
        Assert.Equal(1500, settings.ToolbarAutoHideDelayMs);
        Assert.Equal(12, settings.InfoOverlayFontSize);
        Assert.Equal(0, AppSettings.MinToolbarAutoHideDelayMs);
        Assert.Equal(10000, AppSettings.MaxToolbarAutoHideDelayMs);
        Assert.Equal(8, AppSettings.MinInfoOverlayFontSize);
        Assert.Equal(24, AppSettings.MaxInfoOverlayFontSize);
    }

    [Fact]
    public void Load_OldConfigWithoutToolbarSettings_UsesDefaultsAndReportsNoRepair()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """{ "ConfigVersion": 3, "PreloadWorkerCount": 8 }""");

        var loaded = _store.Load();

        Assert.True(loaded.ToolbarAutoHide);
        Assert.Equal(1500, loaded.ToolbarAutoHideDelayMs);
        Assert.Equal(12, loaded.InfoOverlayFontSize);
        Assert.Empty(_store.LastLoadRepairs);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(-1, 0)]
    [InlineData(10001, 10000)]
    [InlineData(100000, 10000)]
    public void Load_OutOfRangeToolbarAutoHideDelay_IsClampedAndReported(int stored, int expected)
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $$"""{ "ConfigVersion": 3, "ToolbarAutoHideDelayMs": {{stored}} }"""));

        var loaded = _store.Load();

        Assert.Equal(expected, loaded.ToolbarAutoHideDelayMs);
        Assert.Contains(nameof(AppSettings.ToolbarAutoHideDelayMs), _store.LastLoadRepairs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1500)]
    [InlineData(10000)]
    public void Normalize_InRangeToolbarAutoHideDelay_IsKeptUnreported(int delayMs)
    {
        var settings = new AppSettings { ToolbarAutoHideDelayMs = delayMs };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(delayMs, settings.ToolbarAutoHideDelayMs);
        Assert.Empty(repairs);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(7.9, 8)]
    [InlineData(24.1, 24)]
    [InlineData(100, 24)]
    public void Load_OutOfRangeInfoOverlayFontSize_IsClampedAndReported(double stored, double expected)
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $$"""{ "ConfigVersion": 3, "InfoOverlayFontSize": {{stored}} }"""));

        var loaded = _store.Load();

        Assert.Equal(expected, loaded.InfoOverlayFontSize);
        Assert.Contains(nameof(AppSettings.InfoOverlayFontSize), _store.LastLoadRepairs);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(24)]
    public void Normalize_InRangeInfoOverlayFontSize_IsKeptUnreported(double fontSize)
    {
        var settings = new AppSettings { InfoOverlayFontSize = fontSize };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(fontSize, settings.InfoOverlayFontSize);
        Assert.Empty(repairs);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllToolbarAutoHideSettings()
    {
        var settings = new AppSettings
        {
            ToolbarAutoHide = false,
            ToolbarAutoHideDelayMs = 3000,
            InfoOverlayFontSize = 18,
        };

        _store.Save(settings);
        var json = _fileSystem.ReadAllText(_appPaths.ConfigFile);
        var reloaded = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { }).Load();

        Assert.Contains("\"ToolbarAutoHide\": false", json, StringComparison.Ordinal);
        Assert.False(reloaded.ToolbarAutoHide);
        Assert.Equal(3000, reloaded.ToolbarAutoHideDelayMs);
        Assert.Equal(18, reloaded.InfoOverlayFontSize);
    }
}
