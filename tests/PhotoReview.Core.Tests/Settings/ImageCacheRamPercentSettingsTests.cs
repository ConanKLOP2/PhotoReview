using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>RAM%: the in-memory cache share setting loads, clamps and round-trips.</summary>
[Trait("Category", "HotPath")]
public sealed class ImageCacheRamPercentSettingsTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");
    private readonly SettingsStore _store;

    public ImageCacheRamPercentSettingsTests()
    {
        _store = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { });
    }

    [Fact]
    public void Defaults_AreFiftyPercentWithinOneToNinety()
    {
        Assert.Equal(50, new AppSettings().ImageCacheRamPercent);
        Assert.Equal(90, PerformanceOptions.MaxImageCacheRamPercent);
        Assert.Equal(1, PerformanceOptions.MinImageCacheRamPercent);
    }

    [Fact]
    public void Load_OldConfigWithoutPercent_UsesDefaultAndReportsNoRepair()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """
        { "ConfigVersion": 3, "ImageCacheCapacityBytes": 17179869184, "PreloadWorkerCount": 8 }
        """);

        var loaded = _store.Load();

        Assert.Equal(PerformanceOptions.ImageCacheRamPercent, loaded.ImageCacheRamPercent);
        Assert.Equal(17179869184L, loaded.ImageCacheCapacityBytes);
        Assert.DoesNotContain(nameof(AppSettings.ImageCacheRamPercent), _store.LastLoadRepairs);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-20, 1)]
    [InlineData(91, 90)]
    [InlineData(500, 90)]
    public void Load_OutOfRangePercent_IsClampedToNearestBoundAndReported(int stored, int expected)
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, $$"""{ "ConfigVersion": 3, "ImageCacheRamPercent": {{stored}} }""");

        var loaded = _store.Load();

        Assert.Equal(expected, loaded.ImageCacheRamPercent);
        Assert.Contains(nameof(AppSettings.ImageCacheRamPercent), _store.LastLoadRepairs);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(90)]
    public void Normalize_InRangePercent_IsKeptUnreported(int percent)
    {
        var settings = new AppSettings { ImageCacheRamPercent = percent };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(percent, settings.ImageCacheRamPercent);
        Assert.DoesNotContain(nameof(AppSettings.ImageCacheRamPercent), repairs);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsPercent()
    {
        var settings = new AppSettings { ImageCacheRamPercent = 73 };

        _store.Save(settings);
        var reloaded = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { }).Load();

        Assert.Equal(73, reloaded.ImageCacheRamPercent);
    }
}
