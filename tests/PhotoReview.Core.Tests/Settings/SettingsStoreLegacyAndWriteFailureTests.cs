using System.IO;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

[Trait("Category", "HotPath")]
public sealed class SettingsStoreLegacyAndWriteFailureTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");
    private readonly List<string> _startupErrors = [];
    private readonly SettingsStore _store;

    public SettingsStoreLegacyAndWriteFailureTests() =>
        _store = new SettingsStore(_appPaths, _fileSystem, NullLog.Instance, (msg, _) => _startupErrors.Add(msg));

    [Theory(DisplayName = "Load: an unwritable default config on first run degrades to in-memory defaults instead of throwing")]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Load_FirstRun_DefaultWriteFails_ReturnsInMemoryDefaults(Type exceptionType)
    {
        _fileSystem.WriteHook = _ => (Exception)Activator.CreateInstance(exceptionType, "disk full")!;
        AppSettings? changed = null;
        _store.Changed += (_, s) => changed = s;

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Same(loaded, _store.Current);
        Assert.Same(loaded, changed);
        Assert.Single(_startupErrors);
    }

    [Fact(DisplayName = "Load: a corrupt config whose default rewrite fails also degrades to in-memory defaults")]
    public void Load_CorruptConfig_DefaultWriteFails_ReturnsInMemoryDefaults()
    {
        _fileSystem.AddFile(_appPaths.ConfigFile, "{ not json");
        _fileSystem.WriteHook = _ => new IOException("read-only");

        var loaded = _store.Load();

        Assert.Equal(AppSettings.CurrentConfigVersion, loaded.ConfigVersion);
        Assert.Contains(_startupErrors, m => m.Contains("default config.json", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Load: a pre-versioned config (no ConfigVersion key) is migrated and keeps the Vietnamese UI (Q-L1)")]
    public void Load_ConfigWithoutVersionKey_KeepsVietnamese()
    {
        _fileSystem.AddFile(_appPaths.ConfigFile, """{ "LoadingMode": "Fast" }""");

        var loaded = _store.Load();

        Assert.Equal("vi", loaded.UiLanguage);
        Assert.Equal(AppSettings.CurrentConfigVersion, loaded.ConfigVersion);
    }

    [Fact(DisplayName = "Load: an unversioned config with one unreadable value (salvage path) still migrates to Vietnamese")]
    public void Load_UnversionedSalvagedConfig_KeepsVietnamese()
    {
        _fileSystem.AddFile(_appPaths.ConfigFile, """{ "ClickZoomPercent": "abc", "LoadingMode": "Fast" }""");

        var loaded = _store.Load();

        Assert.Equal("vi", loaded.UiLanguage);
    }

    [Fact(DisplayName = "Load: a current-version config keeps its own UiLanguage")]
    public void Load_CurrentVersionConfig_KeepsItsLanguage()
    {
        _fileSystem.AddFile(_appPaths.ConfigFile, """{ "ConfigVersion": 3, "UiLanguage": "auto" }""");

        Assert.Equal("auto", _store.Load().UiLanguage);
    }

    [Fact(DisplayName = "Parse of the v1 fixture (no ConfigVersion key) migrates UiLanguage to vi")]
    public void Parse_V1Fixture_UiLanguageIsVietnamese()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "config-v1.json"));

        Assert.Equal("vi", SettingsStore.Parse(json).UiLanguage);
    }
}
