using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Session;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Session;

public sealed class SessionStoreTests
{
    private sealed class FakeAppPaths : IAppPaths
    {
        public FakeAppPaths(string sessionsDir) => SessionsDir = sessionsDir;
        public string ConfigFile => @"C:\data\config.json";
        public string JournalFile => @"C:\data\operations.jsonl";
        public string SessionsDir { get; }
        public string LogFile => @"C:\data\logs\app.log";
        public string PreviewCacheDir => @"C:\data\cache";
        public string ThumbnailCacheDir => @"C:\data\thumbnails";
        public string WindowPlacementFile => @"C:\data\window-placement.json";
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly string _sessionsDir = @"C:\data\Sessions";

    private SessionStore CreateStore() =>
        new(new FakeAppPaths(_sessionsDir), _fs);

    [Fact(DisplayName = "Session save and load round-trips correctly")]
    public void SaveAndLoad_RoundTrips()
    {
        var store = CreateStore();
        var folder = @"C:\photos\vacation";
        var state = new SessionState
        {
            Folder = folder,
            CurrentPath = @"C:\photos\vacation\img1.jpg",
            Skipped = [@"C:\photos\vacation\skip1.jpg", @"C:\photos\vacation\skip2.jpg"],
            UpdatedUtc = new DateTime(2026, 9, 18, 10, 30, 0, DateTimeKind.Utc)
        };

        store.Save(state);
        var loaded = store.Load(folder);

        Assert.Equal(folder, loaded.Folder);
        Assert.Equal(state.CurrentPath, loaded.CurrentPath);
        Assert.Equal(2, loaded.Skipped.Count);
        Assert.Contains(@"C:\photos\vacation\skip1.jpg", loaded.Skipped);
        Assert.Contains(@"C:\photos\vacation\skip2.jpg", loaded.Skipped);
    }

    [Fact(DisplayName = "Load returns default session state when file does not exist")]
    public void Load_MissingFile_ReturnsDefault()
    {
        var store = CreateStore();
        var folder = @"C:\photos\nonexistent";

        var loaded = store.Load(folder);

        Assert.NotNull(loaded);
        Assert.Equal(folder, loaded.Folder);
        Assert.Null(loaded.CurrentPath);
        Assert.Empty(loaded.Skipped);
    }

    [Fact(DisplayName = "Load returns default session state when JSON is corrupted")]
    public void Load_CorruptJson_ReturnsDefault()
    {
        var store = CreateStore();
        var folder = @"C:\photos\corrupt";
        var path = store.GetPath(folder);

        _fs.WriteAllTextAtomic(path, "{ invalid json content");

        var loaded = store.Load(folder);

        Assert.NotNull(loaded);
        Assert.Equal(folder, loaded.Folder);
        Assert.Null(loaded.CurrentPath);
        Assert.Empty(loaded.Skipped);
    }

    [Fact(DisplayName = "Load assigns Folder when JSON lacks Folder property")]
    public void Load_JsonWithoutFolder_AssignsFolder()
    {
        var store = CreateStore();
        var folder = @"C:\photos\nofolder";
        var path = store.GetPath(folder);

        var jsonWithoutFolder = """
        {
            "CurrentPath": "C:\\photos\\nofolder\\test.jpg",
            "Skipped": ["C:\\photos\\nofolder\\skip.jpg"],
            "UpdatedUtc": "2026-09-18T00:00:00Z"
        }
        """;
        _fs.WriteAllTextAtomic(path, jsonWithoutFolder);

        var loaded = store.Load(folder);

        Assert.Equal(folder, loaded.Folder);
        Assert.Equal(@"C:\photos\nofolder\test.jpg", loaded.CurrentPath);
        Assert.Single(loaded.Skipped);
    }

    [Fact(DisplayName = "Save creates Sessions directory if absent")]
    public void Save_CreatesSessionsDirectory()
    {
        var store = CreateStore();
        Assert.False(_fs.DirectoryExists(_sessionsDir));

        var folder = @"C:\photos\newdir";
        store.Save(new SessionState { Folder = folder });

        Assert.True(_fs.DirectoryExists(_sessionsDir));
    }

    [Fact(DisplayName = "GetPath is case-insensitive and ignores trailing slashes")]
    public void GetPath_Canonicalization()
    {
        var store = CreateStore();

        var path1 = store.GetPath(@"C:\Photos\Album");
        var path2 = store.GetPath(@"c:\photos\album\");
        var path3 = store.GetPath(@"C:\photos\album");

        Assert.Equal(path1, path2);
        Assert.Equal(path1, path3);
    }

    [Fact(DisplayName = "Constructor throws on null arguments")]
    public void Constructor_NullValidation()
    {
        var paths = new FakeAppPaths(_sessionsDir);
        Assert.Throws<ArgumentNullException>(() => new SessionStore(null!, _fs));
        Assert.Throws<ArgumentNullException>(() => new SessionStore(paths, null!));
    }
}
