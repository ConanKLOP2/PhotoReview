using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Json;

/// <summary>
/// WP-11 format lock: the bytes the app writes to session/journal/config files and the files it reads back must stay exactly
/// what the reflection-based serializers wrote before the source-generated contexts replaced them. Expected texts are the
/// previous build's output (indent, property order, key names, enum text, escaping of non-ASCII, DateTime form).
/// </summary>
public sealed class JsonFormatCompatTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class FixedClock(DateTime utc) : IClock
    {
        public DateTime UtcNow { get; } = utc;
    }

    private sealed class FakeAppPaths(string sessionsDir) : IAppPaths
    {
        public string ConfigFile => @"C:\data\config.json";
        public string JournalFile => @"C:\data\operations.jsonl";
        public string SessionsDir { get; } = sessionsDir;
        public string LogFile => @"C:\data\logs\app.log";
        public string PreviewCacheDir => @"C:\data\cache";
        public string ThumbnailCacheDir => @"C:\data\thumbnails";
        public string WindowPlacementFile => @"C:\data\window-placement.json";
    }

    // ---- Session (SessionStore: WriteIndented, default encoder, DateTime as ISO-8601 'Z') ----

    [Fact(DisplayName = "Session file bytes match the previous build (indent, key order, escaping, UTC timestamp)")]
    public void SessionFile_Bytes_MatchPreviousBuild()
    {
        var fs = new InMemoryFileSystem();
        var store = new SessionStore(new FakeAppPaths(@"C:\data\Sessions"), fs);
        var folder = @"C:\photos\vacation";

        store.Save(new SessionState
        {
            Folder = folder,
            CurrentPath = @"C:\photos\vacation\đại.jpg",
            Skipped = [@"C:\photos\vacation\skip1.jpg"],
            UpdatedUtc = new DateTime(2026, 9, 18, 10, 30, 0, DateTimeKind.Utc),
        });

        const string expected = "{\n" +
            "  \"Folder\": \"C:\\\\photos\\\\vacation\",\n" +
            "  \"CurrentPath\": \"C:\\\\photos\\\\vacation\\\\\\u0111\\u1EA1i.jpg\",\n" +
            "  \"Skipped\": [\n" +
            "    \"C:\\\\photos\\\\vacation\\\\skip1.jpg\"\n" +
            "  ],\n" +
            "  \"UpdatedUtc\": \"2026-09-18T10:30:00Z\"\n" +
            "}";
        Assert.Equal(expected.Replace("\n", Environment.NewLine, StringComparison.Ordinal), fs.ReadAllText(store.GetPath(folder)));
    }

    [Fact(DisplayName = "Session file written by the previous build loads with the same values")]
    public void SessionFile_LegacyText_Loads()
    {
        var fs = new InMemoryFileSystem();
        var store = new SessionStore(new FakeAppPaths(@"C:\data\Sessions"), fs);
        var folder = @"C:\photos\vacation";
        const string legacy = "{\n  \"Folder\": \"C:\\\\photos\\\\vacation\",\n  \"CurrentPath\": \"C:\\\\photos\\\\vacation\\\\img2.jpg\",\n" +
            "  \"Skipped\": [\n    \"C:\\\\photos\\\\vacation\\\\s.jpg\"\n  ],\n  \"UpdatedUtc\": \"2026-09-18T10:30:00Z\"\n}";
        fs.AddFile(store.GetPath(folder), legacy);

        var loaded = store.Load(folder);

        Assert.Equal(folder, loaded.Folder);
        Assert.Equal(@"C:\photos\vacation\img2.jpg", loaded.CurrentPath);
        Assert.Equal([@"C:\photos\vacation\s.jpg"], loaded.Skipped);
        Assert.Equal(new DateTime(2026, 9, 18, 10, 30, 0, DateTimeKind.Utc), loaded.UpdatedUtc);
    }

    [Fact(DisplayName = "Session file without Skipped or Folder loads with defaults")]
    public void SessionFile_MissingOptionalKeys_LoadsWithDefaults()
    {
        var fs = new InMemoryFileSystem();
        var store = new SessionStore(new FakeAppPaths(@"C:\data\Sessions"), fs);
        var folder = @"C:\photos\other";
        fs.AddFile(store.GetPath(folder), "{\"CurrentPath\":\"C:\\\\photos\\\\other\\\\a.jpg\"}");

        var loaded = store.Load(folder);

        Assert.Equal(folder, loaded.Folder);
        Assert.Equal(@"C:\photos\other\a.jpg", loaded.CurrentPath);
        Assert.Empty(loaded.Skipped);
    }

    // ---- Journal (OperationJournal.Append: default options, JsonIgnore(WhenWritingNull), enums as names) ----

    [Fact(DisplayName = "Journal line bytes match the previous build (no indent, null members written, optional members only when set)")]
    public void JournalLine_Bytes_MatchPreviousBuild()
    {
        var fs = new InMemoryFileSystem();
        var paths = new AppPaths(@"C:\Users\test\AppData\Local");
        var journal = new OperationJournal(paths, fs, new FixedClock(T0));

        journal.Append(new JournalEntry("id-1", FileOperationType.Move, JournalState.Committed, @"C:\old\a.jpg", @"C:\sel\a.jpg", 10, T0, T0,
            Permanent: true));

        var expected = "{\"Id\":\"id-1\",\"Type\":\"Move\",\"State\":\"Committed\",\"Source\":\"C:\\\\old\\\\a.jpg\"," +
            "\"Destination\":\"C:\\\\sel\\\\a.jpg\",\"Size\":10,\"LastWriteUtc\":\"2026-09-01T00:00:00Z\"," +
            "\"TimestampUtc\":\"2026-09-01T00:00:00Z\",\"Error\":null,\"Permanent\":true}" + Environment.NewLine;
        Assert.Equal(expected, fs.ReadAllText(paths.JournalFile));
    }

    [Fact(DisplayName = "Journal line written by the previous build parses to the same entry, group members included")]
    public void JournalLine_LegacyGroupLine_Parses()
    {
        const string legacy = "{\"Id\":\"g-1\",\"Type\":\"Copy\",\"State\":\"Prepared\",\"Source\":\"C:\\\\a\\\\x.jpg\",\"Destination\":null," +
            "\"Size\":5,\"LastWriteUtc\":\"2026-09-01T00:00:00Z\",\"TimestampUtc\":\"2026-09-01T00:00:00Z\",\"GroupId\":\"grp\"," +
            "\"GroupMembers\":[{\"Source\":\"C:\\\\a\\\\x.jpg\",\"Destination\":\"C:\\\\b\\\\x.jpg\",\"Size\":5," +
            "\"LastWriteUtc\":\"2026-09-01T00:00:00Z\",\"Permanent\":false}]}";

        var entry = JournalLineParser.TryParse(legacy);

        Assert.NotNull(entry);
        Assert.Equal("g-1", entry.Id);
        Assert.Equal(FileOperationType.Copy, entry.Type);
        Assert.Equal(JournalState.Prepared, entry.State);
        Assert.Equal("grp", entry.GroupId);
        var member = Assert.Single(entry.GroupMembers!);
        Assert.Equal(@"C:\b\x.jpg", member.Destination);
        Assert.Equal(5, member.Size);
    }

    [Fact(DisplayName = "Journal line written by Append is read back as the same entry")]
    public void JournalLine_AppendThenParse_RoundTrips()
    {
        var fs = new InMemoryFileSystem();
        var paths = new AppPaths(@"C:\Users\test\AppData\Local");
        var journal = new OperationJournal(paths, fs, new FixedClock(T0));
        var written = new JournalEntry("rt-1", FileOperationType.Move, JournalState.Prepared, @"C:\old\ç.jpg", null, 7, T0, T0, Undo: true, GroupId: "g");

        journal.Append(written);
        var line = fs.ReadAllText(paths.JournalFile).TrimEnd('\r', '\n');

        Assert.Equal(written, JournalLineParser.TryParse(line));
    }

    // ---- Config (SettingsStore: AppSettingsJsonContext, WriteIndented, lenient enums) ----

    [Fact(DisplayName = "config.json written by the previous build keeps its indent and key names after a save")]
    public void ConfigFile_SaveAfterLegacyLoad_KeepsFormat()
    {
        var fs = new InMemoryFileSystem();
        var paths = new AppPaths(@"C:\Users\test\AppData\Local");
        var store = new SettingsStore(paths, fs, NullLog.Instance);
        const string legacy = "{\n  \"ConfigVersion\": 3,\n  \"UiLanguage\": \"en\"\n}";

        var parsed = SettingsStore.ParseText(legacy).Settings;
        store.Save(parsed);
        var written = fs.ReadAllText(paths.ConfigFile).Replace(Environment.NewLine, "\n", StringComparison.Ordinal);

        Assert.StartsWith("{\n  \"", written, StringComparison.Ordinal);
        Assert.Contains("\n  \"ConfigVersion\": 3,", written, StringComparison.Ordinal);
        Assert.Contains("\n  \"UiLanguage\": \"en\"", written, StringComparison.Ordinal);
        var reread = SettingsStore.ParseText(written).Settings;
        Assert.Equal("en", reread.UiLanguage);
        Assert.Equal(AppSettings.CurrentConfigVersion, reread.ConfigVersion);
    }
}
