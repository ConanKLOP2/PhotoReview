using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>feat/preload-window-setting: the configurable preload forward/backward window loads, clamps and round-trips.</summary>
[Trait("Category", "HotPath")]
public sealed class PreloadWindowSettingsTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");
    private readonly SettingsStore _store;

    public PreloadWindowSettingsTests()
    {
        _store = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { });
    }

    [Fact]
    public void Defaults_Are32Forward8Backward()
    {
        var settings = new AppSettings();

        Assert.Equal(32, settings.PreloadForwardCount);
        Assert.Equal(8, settings.PreloadBackwardCount);
        Assert.Equal(32, PerformanceOptions.PreloadForwardCount);
        Assert.Equal(8, PerformanceOptions.PreloadBackwardCount);
        Assert.Equal(1, PerformanceOptions.MinPreloadForwardCount);
        Assert.Equal(0, PerformanceOptions.MinPreloadBackwardCount);
        Assert.Equal(500, PerformanceOptions.MaxPreloadCount);
    }

    [Fact]
    public void Load_OldConfigWithoutPreloadWindow_UsesDefaultsAndReportsNoRepair()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """
        { "ConfigVersion": 3, "PreloadWorkerCount": 8 }
        """);

        var loaded = _store.Load();

        Assert.Equal(PerformanceOptions.PreloadForwardCount, loaded.PreloadForwardCount);
        Assert.Equal(PerformanceOptions.PreloadBackwardCount, loaded.PreloadBackwardCount);
        Assert.DoesNotContain(nameof(AppSettings.PreloadForwardCount), _store.LastLoadRepairs);
        Assert.DoesNotContain(nameof(AppSettings.PreloadBackwardCount), _store.LastLoadRepairs);
    }

    [Theory]
    [InlineData(0, 1)]     // below minimum
    [InlineData(-5, 1)]
    [InlineData(10_000, 500)] // above the memory-bomb guard
    public void Normalize_OutOfRangeForward_IsClampedAndReported(int stored, int expected)
    {
        var settings = new AppSettings { PreloadForwardCount = stored };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(expected, settings.PreloadForwardCount);
        Assert.Contains(nameof(AppSettings.PreloadForwardCount), repairs);
    }

    [Theory]
    [InlineData(-1, 0)]    // below minimum (0 is valid: no backward preload)
    [InlineData(10_000, 500)]
    public void Normalize_OutOfRangeBackward_IsClampedAndReported(int stored, int expected)
    {
        var settings = new AppSettings { PreloadBackwardCount = stored };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(expected, settings.PreloadBackwardCount);
        Assert.Contains(nameof(AppSettings.PreloadBackwardCount), repairs);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(500)]
    public void Normalize_InRangeForward_IsKeptUnreported(int forward)
    {
        var settings = new AppSettings { PreloadForwardCount = forward };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(forward, settings.PreloadForwardCount);
        Assert.DoesNotContain(nameof(AppSettings.PreloadForwardCount), repairs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(500)]
    public void Normalize_InRangeBackward_IsKeptUnreported(int backward)
    {
        var settings = new AppSettings { PreloadBackwardCount = backward };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(backward, settings.PreloadBackwardCount);
        Assert.DoesNotContain(nameof(AppSettings.PreloadBackwardCount), repairs);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsPreloadWindow()
    {
        var settings = new AppSettings { PreloadForwardCount = 64, PreloadBackwardCount = 16 };

        _store.Save(settings);
        var reloaded = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { }).Load();

        Assert.Equal(64, reloaded.PreloadForwardCount);
        Assert.Equal(16, reloaded.PreloadBackwardCount);
    }
}
