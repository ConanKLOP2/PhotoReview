using System.IO;
using System.Text.Json;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

[Trait("Category", "HotPath")]
public sealed class SettingsStoreTests
{
    private readonly InMemoryFileSystem _fileSystem;
    private readonly AppPaths _appPaths;
    private readonly List<(string Message, Exception Ex)> _startupErrors = [];
    private readonly SettingsStore _store;

    public SettingsStoreTests()
    {
        _fileSystem = new InMemoryFileSystem();
        var appData = @"C:\Users\test\AppData\Local";
        _appPaths = new AppPaths(appData);
        _store = new SettingsStore(
            _appPaths,
            _fileSystem,
            NullLog.Instance,
            (msg, ex) => _startupErrors.Add((msg, ex)));
    }

    [Fact(DisplayName = "Load when file does not exist creates and saves default settings")]
    public void Load_WhenFileDoesNotExist_CreatesAndSavesDefault()
    {
        Assert.False(_fileSystem.FileExists(_appPaths.ConfigFile));

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(AppSettings.CurrentConfigVersion, loaded.ConfigVersion);
        Assert.Equal(LoadingMode.Preview, loaded.LoadingMode);
        Assert.True(_fileSystem.FileExists(_appPaths.ConfigFile));
        var savedJson = _fileSystem.ReadAllText(_appPaths.ConfigFile);
        Assert.Contains("\"ConfigVersion\": 3", savedJson);
    }

    [Fact(DisplayName = "Load ignores the retired Folder2Name property from an old config")]
    public void Load_WhenLegacyFolder2NameIsPresent_IgnoresItAndKeepsOtherValues()
    {
        var json = """
        {
            "ConfigVersion": 2,
            "Folder2Name": "Old-Target",
            "LoadingMode": "Fast",
            "LoggingEnabled": true
        }
        """;
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, json);

        var loaded = _store.Load();

