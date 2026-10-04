using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Session;

/// <summary>Diagnostic-log behaviour of <see cref="SessionStore"/> (mutation-testing gaps): warnings are written and a failing logger never breaks Load/Save.</summary>
public sealed class SessionStoreLogTests
{
    private const string Folder = @"C:\photos\shoot";

    private sealed class Paths : IAppPaths
    {
        public string ConfigFile => @"C:\data\config.json";
        public string JournalFile => @"C:\data\operations.jsonl";
        public string SessionsDir => @"C:\data\Sessions";
        public string LogFile => @"C:\data\logs\app.log";
        public string PreviewCacheDir => @"C:\data\cache";
        public string ThumbnailCacheDir => @"C:\data\thumbnails";
        public string WindowPlacementFile => @"C:\data\window-placement.json";
    }

    private sealed class RecordingLog(Exception? toThrow = null) : ILog
    {
        public List<string> Warnings { get; } = [];
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message)
        {
            Warnings.Add(message);
            if (toThrow is not null) throw toThrow;
        }
        public void Error(string message, Exception? ex = null) { }
    }

    private static SessionStore NewStore(InMemoryFileSystem fs, ILog log) => new(new Paths(), fs) { Log = () => log };

    [Fact]
    public void Save_WriteFails_DoesNotThrowAndWarnsWithFolderAndReason()
    {
        var log = new RecordingLog();
        var store = NewStore(new InMemoryFileSystem { WriteHook = _ => new IOException("disk full") }, log);

        store.Save(new SessionState { Folder = Folder });

        var warning = Assert.Single(log.Warnings);
        Assert.Contains(Folder, warning);
        Assert.Contains("disk full", warning);
    }

    [Fact]
    public void Save_WriteFailsAndLoggerThrows_StillDoesNotThrow()
    {
        var store = NewStore(new InMemoryFileSystem { WriteHook = _ => new IOException("disk full") },
            new RecordingLog(new InvalidOperationException("log broken")));

        store.Save(new SessionState { Folder = Folder });
    }

    [Fact]
    public void Save_WriteFailsAndLoggerRunsOutOfMemory_PropagatesIt()
    {
        var store = NewStore(new InMemoryFileSystem { WriteHook = _ => new IOException("disk full") },
            new RecordingLog(new InsufficientMemoryException()));

        Assert.ThrowsAny<OutOfMemoryException>(() => store.Save(new SessionState { Folder = Folder }));
    }

    private static InMemoryFileSystem FsWithCorruptSession(SessionStore probe)
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(probe.GetPath(Folder), "{ not json", DateTime.UtcNow);
        return fs;
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptySessionAndWarnsWithThePath()
    {
        var probe = new SessionStore(new Paths(), new InMemoryFileSystem());
        var log = new RecordingLog();
        var store = NewStore(FsWithCorruptSession(probe), log);

        var state = store.Load(Folder);

        Assert.Equal(Folder, state.Folder);
        Assert.Empty(state.Skipped);
        Assert.Contains(probe.GetPath(Folder), Assert.Single(log.Warnings));
    }

    [Fact]
    public void Load_CorruptFileAndLoggerThrows_StillReturnsEmptySession()
    {
        var probe = new SessionStore(new Paths(), new InMemoryFileSystem());
        var store = NewStore(FsWithCorruptSession(probe), new RecordingLog(new InvalidOperationException("log broken")));

        Assert.Equal(Folder, store.Load(Folder).Folder);
    }

    [Fact]
    public void Load_CorruptFileAndLoggerRunsOutOfMemory_PropagatesIt()
    {
        var probe = new SessionStore(new Paths(), new InMemoryFileSystem());
        var store = NewStore(FsWithCorruptSession(probe), new RecordingLog(new InsufficientMemoryException()));

        Assert.ThrowsAny<OutOfMemoryException>(() => store.Load(Folder));
    }
}
