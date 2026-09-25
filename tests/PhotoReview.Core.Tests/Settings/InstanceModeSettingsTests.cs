using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>Q-R18: the instance-mode setting defaults to SingleWindow, round-trips and survives old or broken configs.</summary>
[Trait("Category", "HotPath")]
public sealed class InstanceModeSettingsTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");

    private SettingsStore NewStore() => new(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { });

    [Fact]
    public void Default_IsSingleWindow()
    {
        Assert.Equal(InstanceMode.SingleWindow, new AppSettings().InstanceMode);
        Assert.Equal(0, (int)InstanceMode.SingleWindow);
    }

    [Fact]
    public void Load_OldConfigWithoutInstanceMode_IsSingleWindowWithoutRepair()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """{ "ConfigVersion": 3, "LoadingMode": "Preview" }""");
        var store = NewStore();

        var loaded = store.Load();

        Assert.Equal(InstanceMode.SingleWindow, loaded.InstanceMode);
        Assert.DoesNotContain(nameof(AppSettings.InstanceMode), store.LastLoadRepairs);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsPerFolder()
    {
        NewStore().Save(new AppSettings { InstanceMode = InstanceMode.PerFolder });

        var reloaded = NewStore().Load();

        Assert.Equal(InstanceMode.PerFolder, reloaded.InstanceMode);
    }

    [Theory]
    [InlineData("\"perfolder\"", InstanceMode.PerFolder)]
    [InlineData("1", InstanceMode.PerFolder)]
    [InlineData("\"nonsense\"", InstanceMode.SingleWindow)]
    [InlineData("7", InstanceMode.SingleWindow)]
    public void Load_StoredValue_IsReadLeniently(string json, InstanceMode expected)
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, $$"""{ "ConfigVersion": 3, "InstanceMode": {{json}} }""");

        Assert.Equal(expected, NewStore().Load().InstanceMode);
    }

    [Fact]
    public void Normalize_UndefinedValue_IsRepairedToSingleWindowAndReported()
    {
        var settings = new AppSettings { InstanceMode = (InstanceMode)42 };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(InstanceMode.SingleWindow, settings.InstanceMode);
        Assert.Contains(nameof(AppSettings.InstanceMode), repairs);
    }
}
