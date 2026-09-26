using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>Title bar field settings: defaults, older configs, round-trip and lenient reading.</summary>
[Trait("Category", "HotPath")]
public sealed class TitleBarFieldsSettingsTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");
    private readonly SettingsStore _store;

    public TitleBarFieldsSettingsTests()
    {
        _store = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { });
    }

    private AppSettings LoadJson(string json)
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, json);
        return new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { }).Load();
    }

    [Fact]
    public void Default_IsFolderNameOnly()
    {
        var settings = new AppSettings();

        Assert.Equal(TitleBarFields.FolderName, settings.TitleBarFields);
        Assert.True(TitleBarFields.Default.HasFlag(TitleBarFields.FolderName));
        Assert.False(TitleBarFields.Default.HasFlag(TitleBarFields.FolderPath));
        Assert.False(TitleBarFields.Default.HasFlag(TitleBarFields.FileName));
    }

    [Fact]
    public void Load_OldConfigWithoutTheSetting_UsesDefault()
    {
        var loaded = LoadJson("""{ "ConfigVersion": 3, "CompareHashEnabled": false }""");

        Assert.False(loaded.CompareHashEnabled);
        Assert.Equal(TitleBarFields.Default, loaded.TitleBarFields);
    }

    [Theory]
    [InlineData(TitleBarFields.All)]
    [InlineData(TitleBarFields.None)]
    [InlineData(TitleBarFields.FolderPath | TitleBarFields.FileName | TitleBarFields.FileSize)]
    [InlineData(TitleBarFields.IndexCount | TitleBarFields.DateTaken)]
    [InlineData(TitleBarFields.All & ~TitleBarFields.ShutterSpeed)]
    public void SaveThenLoad_RoundTrips(TitleBarFields fields)
    {
        _store.Save(new AppSettings { TitleBarFields = fields });
        var reloaded = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { }).Load();

        Assert.Equal(fields, reloaded.TitleBarFields);
    }

    [Fact]
    public void Save_WritesReadableFieldNames()
    {
        _store.Save(new AppSettings { TitleBarFields = TitleBarFields.FolderName | TitleBarFields.FileName });

        var json = _fileSystem.ReadAllText(_appPaths.ConfigFile);

        Assert.Contains("\"TitleBarFields\": \"FolderName, FileName\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"folderpath, FILENAME\"", TitleBarFields.FolderPath | TitleBarFields.FileName)]
    [InlineData("3", TitleBarFields.FolderName | TitleBarFields.FolderPath)]
    [InlineData("\"Nonsense\"", TitleBarFields.Default)] // unknown name: default, not a failed load
    [InlineData("[1, 2]", TitleBarFields.Default)]
    [InlineData("null", TitleBarFields.Default)]
    [InlineData("\"None\"", TitleBarFields.None)]
    public void Load_LenientFieldValues(string jsonValue, TitleBarFields expected)
    {
        var loaded = LoadJson($$"""{ "ConfigVersion": 3, "TitleBarFields": {{jsonValue}}, "CompareSizeEnabled": false }""");

        Assert.Equal(expected, loaded.TitleBarFields);
        Assert.False(loaded.CompareSizeEnabled); // the rest of the config still loaded
    }
}
