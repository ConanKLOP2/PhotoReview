using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>Photo information line settings: defaults, older configs, round-trip and lenient reading.</summary>
[Trait("Category", "HotPath")]
public sealed class ExifInfoSettingsTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");
    private readonly SettingsStore _store;

    public ExifInfoSettingsTests()
    {
        _store = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { });
    }

    private AppSettings LoadJson(string json)
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, json);
        return new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { }).Load();
    }

    [Fact]
    public void Defaults_LineOffAndFieldsExcludeFileNameAndDimensions()
    {
        var settings = new AppSettings();

        Assert.False(settings.ShowExifInfo);
        // FileName and Dimensions are excluded: the status line above already shows the file name and W×H.
        Assert.Equal(ExifInfoFields.Default, settings.ExifInfoFields);
        Assert.Equal(
            ExifInfoFields.DateTaken | ExifInfoFields.Camera | ExifInfoFields.Lens | ExifInfoFields.Iso
                | ExifInfoFields.FocalLength | ExifInfoFields.Aperture | ExifInfoFields.ShutterSpeed,
            ExifInfoFields.Default);
        Assert.False(ExifInfoFields.Default.HasFlag(ExifInfoFields.FileName));
        Assert.False(ExifInfoFields.Default.HasFlag(ExifInfoFields.Dimensions));
    }

    [Fact]
    public void Load_OldConfigWithoutTheSettings_UsesDefaults()
    {
        var loaded = LoadJson("""{ "ConfigVersion": 3, "CompareHashEnabled": false }""");

        Assert.False(loaded.CompareHashEnabled);
        Assert.False(loaded.ShowExifInfo);
        Assert.Equal(ExifInfoFields.Default, loaded.ExifInfoFields);
    }

    [Theory]
    [InlineData(true, ExifInfoFields.All)]
    [InlineData(false, ExifInfoFields.None)]
    [InlineData(true, ExifInfoFields.FileName | ExifInfoFields.Iso | ExifInfoFields.ShutterSpeed)]
    [InlineData(true, ExifInfoFields.FocalLength | ExifInfoFields.Aperture)]
    [InlineData(true, ExifInfoFields.All & ~ExifInfoFields.Aperture)]
    [InlineData(false, ExifInfoFields.Camera | ExifInfoFields.Lens | ExifInfoFields.DateTaken)]
    public void SaveThenLoad_RoundTrips(bool show, ExifInfoFields fields)
    {
        _store.Save(new AppSettings { ShowExifInfo = show, ExifInfoFields = fields });
        var reloaded = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { }).Load();

        Assert.Equal(show, reloaded.ShowExifInfo);
        Assert.Equal(fields, reloaded.ExifInfoFields);
    }

    [Fact]
    public void Save_WritesReadableFieldNames()
    {
        _store.Save(new AppSettings { ExifInfoFields = ExifInfoFields.FileName | ExifInfoFields.Lens });

        var json = _fileSystem.ReadAllText(_appPaths.ConfigFile);

        Assert.Contains("\"ExifInfoFields\": \"FileName, Lens\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"camera, LENS\"", ExifInfoFields.Camera | ExifInfoFields.Lens)]
    [InlineData("12", ExifInfoFields.Dimensions | ExifInfoFields.Camera)]
    [InlineData("4095", ExifInfoFields.All)]                // unknown bits dropped, real bitmask value kept as is
    [InlineData("\"iso, ShutterSpeed\"", ExifInfoFields.Iso | ExifInfoFields.ShutterSpeed)]
    [InlineData("\"Exposure\"", ExifInfoFields.Default)]     // never-shipped grouped name: unknown = default
    [InlineData("\"Shutter\"", ExifInfoFields.Default)]      // unknown name: default, not a failed load
    [InlineData("\"Aperture\"", ExifInfoFields.Aperture)]
    [InlineData("[1, 2]", ExifInfoFields.Default)]
    [InlineData("null", ExifInfoFields.Default)]
    [InlineData("\"None\"", ExifInfoFields.None)]
    public void Load_LenientFieldValues(string jsonValue, ExifInfoFields expected)
    {
        var loaded = LoadJson($$"""{ "ConfigVersion": 3, "ExifInfoFields": {{jsonValue}}, "CompareSizeEnabled": false }""");

        Assert.Equal(expected, loaded.ExifInfoFields);
        Assert.False(loaded.CompareSizeEnabled); // the rest of the config still loaded
    }
}