        Assert.Equal(LoadingMode.Fast, loaded.LoadingMode);
        Assert.True(loaded.LoggingEnabled);
    }

    [Fact(DisplayName = "Load parses valid config and raises Changed")]
    public void Load_WhenValidConfigExists_ParsesProperly()
    {
        var json = """
        {
            "ConfigVersion": 2,
            "LoadingMode": "Fast",
            "ImageSortMode": "Size",
            "LoggingEnabled": true
        }
        """;
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, json);

        var changedFired = false;
        _store.Changed += (_, s) =>
        {
            changedFired = true;
            Assert.Equal(LoadingMode.Fast, s.LoadingMode);
            Assert.Equal(ImageSortMode.SizeDescending, s.ImageSortMode);
            Assert.True(s.LoggingEnabled);
        };

        var loaded = _store.Load();

        Assert.True(changedFired);
        Assert.Equal(LoadingMode.Fast, loaded.LoadingMode);
        Assert.Equal(ImageSortMode.SizeDescending, loaded.ImageSortMode);
        Assert.True(loaded.LoggingEnabled);
        Assert.Empty(_startupErrors);
    }

    [Fact(DisplayName = "INV-11: Load backs up corrupt config and resets to defaults")]
    public void Load_Inv11_WhenCorruptConfig_BacksUpAndResetsToDefault()
    {
        var corruptContent = "{ this is not valid json, corrupt content }";
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, corruptContent);

        var loaded = _store.Load();

        // 1. Reset về default trong RAM và đĩa
        Assert.NotNull(loaded);
        Assert.Equal(LoadingMode.Preview, loaded.LoadingMode);
        Assert.Equal(AppSettings.CurrentConfigVersion, loaded.ConfigVersion);

        // 2. Ghi nhận lỗi khởi động
        Assert.Single(_startupErrors);
        Assert.Contains("Corrupt config.json detected", _startupErrors[0].Message);
        Assert.IsAssignableFrom<JsonException>(_startupErrors[0].Ex);

        // 3. File backup .corrupt-<timestamp> được tạo ra
        var dir = Path.GetDirectoryName(_appPaths.ConfigFile)!;
        var backupFiles = _fileSystem.EnumerateFiles(dir, "config.json.corrupt-*").ToList();
        Assert.Single(backupFiles);
        Assert.Equal(corruptContent, _fileSystem.ReadAllText(backupFiles[0]));
    }

    [Fact(DisplayName = "A corrupt config that could not be backed up is never overwritten, not even by a later Save")]
    public void Load_CorruptConfigBackupFails_LaterSaveKeepsTheCorruptFile()
    {
        var corruptContent = "{ this is not valid json }";
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, corruptContent);
        _fileSystem.CopyHook = (_, _) => new UnauthorizedAccessException("backup denied");

        var loaded = _store.Load();
        _store.Save(loaded);

        Assert.Equal(corruptContent, _fileSystem.ReadAllText(_appPaths.ConfigFile));
        Assert.Same(loaded, _store.Current);
    }

    [Fact(DisplayName = "A valid config that was only unreadable (locked) is not replaced by defaults on a later Save")]
    public void Load_ConfigLocked_LaterSaveKeepsTheValidFile()
    {
        var validJson = "{ \"ConfigVersion\": 3, \"LoggingEnabled\": true }";
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, validJson);
        _fileSystem.OpenReadHook = _ => new IOException("locked");

        var loaded = _store.Load();
        _fileSystem.OpenReadHook = null;
        _store.Save(loaded);

        Assert.Equal(validJson, _fileSystem.ReadAllText(_appPaths.ConfigFile));
    }

    [Fact(DisplayName = "INV-11: Load falls back to in-memory defaults without overwriting locked config")]
    public void Load_Inv11_WhenFileLocked_UsesInMemoryDefaultsAndLeavesFileIntact()
    {
        var validJson = """
        {
            "ConfigVersion": 2,
            "LoadingMode": "Original"
        }
        """;
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, validJson);

        // Giả lập file bị antivirus hoặc tiến trình khác khóa độc quyền
        _fileSystem.OpenReadHook = path =>
            path.Equals(_appPaths.ConfigFile, StringComparison.OrdinalIgnoreCase)
                ? new IOException("Sharing violation: File is locked by another process.")
                : null;

        var loaded = _store.Load();

        // 1. Trả về default trong RAM
        Assert.NotNull(loaded);
        Assert.Equal(LoadingMode.Preview, loaded.LoadingMode);

        // 2. Báo lỗi vào callback
        Assert.Single(_startupErrors);
        Assert.Contains("Config.json inaccessible", _startupErrors[0].Message);
        Assert.IsType<IOException>(_startupErrors[0].Ex);

        // 3. File trên đĩa không bị ghi đè hay xóa
        _fileSystem.OpenReadHook = null;
        Assert.Equal(validJson, _fileSystem.ReadAllText(_appPaths.ConfigFile));
    }

    [Fact(DisplayName = "Save writes atomic JSON and raises Changed")]
    public void Save_WritesAtomicAndRaisesChanged()
    {
        var settings = new AppSettings
        {
            LoadingMode = LoadingMode.Original,
            LoggingEnabled = true
        };

        var eventRaised = false;
        _store.Changed += (_, s) =>
        {
            eventRaised = true;
            Assert.Equal(LoadingMode.Original, s.LoadingMode);
        };

        _store.Save(settings);

        Assert.True(eventRaised);
        Assert.Same(settings, _store.Current);
        Assert.True(_fileSystem.FileExists(_appPaths.ConfigFile));

        var diskJson = _fileSystem.ReadAllText(_appPaths.ConfigFile);
        Assert.Contains("\"LoggingEnabled\": true", diskJson);
        Assert.DoesNotContain("Folder2Name", diskJson);
        Assert.Contains("\"LoadingMode\": \"Original\"", diskJson);
    }

    [Fact(DisplayName = "Migrate upgrades version 1 config to the current version")]
    public void Migrate_UpgradesV1ToCurrent()
    {
        var v1Settings = new AppSettings
        {
            ConfigVersion = 1,
            Actions = null!
        };

        SettingsStore.Migrate(v1Settings);

        Assert.Equal(AppSettings.CurrentConfigVersion, v1Settings.ConfigVersion);
        Assert.NotNull(v1Settings.Actions);
        Assert.NotEmpty(v1Settings.Actions);
        Assert.NotNull(v1Settings.Shortcuts);
    }

    // Reference: the reflection-based serializer config.json used before perf(startup).
    private static readonly JsonSerializerOptions ReflectionIndented = new() { WriteIndented = true };

    [Fact(DisplayName = "perf(startup): Save writes byte-for-byte what the reflection serializer wrote")]
    public void Save_SourceGeneratedJson_MatchesReflectionFormat()
    {
        var settings = new AppSettings
        {
            LoggingEnabled = true,
            DecoderBackend = DecoderBackend.WicDirect,
            ImageSortMode = ImageSortMode.SizeDescending,
            PreloadMemoryLoadLimit = 0.75,
            Actions = [new ReviewAction { Name = "Loại 2", Shortcut = "Enter", Destination = @"C:\Xiuren\[[WALLPAPER]" }],
        };

        _store.Save(settings);

        Assert.Equal(JsonSerializer.Serialize(settings, ReflectionIndented), _fileSystem.ReadAllText(_appPaths.ConfigFile));
    }

    [Fact(DisplayName = "perf(startup): Load reads config.json exactly like the reflection serializer, incl. lenient enums")]
    public void Load_SourceGeneratedJson_MatchesReflectionRead()
    {
        var json = """
        {
            "ConfigVersion": 2,
            "LoadingMode": "NoSuchMode",
            "DecoderBackend": "WicDirect",
            "ImageSortMode": 3,
            "ImageCacheCapacityBytes": 17179869184,
            "PreloadWorkerCount": 8,
            "Shortcuts": { "Next": "D", "Previous": "A" },
            "Actions": [ { "Name": "Keep", "Shortcut": "K", "Operation": "Copy", "Destination": "Keep" } ]
        }
        """;
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, json);

        var loaded = _store.Load();
        var expected = JsonSerializer.Deserialize<AppSettings>(json)!;
        SettingsStore.Migrate(expected); // Load migrates v2 -> current (UiLanguage, ADR 0006); compare deserializers only

        Assert.Equal(JsonSerializer.Serialize(expected, ReflectionIndented), JsonSerializer.Serialize(loaded, ReflectionIndented));
        Assert.Equal("D", loaded.Shortcuts.Next);
        Assert.Equal(FileOperationType.Copy, Assert.Single(loaded.Actions).Operation);
    }

    private static readonly string[] ExpectedRepairs =
        ["ImageCacheCapacityBytes", "SourceBytesCapacityBytes", "PreloadWorkerCount", "PreloadMemoryLoadLimit", "Actions"];

    [Fact(DisplayName = "Load resets unusable numeric values and null actions to safe defaults and reports them (R2-F-04)")]
    public void Load_WhenConfigHoldsUnusableValues_ResetsThemAndReportsRepairs()
    {
        var json = """
        {
            "ImageCacheCapacityBytes": 0,
            "SourceBytesCapacityBytes": -5,
            "PreloadWorkerCount": -1,
            "PreloadMemoryLoadLimit": 7.5,
            "Actions": [null, { "Name": "Keep", "Shortcut": "F9", "Operation": "Move", "Destination": "K" }]
        }
        """;
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, json);

        var loaded = _store.Load();

        Assert.Equal(PerformanceOptions.ImageCacheCapacityBytes, loaded.ImageCacheCapacityBytes);
        Assert.Equal(PerformanceOptions.SourceBytesCapacityBytes, loaded.SourceBytesCapacityBytes);
        Assert.Equal(PerformanceOptions.PreloadWorkerCount, loaded.PreloadWorkerCount);
        Assert.Equal(PerformanceOptions.PreloadMemoryLoadLimit, loaded.PreloadMemoryLoadLimit);
        var action = Assert.Single(loaded.Actions);
        Assert.Equal("Keep", action.Name);
        Assert.Equal(ExpectedRepairs.Order(), _store.LastLoadRepairs.Order());
    }

    [Fact(DisplayName = "Load of a valid config reports no repairs")]
    public void Load_WhenConfigIsValid_ReportsNoRepairs()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, "{ \"LoggingEnabled\": true }");

        _store.Load();

        Assert.Empty(_store.LastLoadRepairs);
    }

    [Fact(DisplayName = "Normalize resets an out-of-range enum value")]
    public void Normalize_WhenEnumIsOutOfRange_ResetsToDefault()
    {
        var settings = new AppSettings { LoadingMode = (LoadingMode)99 };

        var repaired = SettingsNormalizer.Normalize(settings);

        Assert.Equal(LoadingMode.Preview, settings.LoadingMode);
        Assert.Equal(nameof(AppSettings.LoadingMode), Assert.Single(repaired));
    }

    // ---- AR11a: v1/v2 fixture migration, moved from the App.Tests project's now-removed AppSettings.Load(path)
    // path onto the internal SettingsStore.Parse(json) seam (no disk access needed). ----

    private static string FixtureText(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact(DisplayName = "Parse of a v1 fixture migrates to enums properly")]
    public void Parse_V1Fixture_MigratesProperly()
    {
        var settings = SettingsStore.Parse(FixtureText("config-v1.json"));

        Assert.Equal(LoadingMode.Preview, settings.LoadingMode);
        Assert.Equal(ImageSortMode.SizeDescending, settings.ImageSortMode);
        Assert.Equal(InitialViewMode.Fit, settings.InitialViewMode);
        Assert.Single(settings.Actions);
        Assert.Equal(FileOperationType.Move, settings.Actions[0].Operation);
    }

    [Fact(DisplayName = "Parse of a v2 fixture parses enums and aliases correctly")]
    public void Parse_V2Fixture_ParsesCorrectly()
    {
        var settings = SettingsStore.Parse(FixtureText("config-v2.json"));

        Assert.Equal(AppSettings.CurrentConfigVersion, settings.ConfigVersion);
        Assert.Equal("vi", settings.UiLanguage); // Q-L1: pre-i18n configs keep Vietnamese
        Assert.Equal(LoadingMode.Original, settings.LoadingMode);
        Assert.Equal(ImageSortMode.SizeAscending, settings.ImageSortMode);
        Assert.Equal(InitialViewMode.Percent200, settings.InitialViewMode);
        Assert.Single(settings.Actions);
        Assert.Equal(FileOperationType.Recycle, settings.Actions[0].Operation);
    }
}
