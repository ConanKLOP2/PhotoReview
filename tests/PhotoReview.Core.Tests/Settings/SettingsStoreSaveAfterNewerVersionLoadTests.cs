using System.Text.Json;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// Audit C-04, characterization tests (they pin what the code does TODAY, they do not endorse it). A config.json written by a
/// NEWER build (ConfigVersion greater than <see cref="AppSettings.CurrentConfigVersion"/>) is left untouched by
/// <see cref="SettingsStore.Load"/>, but the first <see cref="SettingsStore.Save"/> in this older build rewrites the whole file
/// as the current version: unknown fields are dropped and NO backup is made.
/// Known limitation: the user silently loses the newer build's fields (and the version stamp) after any Save; decision pending
/// with the project owner (see the PR description). When this behaviour is deliberately changed, update these tests with it.
/// </summary>
public sealed class SettingsStoreSaveAfterNewerVersionLoadTests
{
    private const string Newer = """{"ConfigVersion":99,"FutureField":123,"UiLanguage":"en","ClickZoomPercent":250}""";

    private readonly InMemoryFileSystem _fs = new();
    private readonly AppPaths _paths = new(@"C:\Users\test\AppData\Local");

    private SettingsStore NewStore(Action<string, Exception>? onStartupError = null) => new(_paths, _fs, NullLog.Instance, onStartupError);

    private List<string> Siblings() =>
        _fs.EnumerateFiles(Path.GetDirectoryName(_paths.ConfigFile)!, "*").Where(f => f != _paths.ConfigFile).ToList();

    [Fact(DisplayName = "C-04 known limitation: Save after loading a newer-build config rewrites it as the current version and drops unknown fields")]
    public void Save_AfterLoadingNewerVersion_WritesCurrentVersionWithoutUnknownFields()
    {
        _fs.AddFile(_paths.ConfigFile, Newer);
        var store = NewStore();
        var loaded = store.Load();
        Assert.Equal(99, loaded.ConfigVersion); // Load keeps the newer stamp in memory...
        Assert.Equal(Newer, _fs.ReadAllText(_paths.ConfigFile)); // ...and does not touch the file

        loaded.ClickZoomPercent = 300;
        store.Save(loaded);

        var written = _fs.ReadAllText(_paths.ConfigFile);
        using var doc = JsonDocument.Parse(written);
        Assert.Equal(AppSettings.CurrentConfigVersion, doc.RootElement.GetProperty("ConfigVersion").GetInt32());
        Assert.True(AppSettings.CurrentConfigVersion < 99);
        Assert.False(doc.RootElement.TryGetProperty("FutureField", out _)); // unknown field LOST
        Assert.Equal(300, doc.RootElement.GetProperty("ClickZoomPercent").GetInt32()); // known fields keep the user's value
        Assert.Equal(AppSettings.CurrentConfigVersion, store.Current.ConfigVersion);
    }

    [Fact(DisplayName = "C-04 known limitation: that overwrite makes no backup of the newer file and raises no warning")]
    public void Save_AfterLoadingNewerVersion_KeepsNoBackupOfTheOriginal()
    {
        _fs.AddFile(_paths.ConfigFile, Newer);
        var errors = 0;
        var store = NewStore((_, _) => errors++);
        var loaded = store.Load();
        Assert.Empty(store.LastLoadRepairs);

        store.Save(loaded);

        Assert.Empty(Siblings()); // no .corrupt-/.repaired- copy: the original bytes are gone
        Assert.Equal(0, errors);
        Assert.Empty(store.LastLoadRepairs);
    }

    [Fact(DisplayName = "C-04: a second Load after the Save sees the current version and keeps the known fields")]
    public void Load_AfterSaveOfNewerVersionFile_ReadsBackCurrentVersion()
    {
        _fs.AddFile(_paths.ConfigFile, Newer);
        var first = NewStore();
        first.Save(first.Load());

        var second = NewStore();
        var reloaded = second.Load();

        Assert.Equal(AppSettings.CurrentConfigVersion, reloaded.ConfigVersion);
        Assert.Equal(250, reloaded.ClickZoomPercent);
        Assert.Empty(second.LastLoadRepairs);
    }

    [Fact(DisplayName = "C-04: Save stamps the current version even when the in-memory settings carry another one")]
    public void Save_AlwaysStampsCurrentConfigVersion()
    {
        var store = NewStore();
        var settings = new AppSettings { ConfigVersion = 99 };

        store.Save(settings);

        using var doc = JsonDocument.Parse(_fs.ReadAllText(_paths.ConfigFile));
        Assert.Equal(AppSettings.CurrentConfigVersion, doc.RootElement.GetProperty("ConfigVersion").GetInt32());
    }
}
