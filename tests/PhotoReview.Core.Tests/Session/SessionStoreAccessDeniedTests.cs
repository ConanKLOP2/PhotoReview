using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Session;

/// <summary>
/// <see cref="SessionStore"/> against permission and IO failures from the file system: the session is "no session", never an exception
/// (Load), and the start-up sweep of stale temp files is best effort per file.
/// </summary>
public sealed class SessionStoreAccessDeniedTests
{
    private const string Folder = @"C:\photos\shoot";
    private const string SessionsDir = @"C:\data\Sessions";

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

    [Theory(DisplayName = "Load: a session file that cannot be read (IO error / access denied) is no session, with the folder kept")]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Load_ReadFails_ReturnsAnEmptySessionForTheFolder(Type exceptionType)
    {
        var fs = new InMemoryFileSystem();
        var store = new SessionStore(new Paths(), fs);
        store.Save(new SessionState { Folder = Folder, CurrentPath = Folder + @"\a.jpg", Skipped = [Folder + @"\b.jpg"] });
        Assert.Equal(Folder + @"\a.jpg", store.Load(Folder).CurrentPath); // precondition: the session is really on "disk"
        fs.OpenReadHook = _ => (Exception)Activator.CreateInstance(exceptionType, "denied")!;

        var loaded = store.Load(Folder);

        Assert.Equal(Folder, loaded.Folder);
        Assert.Null(loaded.CurrentPath);
        Assert.Empty(loaded.Skipped);
    }

    [Fact(DisplayName = "Sweep: access denied while listing the sessions directory removes nothing and does not throw")]
    public async Task Sweep_ListingDenied_ReturnsZero()
    {
        var fs = new InMemoryFileSystem { EnumerateFilesHook = _ => new UnauthorizedAccessException("denied") };
        var store = new SessionStore(new Paths(), fs);
        await store.StartupSweepTask; // the constructor sweep itself must not fault
        fs.AddFile(SessionsDir + @"\old.tmp", "x", DateTime.UtcNow.AddDays(-3));

        var removed = store.SweepStaleTempFiles(DateTime.UtcNow);

        Assert.Equal(0, removed);
        Assert.True(fs.FileExists(SessionsDir + @"\old.tmp"));
    }

    [Fact(DisplayName = "Sweep: access denied deleting one stale temp file skips it and still removes the others")]
    public async Task Sweep_DeleteDeniedForOneFile_OthersStillRemoved()
    {
        var fs = new InMemoryFileSystem();
        var store = new SessionStore(new Paths(), fs);
        await store.StartupSweepTask;
        var old = DateTime.UtcNow.AddDays(-3);
        fs.AddFile(SessionsDir + @"\a.tmp", "x", old);
        fs.AddFile(SessionsDir + @"\b.tmp", "x", old);
        fs.DeleteHook = p => p.EndsWith("a.tmp", StringComparison.OrdinalIgnoreCase) ? new UnauthorizedAccessException("denied") : null;

        var removed = store.SweepStaleTempFiles(DateTime.UtcNow);

        Assert.Equal(1, removed);
        Assert.True(fs.FileExists(SessionsDir + @"\a.tmp"));
        Assert.False(fs.FileExists(SessionsDir + @"\b.tmp"));
    }
}
