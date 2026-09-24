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
}
