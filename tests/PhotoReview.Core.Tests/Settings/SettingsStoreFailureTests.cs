using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>Failure injection on <see cref="SettingsStore"/>: a failed save must not lose or corrupt what is on disk.</summary>
public sealed class SettingsStoreFailureTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly AppPaths _paths = new(@"C:\Users\test\AppData\Local");

    private SettingsStore NewStore() => new(_paths, _fs, NullLog.Instance);

    [Theory(DisplayName = "A failed Save throws, leaves the previous config.json byte-for-byte, keeps Current and raises no Changed")]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Save_WriteFails_KeepsPreviousFileAndState(Type failure)
    {
        var store = NewStore();
        store.Save(new AppSettings { ClickZoomPercent = 150 });
        var before = _fs.ReadAllText(_paths.ConfigFile);
        var current = store.Current;
        var changed = 0;
        store.Changed += (_, _) => changed++;
        _fs.WriteHook = _ => (Exception)Activator.CreateInstance(failure, "disk full")!;

        Assert.Throws(failure, () => store.Save(new AppSettings { ClickZoomPercent = 400 }));

        Assert.Equal(before, _fs.ReadAllText(_paths.ConfigFile));
        Assert.Same(current, store.Current);
        Assert.Equal(0, changed);
        _fs.WriteHook = null;
        store.Save(new AppSettings { ClickZoomPercent = 400 });
        Assert.Equal(400, NewStore().Load().ClickZoomPercent);
        Assert.Equal(1, changed);
    }

    [Fact(DisplayName = "Save(null) is rejected")]
    public void Save_Null_Throws() => Assert.Throws<ArgumentNullException>(() => NewStore().Save(null!));

    [Theory(DisplayName = "Migrate is idempotent for every historic version and never leaves null members")]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void Migrate_Idempotent(int version)
    {
        var settings = new AppSettings { ConfigVersion = version, UiLanguage = null!, Actions = null!, Shortcuts = null! };

        SettingsStore.Migrate(settings);
        var once = System.Text.Json.JsonSerializer.Serialize(settings);
        SettingsStore.Migrate(settings);

        Assert.Equal(once, System.Text.Json.JsonSerializer.Serialize(settings));
        Assert.NotNull(settings.Actions);
        Assert.NotNull(settings.Shortcuts);
        Assert.False(string.IsNullOrWhiteSpace(settings.UiLanguage));
        Assert.True(settings.ConfigVersion >= AppSettings.CurrentConfigVersion);
    }

    [Fact(DisplayName = "A pre-language config (version 2) keeps the Vietnamese UI; a version-3 config keeps its own language")]
    public void Migrate_LanguageDefaults()
    {
        var old = new AppSettings { ConfigVersion = 2, UiLanguage = "auto" };
        var current = new AppSettings { ConfigVersion = 3, UiLanguage = "de" };

        SettingsStore.Migrate(old);
        SettingsStore.Migrate(current);

        Assert.Equal("vi", old.UiLanguage);
        Assert.Equal("de", current.UiLanguage);
    }

    [Fact(DisplayName = "Two corrupt loads within the same second keep both backups under unique names")]
    public void Load_TwoCorruptLoadsInOneSecond_KeepBothOrFirstBackup()
    {
        _fs.AddFile(_paths.ConfigFile, "{ broken 1");
        var first = NewStore();
        first.Load();
        var backups = _fs.EnumerateFiles(Path.GetDirectoryName(_paths.ConfigFile)!, "config.json.corrupt-*").ToList();
        Assert.Single(backups);
        var firstBackupText = _fs.ReadAllText(backups[0]);

        // Corrupt the freshly written defaults again straight away (same clock second).
        _fs.AddFile(_paths.ConfigFile, "{ broken 2");
        var second = Record.Exception(() => NewStore().Load());

        Assert.Null(second);
        Assert.Equal("{ broken 1", firstBackupText);
        var afterSecond = _fs.EnumerateFiles(Path.GetDirectoryName(_paths.ConfigFile)!, "config.json.corrupt-*").Select(f => _fs.ReadAllText(f)).ToList();
        Assert.Contains("{ broken 1", afterSecond);
        Assert.Contains("{ broken 2", afterSecond); // the second bad file is preserved too, under a unique name
        Assert.Equal(2, afterSecond.Count);
    }

    [Fact(DisplayName = "Load with a mistyped value keeps the readable properties, backs the file up and reports the reset names")]
    public void Load_MistypedValue_SalvagesAndReports()
    {
        _fs.AddFile(_paths.ConfigFile, """{"ConfigVersion":3,"ClickZoomPercent":"abc","LoggingEnabled":true,"LoadingMode":"Original"}""");
        var store = NewStore();

        var loaded = store.Load();

        Assert.True(loaded.LoggingEnabled);
        Assert.Equal(LoadingMode.Original, loaded.LoadingMode);
        Assert.Equal(AppSettings.DefaultClickZoomPercent, loaded.ClickZoomPercent);
        Assert.Contains(nameof(AppSettings.ClickZoomPercent), store.LastLoadRepairs);
        Assert.Single(_fs.EnumerateFiles(Path.GetDirectoryName(_paths.ConfigFile)!, "config.json.corrupt-*"));
    }

    [Fact(DisplayName = "Salvage whose backup fails keeps the file untouched for the whole session (no overwrite on Save)")]
    public void Load_SalvageBackupFails_ConfigNeverOverwritten()
    {
        const string original = """{"ConfigVersion":3,"ClickZoomPercent":"abc","LoggingEnabled":true}""";
        _fs.AddFile(_paths.ConfigFile, original);
        _fs.CopyHook = (_, _) => new IOException("read-only volume");
        var store = NewStore();

        var loaded = store.Load();
        store.Save(loaded);

        Assert.True(loaded.LoggingEnabled);
        Assert.Equal(original, _fs.ReadAllText(_paths.ConfigFile));
    }

    [Fact(DisplayName = "Salvage keeps a customised Actions list and Shortcuts object while resetting only the mistyped value")]
    public void Load_MistypedValue_KeepsNestedCollections()
    {
        _fs.AddFile(_paths.ConfigFile, """
            {"ConfigVersion":3,"ClickZoomPercent":"abc",
             "Actions":[{"Name":"Keep","Shortcut":"K","Operation":"Copy","Destination":"Keepers","Confirm":true}],
             "Shortcuts":{"Next":"N","Previous":"P"}}
            """);
        var store = NewStore();

        var loaded = store.Load();

        Assert.Contains(nameof(AppSettings.ClickZoomPercent), store.LastLoadRepairs);
        Assert.DoesNotContain(nameof(AppSettings.Actions), store.LastLoadRepairs);
        var action = Assert.Single(loaded.Actions);
        Assert.Equal("Keep", action.Name);
        Assert.Equal("Keepers", action.Destination);
        Assert.Equal(FileOperationType.Copy, action.Operation);
        Assert.True(action.Confirm);
        Assert.Equal("N", loaded.Shortcuts.Next);
        Assert.Equal("P", loaded.Shortcuts.Previous);
    }

    [Fact(DisplayName = "Salvage with a bad element inside Actions resets only Actions and reports it; Shortcuts and other values stay")]
    public void Load_BadElementInsideActions_ResetsOnlyActions()
    {
        _fs.AddFile(_paths.ConfigFile, """
            {"ConfigVersion":3,"LoggingEnabled":true,
             "Actions":[{"Name":"Ok","Shortcut":"K"},{"Name":5,"Shortcut":{"x":1}}],
             "Shortcuts":{"Next":"N"}}
            """);
        var store = NewStore();

        var loaded = store.Load();

        Assert.True(loaded.LoggingEnabled);
        Assert.Equal("N", loaded.Shortcuts.Next);
        Assert.Contains(nameof(AppSettings.Actions), store.LastLoadRepairs);
        Assert.NotEmpty(loaded.Actions);
    }

    [Fact(DisplayName = "Salvage with a bad value inside Shortcuts resets only Shortcuts and keeps the Actions list")]
    public void Load_BadValueInsideShortcuts_ResetsOnlyShortcuts()
    {
        _fs.AddFile(_paths.ConfigFile, """
            {"ConfigVersion":3,
             "Actions":[{"Name":"Keep","Shortcut":"K"}],
             "Shortcuts":{"Next":["not","a","string"]}}
            """);
        var store = NewStore();

        var loaded = store.Load();

        Assert.Contains(nameof(AppSettings.Shortcuts), store.LastLoadRepairs);
        Assert.Equal(new ShortcutMappings().Next, loaded.Shortcuts.Next);
        Assert.Equal("Keep", Assert.Single(loaded.Actions).Name);
    }

    [Fact(DisplayName = "RV-S03: a blank mandatory shortcut whose default is taken by an action keeps the default and clears the action's key, reported")]
    public void Load_BlankShortcutWhoseDefaultIsTaken_ActionShortcutIsClearedAndReported()
    {
        _fs.AddFile(_paths.ConfigFile, """
            {"ConfigVersion":3,"Shortcuts":{"Previous":""},
             "Actions":[{"Name":"Mine","Shortcut":"Left"},{"Name":"Other","Shortcut":"Z"}]}
            """);
        var store = NewStore();

        var loaded = store.Load();

        Assert.Equal(new ShortcutMappings().Previous, loaded.Shortcuts.Previous); // navigation keeps working
        Assert.Equal("", loaded.Actions[0].Shortcut);
        Assert.Equal("Z", loaded.Actions[1].Shortcut); // unrelated actions are never touched
        Assert.Contains(nameof(AppSettings.Shortcuts), store.LastLoadRepairs);
        Assert.Contains("Actions[0].Shortcut", store.LastLoadRepairs);
        Assert.DoesNotContain("Actions[1].Shortcut", store.LastLoadRepairs);
    }

    [Fact(DisplayName = "RV-S03: an action blank in Name/Shortcut or sharing a key loads (not dropped) and is left for the settings dialog to reject")]
    public void Load_BlankOrDuplicateActions_LoadAndValidatorFlagsThem()
    {
        _fs.AddFile(_paths.ConfigFile, """
            {"ConfigVersion":3,
             "Actions":[{"Name":"","Shortcut":"Q"},{"Name":"B","Shortcut":""},{"Name":"C","Shortcut":"W"},{"Name":"D","Shortcut":"W"}]}
            """);
        var store = NewStore();

        var loaded = store.Load();

        Assert.Equal(4, loaded.Actions.Count);
        Assert.NotNull(store.ValidateShortcuts(loaded));
    }

    [Fact(DisplayName = "RV-S04: a Changed handler that throws IOException surfaces to the caller and does not make Load fall back to defaults")]
    public void Load_ChangedHandlerThrowsIOException_KeepsLoadedValues()
    {
        _fs.AddFile(_paths.ConfigFile, """{"ConfigVersion":3,"ClickZoomPercent":150}""");
        var store = NewStore();
        EventHandler<AppSettings> broken = (_, _) => throw new IOException("handler bug");
        store.Changed += broken;

        Assert.Throws<IOException>(() => store.Load());

        Assert.Equal(150, store.Current.ClickZoomPercent); // not the defaults
        store.Changed -= broken;
        store.Save(new AppSettings { ClickZoomPercent = 200 }); // _keepCorruptFile was not set: Save still writes the file
        Assert.Equal(200, NewStore().Load().ClickZoomPercent);
    }

    public static TheoryData<string, string> UnparsableEnumCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var name in new[]
        {
            nameof(AppSettings.LoadingMode), nameof(AppSettings.ImageSortMode), nameof(AppSettings.ScalingQuality),
            nameof(AppSettings.DecoderBackend), nameof(AppSettings.KeyboardZoomAnchor), nameof(AppSettings.KineticGlideSmoothing),
            nameof(AppSettings.FitWidthAnchor2), nameof(AppSettings.MiddleClickAction),
        })
        {
            foreach (var literal in new[] { "\"garbage\"", "\"\"", "null", "42", "{}" })
                data.Add(name, literal);
        }
        return data;
    }

    [Theory(DisplayName = "RV-S01: an unparsable enum value loads as the AppSettings default (not the zero member) and is reported")]
    [MemberData(nameof(UnparsableEnumCases))]
    public void Load_UnparsableEnum_ResetsToDefaultAndReports(string property, string jsonLiteral)
    {
        _fs.AddFile(_paths.ConfigFile, "{\"ConfigVersion\":3,\"" + property + "\":" + jsonLiteral + "}");
        var store = NewStore();

        var loaded = store.Load();

        var prop = typeof(AppSettings).GetProperty(property)!;
        Assert.Equal(prop.GetValue(new AppSettings()), prop.GetValue(loaded));
        Assert.Contains(property, store.LastLoadRepairs);
    }

    [Fact(DisplayName = "RV-S01: a repaired enum is written back by its PascalCase name")]
    public void Load_UnparsableEnum_IsPersistedAsName()
    {
        _fs.AddFile(_paths.ConfigFile, "{\"ConfigVersion\":3,\"LoadingMode\":\"garbage\"}");
        NewStore().Load();

        Assert.Contains("\"LoadingMode\": \"Preview\"", _fs.ReadAllText(_paths.ConfigFile), StringComparison.Ordinal);
        var second = NewStore();
        second.Load();
        Assert.Empty(second.LastLoadRepairs);
    }
}
