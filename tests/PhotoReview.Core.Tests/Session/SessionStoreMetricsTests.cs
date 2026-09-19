using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Session;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Session;

public sealed class SessionStoreMetricsTests
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

    [Fact(DisplayName = "Every Save is counted as a session write; Load is not")]
    public void SaveIsCountedLoadIsNot()
    {
        var metrics = new ReviewMetrics();
        var store = new SessionStore(new Paths(), new InMemoryFileSystem(), metrics);
        var state = new SessionState { Folder = @"C:\photos", CurrentPath = @"C:\photos\a.jpg" };

        store.Save(state);
        store.Save(state);
        store.Load(@"C:\photos");

        Assert.Equal(2, metrics.Snapshot().SessionWriteCount);
    }

    [Fact(DisplayName = "SessionStore works without metrics")]
    public void SaveWithoutMetricsStillWorks()
    {
        var store = new SessionStore(new Paths(), new InMemoryFileSystem());

        store.Save(new SessionState { Folder = @"C:\photos" });

        Assert.Equal(@"C:\photos", store.Load(@"C:\photos").Folder);
    }
}
