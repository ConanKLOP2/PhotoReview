using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>R05: a non-finite font size must be repaired to the default (not survive Math.Clamp and then fail repair persistence), and
/// <see cref="AppSettings.KeyboardZoomStepPercent"/> is clamped to its documented range.</summary>
public sealed class NonFiniteAndZoomStepSettingsTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");

    private SettingsStore NewStore() => new(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { });

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void Load_NonFiniteInfoOverlayFontSize_IsResetToDefaultAndPersistedWithoutThrowing(string literal)
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, $$"""{ "ConfigVersion": 3, "InfoOverlayFontSize": "{{literal}}" }""");
        var store = NewStore();

        var loaded = store.Load();

        Assert.Equal(AppSettings.DefaultInfoOverlayFontSize, loaded.InfoOverlayFontSize);
        Assert.Contains(nameof(AppSettings.InfoOverlayFontSize), store.LastLoadRepairs);
        // The repaired value was written back, so a second load is clean.
        var second = NewStore();
        Assert.Equal(AppSettings.DefaultInfoOverlayFontSize, second.Load().InfoOverlayFontSize);
        Assert.Empty(second.LastLoadRepairs);
    }

    [Fact]
    public void Normalize_NaNInfoOverlayFontSize_ResetsToDefaultAndReports()
    {
        var settings = new AppSettings { InfoOverlayFontSize = double.NaN };

        var repaired = SettingsNormalizer.Normalize(settings);

        Assert.Equal(AppSettings.DefaultInfoOverlayFontSize, settings.InfoOverlayFontSize);
        Assert.Contains(nameof(AppSettings.InfoOverlayFontSize), repaired);
    }

    [Theory]
    [InlineData(int.MinValue, 5)]
    [InlineData(-10, 5)]
    [InlineData(0, 5)]
    [InlineData(4, 5)]
    [InlineData(101, 100)]
    [InlineData(int.MaxValue, 100)]
    public void Load_OutOfRangeKeyboardZoomStep_IsClampedAndReported(int stored, int expected)
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $$"""{ "ConfigVersion": 3, "KeyboardZoomStepPercent": {{stored}} }"""));
        var store = NewStore();

        var loaded = store.Load();

        Assert.Equal(expected, loaded.KeyboardZoomStepPercent);
        Assert.Contains(nameof(AppSettings.KeyboardZoomStepPercent), store.LastLoadRepairs);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(100)]
    public void Normalize_InRangeKeyboardZoomStep_IsKeptUnreported(int percent)
    {
        var settings = new AppSettings { KeyboardZoomStepPercent = percent };

        var repaired = SettingsNormalizer.Normalize(settings);

        Assert.Equal(percent, settings.KeyboardZoomStepPercent);
        Assert.DoesNotContain(nameof(AppSettings.KeyboardZoomStepPercent), repaired);
    }
}
