using System.Text.Json;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// Audit C-04. A config.json written by a NEWER build (ConfigVersion greater than <see cref="AppSettings.CurrentConfigVersion"/>)
/// is left untouched by <see cref="SettingsStore.Load"/>; the first <see cref="SettingsStore.Save"/> in this older build rewrites
/// it as the current version (unknown fields are dropped), so it first keeps the verbatim original as
/// <c>config.json.newer-yyyyMMddHHmmss</c> (unique name, newest 3 kept). A failed backup never blocks the Save.
/// </summary>
public sealed class SettingsStoreSaveAfterNewerVersionLoadTests
{
    private const string Newer = """{"ConfigVersion":99,"FutureField":123,"UiLanguage":"en","ClickZoomPercent":250}""";

    private readonly InMemoryFileSystem _fs = new();
    private readonly AppPaths _paths = new(@"C:\Users\test\AppData\Local");

    private SettingsStore NewStore(Action<string, Exception>? onStartupError = null) => new(_paths, _fs, NullLog.Instance, onStartupError);

    private List<string> Siblings() =>
        _fs.EnumerateFiles(Path.GetDirectoryName(_paths.ConfigFile)!, "*").Where(f => f != _paths.ConfigFile).ToList();

    private List<string> NewerBackups() =>
        Siblings().Where(f => Path.GetFileName(f).StartsWith("config.json.newer-", StringComparison.Ordinal)).ToList();

    private void LoadAndSave(string fileText)
    {
        _fs.AddFile(_paths.ConfigFile, fileText);
        var store = NewStore();
        store.Save(store.Load());
    }

    [Fact(DisplayName = "C-04: Save after loading a newer-build config rewrites it as the current version and drops unknown fields")]
    public void Save_AfterLoadingNewerVersion_WritesCurrentVersionWithoutUnknownFields()
    {
        _fs.AddFile(_paths.ConfigFile, Newer);
        var store = NewStore();
        var loaded = store.Load();
        Assert.Equal(99, loaded.ConfigVersion); // Load keeps the newer stamp in memory...
        Assert.Equal(Newer, _fs.ReadAllText(_paths.ConfigFile)); // ...and does not touch the file
        Assert.Empty(Siblings()); // Load itself makes no backup

        loaded.ClickZoomPercent = 300;
        store.Save(loaded);

        var written = _fs.ReadAllText(_paths.ConfigFile);
        using var doc = JsonDocument.Parse(written);
        Assert.Equal(AppSettings.CurrentConfigVersion, doc.RootElement.GetProperty("ConfigVersion").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("FutureField", out _));
        Assert.Equal(300, doc.RootElement.GetProperty("ClickZoomPercent").GetInt32());
        Assert.Equal(AppSettings.CurrentConfigVersion, store.Current.ConfigVersion);
    }

    [Fact(DisplayName = "C-04: that overwrite first keeps a byte-for-byte config.json.newer-<stamp> backup and raises no startup error")]
    public void Save_AfterLoadingNewerVersion_KeepsVerbatimBackupOfTheOriginal()
    {
        _fs.AddFile(_paths.ConfigFile, Newer);
        var errors = 0;
        var store = NewStore((_, _) => errors++);
        var loaded = store.Load();
        Assert.Empty(store.LastLoadRepairs);

        store.Save(loaded);

        var backup = Assert.Single(NewerBackups());
        Assert.Matches(@"^config\.json\.newer-\d{14}$", Path.GetFileName(backup));
        Assert.Equal(Path.GetDirectoryName(_paths.ConfigFile), Path.GetDirectoryName(backup));
        Assert.Equal(Newer, _fs.ReadAllText(backup));
        Assert.Equal(0, errors);
        Assert.Empty(store.LastLoadRepairs);
    }

    [Fact(DisplayName = "C-04: a second Save of the same store does not back up again (the file is already the current version)")]
    public void Save_Twice_BacksUpOnlyTheNewerOriginal()
    {
        _fs.AddFile(_paths.ConfigFile, Newer);
        var store = NewStore();
        var loaded = store.Load();
        store.Save(loaded);
        store.Save(loaded);

        var backup = Assert.Single(NewerBackups());
        Assert.Equal(Newer, _fs.ReadAllText(backup));
    }

    [Theory(DisplayName = "C-04: no backup when the file version equals or is below the app version")]
    [InlineData(AppSettings.CurrentConfigVersion)]
    [InlineData(2)]
    [InlineData(1)]
    public void Save_FileNotNewer_MakesNoBackup(int version)
    {
        LoadAndSave("{\"ConfigVersion\":" + version + ",\"ClickZoomPercent\":250}");

        Assert.Empty(NewerBackups());
    }

    [Fact(DisplayName = "C-04: two newer-file Saves in the same second get unique backup names and keep both originals")]
    public void Save_TwiceInSameSecond_UsesUniqueNames()
    {
        var first = Newer.Replace("123", "1");
        var second = Newer.Replace("123", "2");
        LoadAndSave(first);
        LoadAndSave(second);

        var contents = NewerBackups().Select(f => _fs.ReadAllText(f)).OrderBy(c => c, StringComparer.Ordinal).ToList();
        Assert.Equal([first, second], contents);
        Assert.Equal(2, NewerBackups().Select(f => Path.GetFileName(f)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact(DisplayName = "C-04: only the 3 newest config.json.newer-* backups are kept; other files are untouched")]
    public void Save_ManyTimes_KeepsAtMostThreeNewestBackups()
    {
        var corrupt = _paths.ConfigFile + ".corrupt-20200101000000";
        _fs.AddFile(corrupt, "keep me");
        for (var i = 1; i <= 5; i++) LoadAndSave(Newer.Replace("123", i.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var contents = NewerBackups().Select(f => _fs.ReadAllText(f)).OrderBy(c => c, StringComparer.Ordinal).ToList();
        Assert.Equal([Newer.Replace("123", "3"), Newer.Replace("123", "4"), Newer.Replace("123", "5")], contents);
        Assert.True(_fs.FileExists(corrupt));
    }

    [Fact(DisplayName = "C-04: a failing backup does not block the Save")]
    public void Save_WhenBackupFails_StillWritesTheConfig()
    {
        _fs.AddFile(_paths.ConfigFile, Newer);
        var store = NewStore();
        var loaded = store.Load();
        loaded.ClickZoomPercent = 310;
        _fs.CopyHook = (_, _) => new IOException("disk full");

        store.Save(loaded);

        Assert.Empty(NewerBackups());
        using var doc = JsonDocument.Parse(_fs.ReadAllText(_paths.ConfigFile));
        Assert.Equal(AppSettings.CurrentConfigVersion, doc.RootElement.GetProperty("ConfigVersion").GetInt32());
        Assert.Equal(310, doc.RootElement.GetProperty("ClickZoomPercent").GetInt32());
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
