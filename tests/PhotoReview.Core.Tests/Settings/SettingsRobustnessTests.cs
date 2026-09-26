using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// Adversarial config.json handling on the real file system (BOM, encodings, garbage, locked files) plus a
/// mutation fuzz: whatever a hand-edited or corrupted file contains, <see cref="SettingsStore.Load"/> must not throw
/// and must hand the rest of the app settings it can safely use.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class SettingsRobustnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview-SettingsRobust-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly List<string> _startupErrors = [];

    public SettingsRobustnessTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new AppPaths(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort temp cleanup */ }
    }

    private SettingsStore NewStore() => new(_paths, new PhysicalFileSystem(), NullLog.Instance, (m, _) => _startupErrors.Add(m));

    private AppSettings LoadBytes(byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.ConfigFile)!);
        File.WriteAllBytes(_paths.ConfigFile, bytes);
        return NewStore().Load();
    }

    private AppSettings LoadJson(string json) => LoadBytes(new UTF8Encoding(false).GetBytes(json));

    /// <summary>What the rest of the app relies on: nothing null, every number/enum inside its supported range.</summary>
    internal static void AssertUsable(AppSettings s)
    {
        Assert.NotNull(s.Actions);
        Assert.All(s.Actions, a =>
        {
            Assert.NotNull(a);
            Assert.NotNull(a.Name);
            Assert.NotNull(a.Shortcut);
            Assert.NotNull(a.Destination);
            Assert.True(Enum.IsDefined(a.Operation));
        });
        Assert.NotNull(s.Shortcuts);
        foreach (var p in typeof(ShortcutMappings).GetProperties().Where(p => p.PropertyType == typeof(string)))
        {
            var value = (string?)p.GetValue(s.Shortcuts);
            Assert.NotNull(value);
            if (!ShortcutMappings.IsOptional(p.Name)) Assert.False(string.IsNullOrWhiteSpace(value), p.Name + " must not be blank");
        }
        Assert.False(string.IsNullOrWhiteSpace(s.UiLanguage));
        Assert.True(s.ImageCacheCapacityBytes > 0);
        Assert.InRange(s.ImageCacheRamPercent, PerformanceOptions.MinImageCacheRamPercent, PerformanceOptions.MaxImageCacheRamPercent);
        Assert.True(s.SourceBytesCapacityBytes > 0);
        Assert.True(s.MemoryReserveBytes >= 0);
        Assert.True(s.PreviewDiskCacheCapacityBytes >= 0);
        Assert.InRange(s.PreloadWorkerCount, 1, PerformanceOptions.MaxPreloadWorkerCount);
        Assert.True(s.PreloadMemoryLoadLimit > 0 && s.PreloadMemoryLoadLimit <= 1);
        Assert.InRange(s.ClickZoomPercent, AppSettings.MinClickZoomPercent, AppSettings.MaxClickZoomPercent);
        Assert.True(Enum.IsDefined(s.InitialViewMode));
        Assert.True(Enum.IsDefined(s.LoadingMode));
        Assert.True(Enum.IsDefined(s.ImageSortMode));
        Assert.True(Enum.IsDefined(s.ScalingQuality));
        Assert.True(Enum.IsDefined(s.DecoderBackend));
        Assert.True(Enum.IsDefined(s.JournalDurability));
        Assert.True(Enum.IsDefined(s.InstanceMode));
        Assert.True(Enum.IsDefined(s.MouseWheelAction));
    }

    [Theory(DisplayName = "Load never throws for structurally odd files and always returns usable settings")]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("{")]
    [InlineData("{\"Actions\": [")]
    [InlineData("{} trailing")]
    [InlineData("{\"LoadingMode\": \"Fast\"} {\"LoadingMode\": \"Preview\"}")]
    public void Load_OddStructure_ReturnsUsableSettings(string json)
    {
        var s = LoadJson(json);

        AssertUsable(s);
    }

    [Fact(DisplayName = "Load of a UTF-8 BOM file keeps the values")]
    public void Load_Utf8Bom_KeepsValues()
    {
        var body = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"ConfigVersion\":3,\"LoggingEnabled\":true,\"ClickZoomPercent\":250}")).ToArray();

        var s = LoadBytes(body);

        Assert.True(s.LoggingEnabled);
        Assert.Equal(250, s.ClickZoomPercent);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_paths.ConfigFile)!, "*.corrupt-*"));
    }

    [Fact(DisplayName = "Load of a UTF-16 file (Notepad 'Unicode') keeps the values")]
    public void Load_Utf16_KeepsValues()
    {
        var s = LoadBytes(new UnicodeEncoding(false, true).GetPreamble().Concat(new UnicodeEncoding(false, true).GetBytes("{\"ConfigVersion\":3,\"LoggingEnabled\":true}")).ToArray());

        Assert.True(s.LoggingEnabled);
    }

    [Fact(DisplayName = "Binary garbage is backed up, defaults are used and startup does not throw")]
    public void Load_BinaryGarbage_BacksUpAndUsesDefaults()
    {
        var garbage = new byte[4096];
        new Random(7).NextBytes(garbage);

        var s = LoadBytes(garbage);

        AssertUsable(s);
        var backups = Directory.GetFiles(Path.GetDirectoryName(_paths.ConfigFile)!, "*.corrupt-*");
        Assert.Single(backups);
        Assert.Equal(garbage, File.ReadAllBytes(backups[0]));
    }

    [Fact(DisplayName = "Unknown and nested extra fields are ignored; the known ones still load")]
    public void Load_ExtraFields_Ignored()
    {
        var s = LoadJson("""{"ConfigVersion":3,"Future":{"a":[1,2,{"b":null}]},"LoggingEnabled":true,"AnotherFuture":"x","ClickZoomPercent":200}""");

        Assert.True(s.LoggingEnabled);
        Assert.Equal(200, s.ClickZoomPercent);
    }

    [Fact(DisplayName = "A duplicated property in the file: the last value wins and nothing throws")]
    public void Load_DuplicateProperty_LastWins()
    {
        var s = LoadJson("""{"ConfigVersion":3,"ClickZoomPercent":150,"ClickZoomPercent":300}""");

        Assert.Equal(300, s.ClickZoomPercent);
    }

    [Fact(DisplayName = "Extremely deep nesting in an unknown field is treated as corrupt, not a stack overflow")]
    public void Load_DeepNesting_DoesNotCrash()
    {
        var deep = new string('[', 100_000) + new string(']', 100_000);

        var s = LoadJson("{\"ConfigVersion\":3,\"Future\":" + deep + "}");

        AssertUsable(s);
    }

    [Fact(DisplayName = "A multi-MB config file (huge unknown string) loads, keeps its known values and is not backed up as corrupt")]
    public void Load_HugeFile_DoesNotThrow()
    {
        // 4 MB is far beyond any real config yet keeps the default run light (was 50 MB = ~250 MB of string copies).
        var sb = new StringBuilder("{\"ConfigVersion\":3,\"ClickZoomPercent\":250,\"Junk\":\"");
        sb.Append('x', 4 * 1024 * 1024).Append("\"}");

        var s = LoadJson(sb.ToString());

        AssertUsable(s);
        Assert.Equal(250, s.ClickZoomPercent);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_paths.ConfigFile)!, "*.corrupt-*"));
    }

    [Theory(DisplayName = "Out-of-range and extreme numbers are repaired to usable values")]
    [InlineData("\"ClickZoomPercent\": -5")]
    [InlineData("\"ClickZoomPercent\": 2147483647")]
    [InlineData("\"ClickZoomPercent\": -2147483648")]
    [InlineData("\"ImageCacheRamPercent\": 2147483647")]
    [InlineData("\"ImageCacheRamPercent\": -1")]
    [InlineData("\"ImageCacheCapacityBytes\": 0")]
    [InlineData("\"ImageCacheCapacityBytes\": -9223372036854775808")]
    [InlineData("\"SourceBytesCapacityBytes\": -1")]
    [InlineData("\"MemoryReserveBytes\": -1")]
    [InlineData("\"PreviewDiskCacheCapacityBytes\": -1")]
    [InlineData("\"PreloadWorkerCount\": 0")]
    [InlineData("\"PreloadWorkerCount\": -3")]
    [InlineData("\"PreloadWorkerCount\": 2147483647")]
    [InlineData("\"PreloadMemoryLoadLimit\": 0")]
    [InlineData("\"PreloadMemoryLoadLimit\": -0.5")]
    [InlineData("\"PreloadMemoryLoadLimit\": 1.5")]
    [InlineData("\"PreloadMemoryLoadLimit\": 1.7976931348623157e308")]
    [InlineData("\"PreloadMemoryLoadLimit\": 5e-324")]
    [InlineData("\"InitialViewMode\": 99")]
    [InlineData("\"DecoderBackend\": \"NoSuchDecoder\"")]
    [InlineData("\"MouseWheelAction\": {\"x\":1}")]
    [InlineData("\"JournalDurability\": []")]
    public void Load_ExtremeNumbers_AreRepaired(string field)
    {
        var s = LoadJson("{\"ConfigVersion\":3," + field + "}");

        AssertUsable(s);
    }

    [Theory(DisplayName = "Null or blank mandatory shortcuts are repaired to their defaults")]
    [InlineData("\"Next\": null", "Next", "Right")]
    [InlineData("\"Previous\": \"\"", "Previous", "Left")]
    [InlineData("\"Undo\": \"   \"", "Undo", "Z")]
    [InlineData("\"Fullscreen\": null", "Fullscreen", "F11")]
    public void Load_BlankMandatoryShortcut_ResetToDefault(string field, string property, string expected)
    {
        var s = LoadJson("{\"ConfigVersion\":3,\"Shortcuts\":{" + field + "}}");

        Assert.Equal(expected, typeof(ShortcutMappings).GetProperty(property)!.GetValue(s.Shortcuts));
        Assert.Contains(nameof(AppSettings.Shortcuts), NewStoreLoadRepairs());
    }

    private IReadOnlyList<string> NewStoreLoadRepairs()
    {
        var store = NewStore();
        store.Load();
        return store.LastLoadRepairs;
    }

    [Fact(DisplayName = "Actions with null name/shortcut/destination and null entries load as usable actions")]
    public void Load_ActionsWithNulls_Usable()
    {
        var s = LoadJson("""{"ConfigVersion":3,"Actions":[null,{"Name":null,"Shortcut":null,"Destination":null,"Operation":"Copy"},{"Operation":77}]}""");

        AssertUsable(s);
        Assert.Equal(2, s.Actions.Count);
    }

    [Fact(DisplayName = "Empty Actions array is honoured (user removed all actions)")]
    public void Load_EmptyActions_StaysEmpty()
    {
        var s = LoadJson("""{"ConfigVersion":3,"Actions":[]}""");

        Assert.Empty(s.Actions);
    }

    [Fact(DisplayName = "Two actions bound to the same shortcut load; the validator (not Load) reports the conflict")]
    public void Load_DuplicateActionShortcuts_LoadsAndValidatorFlagsIt()
    {
        var s = LoadJson("""{"ConfigVersion":3,"Actions":[{"Name":"A","Shortcut":"F7","Operation":"Move","Destination":"a"},{"Name":"B","Shortcut":"f7","Operation":"Copy","Destination":"b"}]}""");

        Assert.Equal(2, s.Actions.Count);
        Assert.NotNull(NewStore().ValidateShortcuts(s));
    }

    [Fact(DisplayName = "A future ConfigVersion loads without being migrated backwards")]
    public void Load_FutureVersion_NotDowngradedInMemory()
    {
        var s = LoadJson("""{"ConfigVersion":99,"UiLanguage":"fr"}""");

        Assert.Equal("fr", s.UiLanguage);
        Assert.True(s.ConfigVersion >= AppSettings.CurrentConfigVersion);
    }

    [Fact(DisplayName = "A string-typed number ('4') in the file is accepted instead of discarding the whole config")]
    public void Load_StringTypedNumber_Accepted()
    {
        var s = LoadJson("""{"ConfigVersion":3,"LoggingEnabled":true,"ClickZoomPercent":"250"}""");

        Assert.True(s.LoggingEnabled);
        Assert.Equal(250, s.ClickZoomPercent);
    }

    [Fact(DisplayName = "Comments and trailing commas in a hand-edited config are accepted, not treated as corruption")]
    public void Load_CommentsAndTrailingCommas_Accepted()
    {
        var s = LoadJson("""
            {
              // my tweaks
              "ConfigVersion": 3,
              "LoggingEnabled": true, /* on */
              "Actions": [ { "Name": "Mine", "Shortcut": "F9", "Operation": "Copy", "Destination": "D", }, ],
              "ClickZoomPercent": "abc", // salvaged path must accept comments too
            }
            """);

        Assert.True(s.LoggingEnabled);
        Assert.Equal("Mine", Assert.Single(s.Actions).Name);
        Assert.Equal(AppSettings.DefaultClickZoomPercent, s.ClickZoomPercent);
    }

    [Fact(DisplayName = "A commented, comma-trailing but otherwise valid config leaves no .corrupt backup")]
    public void Load_CommentedValidConfig_NoBackup()
    {
        var s = LoadJson("{ /* c */ \"ConfigVersion\": 3, \"ClickZoomPercent\": 250, }");

        Assert.Equal(250, s.ClickZoomPercent);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_paths.ConfigFile)!, "*.corrupt-*"));
    }

    [Fact(DisplayName = "Concurrent Save and Load never observe a torn config.json")]
    public async Task ConcurrentSaveAndLoad_NeverTorn()
    {
        var writer = NewStore();
        writer.Save(new AppSettings());
        using var stop = new CancellationTokenSource();
        var failures = new List<string>();
        var savers = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            var store = NewStore();
            for (var i = 0; i < 60; i++)
            {
                var s = new AppSettings { ClickZoomPercent = 10 + (t * 60) + i, LastMoveToFolder = new string('é', 200 + i) };
                try { store.Save(s); }
                catch (IOException) { /* a rename racing another rename may fail; the file must still be whole */ }
                catch (UnauthorizedAccessException) { }
            }
        })).ToArray();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                string text;
                try { text = File.ReadAllText(_paths.ConfigFile); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                try { JsonDocument.Parse(text).Dispose(); }
                catch (JsonException e) { lock (failures) failures.Add(e.Message); }
            }
        });

        await Task.WhenAll(savers);
        await stop.CancelAsync();
        await reader;

        Assert.Empty(failures);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_paths.ConfigFile)!, "*.tmp"));
        AssertUsable(NewStore().Load());
    }

    // ---- randomized round trip and mutation fuzz ----

    private static AppSettings RandomSettings(Random r)
    {
        var keys = new[] { "F1", "F2", "F6", "F7", "F8", "F9", "F10", "F12", "Q", "W", "E", "R", "T", "U", "O", "P", "A", "S", "G", "H", "J", "K", "L", "X", "V", "B", "N" };
        var pool = new Queue<string>(keys.OrderBy(_ => r.Next()));
        string Key() => pool.Dequeue();
        var text = new[] { "", "Group-2", "Đích \u65E5\u672C\u8A9E", "\u0645\u062C\u0644\u062F", "tr\u0130\u0131", "a\"b\\c", "{name}", "line\nbreak", new string('x', 500) };
        string Text() => text[r.Next(text.Length)];
        var s = new AppSettings
        {
            InitialViewMode = (InitialViewMode)r.Next(0, 4),
            LoadingMode = Enum.GetValues<LoadingMode>()[r.Next(Enum.GetValues<LoadingMode>().Length)],
            LoggingEnabled = r.Next(2) == 0,
            ImageSortMode = Enum.GetValues<ImageSortMode>()[r.Next(Enum.GetValues<ImageSortMode>().Length)],
            CompareHashEnabled = r.Next(2) == 0,
            CompareSizeEnabled = r.Next(2) == 0,
            ImageCacheCapacityBytes = 1 + r.NextInt64(long.MaxValue - 1),
            ImageCacheRamPercent = r.Next(1, 91),
            MemoryReserveBytes = r.NextInt64(long.MaxValue),
            PreloadWorkerCount = r.Next(1, PerformanceOptions.MaxPreloadWorkerCount + 1),
            PreloadMemoryLoadLimit = Math.Max(0.01, r.NextDouble()),
            PreviewDiskCacheCapacityBytes = r.NextInt64(long.MaxValue),
            UseSourceBytesCache = r.Next(2) == 0,
            SourceBytesCapacityBytes = 1 + r.NextInt64(long.MaxValue - 1),
            UiLanguage = new[] { "auto", "en", "vi", "pt-br" }[r.Next(4)],
            AllowPermanentDeleteWithoutRecycleBin = r.Next(2) == 0,
            LastMoveToFolder = r.Next(3) == 0 ? null : Text(),
            LastCopyToFolder = r.Next(3) == 0 ? null : Text(),
            MoveCopyReuseLastFolder = r.Next(2) == 0,
            ShowInfoOverlay = r.Next(2) == 0,
            ShowFileInfo = r.Next(2) == 0,
            ShowFolderInfo = r.Next(2) == 0,
            ShowExifInfo = r.Next(2) == 0,
            ExifInfoFields = (ExifInfoFields)r.Next(0, 512),
            MouseWheelAction = Enum.GetValues<MouseWheelAction>()[r.Next(Enum.GetValues<MouseWheelAction>().Length)],
            ClickToZoomEnabled = r.Next(2) == 0,
            ClickZoomPercent = r.Next(AppSettings.MinClickZoomPercent, AppSettings.MaxClickZoomPercent + 1),
            KineticPanEnabled = r.Next(2) == 0,
            Shortcuts = new ShortcutMappings
            {
                Next = Key(), Previous = Key(), MoveToFolder2 = Key(), SendToRecycleBin = Key(), Compare = Key(),
                NextFolder = Key(), PreviousFolder = Key(), FirstImage = Key(), ZoomIn = Key(), ZoomOut = Key(),
                ToggleFit = Key(), Skip = Key(), Undo = Key(), Fullscreen = Key(),
                MoveToFolder = r.Next(3) == 0 ? "" : Key(), CopyToFolder = r.Next(3) == 0 ? "" : Key(),
                LastImage = r.Next(3) == 0 ? "" : Key(), ZoomActualSize = r.Next(3) == 0 ? "" : Key(),
                ToggleInfoOverlay = r.Next(3) == 0 ? "" : Key(),
            },
            Actions = [],
        };
        var actionCount = r.Next(0, 4);
        for (var i = 0; i < actionCount; i++)
        {
            s.Actions.Add(new ReviewAction
            {
                Name = "Act" + i + Text(),
                Shortcut = Key(),
                Operation = r.Next(2) == 0 ? FileOperationType.Move : FileOperationType.Copy,
                Destination = Text(),
                Confirm = r.Next(2) == 0,
            });
        }
        return s;
    }

    private static string Json(AppSettings s) => JsonSerializer.Serialize(s);

    [Theory(DisplayName = "Fuzz: Save then Load round-trips every setting")]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    public void Fuzz_SaveLoad_RoundTrips(int seed)
    {
        var r = new Random(seed);
        for (var i = 0; i < 25; i++)
        {
            var original = RandomSettings(r);
            var store = NewStore();
            store.Save(original);

            var reloaded = NewStore().Load();

            Assert.Equal(Json(original), Json(reloaded));
        }
    }

    [Theory(DisplayName = "Fuzz: Normalize is idempotent and Clone equals its source")]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    public void Fuzz_NormalizeIdempotent_CloneEqual(int seed)
    {
        var r = new Random(seed);
        for (var i = 0; i < 40; i++)
        {
            var s = RandomSettings(r);
            s.ClickZoomPercent = r.Next(-1000, 2000);
            s.ImageCacheRamPercent = r.Next(-50, 500);
            s.PreloadWorkerCount = r.Next(-5, 5000);
            s.PreloadMemoryLoadLimit = new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 0, 0.5, 1, 2 }[r.Next(8)];
            s.ImageCacheCapacityBytes = r.Next(-5, 5);

            SettingsNormalizer.Normalize(s);
            AssertUsable(s);
            var once = Json(s);
            var second = SettingsNormalizer.Normalize(s);

            Assert.Empty(second);
            Assert.Equal(once, Json(s));
            Assert.Equal(once, Json(AppSettings.Clone(s)));
        }
    }

    private static readonly string[] Tokens =
    [
        "null", "true", "false", "0", "-1", "1e999", "-1e999", "1.5", "9223372036854775807", "99999999999999999999",
        "\"\"", "\"x\"", "\"4\"", "\"NaN\"", "\"Infinity\"", "[]", "{}", "[null]", "{\"a\":1}", "\"\\ud800\"",
    ];

    /// <summary>Mutates one leaf or property of the JSON: replaces its value, removes it, or duplicates the property.</summary>
    private static string Mutate(string json, Random r)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var names = node.Select(kv => kv.Key).ToList();
        var target = names[r.Next(names.Count)];
        var token = Tokens[r.Next(Tokens.Length)];
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var kv in node)
        {
            if (kv.Key == target)
            {
                switch (r.Next(3))
                {
                    case 0: Append(kv.Key, token); break;               // replace
                    case 1: break;                                      // remove
                    default: Append(kv.Key, kv.Value?.ToJsonString() ?? "null"); Append(kv.Key, token); break; // duplicate
                }
            }
            else
            {
                Append(kv.Key, kv.Value?.ToJsonString() ?? "null");
            }
        }
        return sb.Append('}').ToString();

        void Append(string key, string value)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(JsonSerializer.Serialize(key)).Append(':').Append(value);
        }
    }

    [Theory(DisplayName = "Fuzz: a config with any one property mutated never throws on Load and yields usable settings")]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(35)]
    [InlineData(36)]
    public void Fuzz_MutatedProperty_LoadsUsable(int seed)
    {
        var r = new Random(seed);
        for (var i = 0; i < 60; i++)
        {
            var valid = JsonSerializer.Serialize(RandomSettings(r));
            var mutated = Mutate(valid, r);

            AppSettings s = null!;
            var ex = Record.Exception(() => s = LoadJson(mutated));

            Assert.True(ex is null, mutated + "\n" + ex);
            AssertUsable(s);
        }
    }

    [Fact(DisplayName = "Fuzz: a mistyped value in one property does not throw away the user's actions and shortcuts")]
    public void Load_OneMistypedProperty_KeepsTheRest()
    {
        var s = LoadJson("""
            {"ConfigVersion":3,"LoggingEnabled":"not-a-bool-but-string","ClickZoomPercent":300,
             "Actions":[{"Name":"Mine","Shortcut":"F9","Operation":"Copy","Destination":"D"}],
             "Shortcuts":{"Next":"N"}}
            """);

        Assert.Equal(300, s.ClickZoomPercent);
        Assert.Equal("Mine", Assert.Single(s.Actions).Name);
        Assert.Equal("N", s.Shortcuts.Next);
    }

    [Fact(DisplayName = "Extra reflection guard: every AppSettings property is covered by the JSON contract")]
    public void EveryPublicSettingRoundTripsThroughClone()
    {
        var r = new Random(99);
        var source = RandomSettings(r);
        var clone = AppSettings.Clone(source);

        foreach (var p in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            var a = JsonSerializer.Serialize(p.GetValue(source), p.PropertyType);
            var b = JsonSerializer.Serialize(p.GetValue(clone), p.PropertyType);
            Assert.True(a == b, p.Name);
        }
    }
}
