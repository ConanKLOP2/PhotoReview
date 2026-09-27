using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// feat/image-crossfade: <see cref="AppSettings.ImageTransition"/> / <see cref="AppSettings.ImageTransitionMs"/>
/// defaults, lenient JSON aliases and normalizer clamping.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ImageTransitionSettingsTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");
    private readonly SettingsStore _store;

    public ImageTransitionSettingsTests()
    {
        _store = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { });
    }

    [Fact]
    public void Defaults_AreNoneAnd120Ms()
    {
        var settings = new AppSettings();

        Assert.Equal(ImageTransition.None, settings.ImageTransition);
        Assert.Equal(0, (int)ImageTransition.None);
        Assert.Equal(1, (int)ImageTransition.Fade);
        Assert.Equal(120, settings.ImageTransitionMs);
        Assert.Equal(40, AppSettings.MinImageTransitionMs);
        Assert.Equal(400, AppSettings.MaxImageTransitionMs);
    }

    [Fact]
    public void Load_OldConfigWithoutImageTransition_UsesDefaultsAndReportsNoRepair()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """{ "ConfigVersion": 3, "PreloadWorkerCount": 8 }""");

        var loaded = _store.Load();

        Assert.Equal(ImageTransition.None, loaded.ImageTransition);
        Assert.Equal(120, loaded.ImageTransitionMs);
        Assert.Empty(_store.LastLoadRepairs);
    }

    [Theory]
    [InlineData("\"Fade\"", ImageTransition.Fade)]
    [InlineData("\"fade\"", ImageTransition.Fade)]
    [InlineData("1", ImageTransition.Fade)]
    [InlineData("\"None\"", ImageTransition.None)]
    [InlineData("0", ImageTransition.None)]
    [InlineData("\"Dissolve\"", ImageTransition.None)] // unknown text -> default
    public void Load_ImageTransitionText_IsReadLeniently(string json, ImageTransition expected)
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, $$"""{ "ConfigVersion": 3, "ImageTransition": {{json}} }""");

        Assert.Equal(expected, _store.Load().ImageTransition);
    }

    [Fact]
    public void Normalize_UndefinedImageTransition_IsResetToNoneAndReported()
    {
        var settings = new AppSettings { ImageTransition = (ImageTransition)42 };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(ImageTransition.None, settings.ImageTransition);
        Assert.Contains(nameof(AppSettings.ImageTransition), repairs);
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(-100, 40)]
    [InlineData(39, 40)]
    [InlineData(401, 400)]
    [InlineData(100000, 400)]
    public void Load_OutOfRangeImageTransitionMs_IsClampedAndReported(int stored, int expected)
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, string.Create(System.Globalization.CultureInfo.InvariantCulture, $$"""{ "ConfigVersion": 3, "ImageTransitionMs": {{stored}} }"""));

        var loaded = _store.Load();

        Assert.Equal(expected, loaded.ImageTransitionMs);
        Assert.Contains(nameof(AppSettings.ImageTransitionMs), _store.LastLoadRepairs);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    [InlineData(400)]
    public void Normalize_InRangeImageTransitionMs_IsKeptUnreported(int ms)
    {
        var settings = new AppSettings { ImageTransitionMs = ms };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(ms, settings.ImageTransitionMs);
        Assert.Empty(repairs);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsImageTransitionSettings()
    {
        var settings = new AppSettings { ImageTransition = ImageTransition.Fade, ImageTransitionMs = 250 };

        _store.Save(settings);
        var json = _fileSystem.ReadAllText(_appPaths.ConfigFile);
        var reloaded = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { }).Load();

        Assert.Contains("\"ImageTransition\": \"Fade\"", json, StringComparison.Ordinal);
        Assert.Equal(ImageTransition.Fade, reloaded.ImageTransition);
        Assert.Equal(250, reloaded.ImageTransitionMs);
    }
}
