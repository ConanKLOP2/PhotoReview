using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Session;

/// <summary>Mutation-testing gaps in <see cref="SessionStore"/>: stale-temp age boundary and folder canonicalisation.</summary>
public sealed class SessionStoreMutationGapTests
{
    private sealed class Paths : PhotoReview.Core.Abstractions.IAppPaths
    {
        public string ConfigFile => @"C:\data\config.json";
        public string JournalFile => @"C:\data\operations.jsonl";
        public string SessionsDir => @"C:\data\Sessions";
        public string LogFile => @"C:\data\logs\app.log";
        public string PreviewCacheDir => @"C:\data\cache";
        public string ThumbnailCacheDir => @"C:\data\thumbnails";
        public string WindowPlacementFile => @"C:\data\window-placement.json";
    }

    [Fact]
    public async Task SweepStaleTempFiles_TempExactlyOneDayOld_IsKeptAndOlderOneIsRemoved()
    {
        var fs = new InMemoryFileSystem();
        var store = new SessionStore(new Paths(), fs);
        await store.StartupSweepTask; // the real-clock startup sweep must be over before the fixture is added
        var now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        fs.AddFile(@"C:\data\Sessions\exactly-a-day.tmp", "x", now - TimeSpan.FromDays(1));
        fs.AddFile(@"C:\data\Sessions\older.tmp", "x", now - TimeSpan.FromDays(1) - TimeSpan.FromTicks(1));

        var removed = store.SweepStaleTempFiles(now);

        Assert.Equal(1, removed);
        Assert.True(fs.FileExists(@"C:\data\Sessions\exactly-a-day.tmp"));
        Assert.False(fs.FileExists(@"C:\data\Sessions\older.tmp"));
    }

    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\photos", @"C:\photos")]
    [InlineData(@"C:\photos\", @"C:\photos")]
    [InlineData(@"C:\photos\\", @"C:\photos")]
    public void CanonicalFolder_TrimsTrailingSeparatorsExceptOnADriveRoot(string folder, string expected)
    {
        Assert.Equal(expected, SessionStore.CanonicalFolder(folder));
    }

    [Fact]
    public void GetPath_FolderWithAndWithoutTrailingSeparator_IsTheSameFileButDriveRootDiffersFromItsChild()
    {
        var store = new SessionStore(new Paths(), new InMemoryFileSystem());

        Assert.Equal(store.GetPath(@"C:\photos"), store.GetPath(@"C:\photos\"));
        Assert.NotEqual(store.GetPath(@"C:\"), store.GetPath(@"C:\photos"));
    }
}