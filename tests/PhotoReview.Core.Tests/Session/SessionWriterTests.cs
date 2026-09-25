using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Session;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Session;

[Trait("Category", "HotPath")]
public sealed class SessionWriterTests
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

    /// <summary>A timer the test fires by hand.</summary>
    private sealed class ManualDelay
    {
        private readonly List<TaskCompletionSource> _waiters = [];
        public int Requests => _waiters.Count;

        public Task Delay(TimeSpan _, CancellationToken token)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => tcs.TrySetCanceled(token));
            _waiters.Add(tcs);
            return tcs.Task;
        }

        public void FireAll() { foreach (var w in _waiters) w.TrySetResult(); }
    }

    private readonly ReviewMetrics _metrics = new();
    private readonly InMemoryFileSystem _fs = new();
    private readonly ManualDelay _timer = new();

    private (SessionWriter Writer, SessionStore Store) Create()
    {
        var store = new SessionStore(new Paths(), _fs, _metrics);
        return (new SessionWriter(store, delay: _timer.Delay), store);
    }

    private static SessionState State(string folder, string current, params string[] skipped) =>
        new() { Folder = folder, CurrentPath = current, Skipped = [.. skipped] };

    [Fact(DisplayName = "100 updates within the debounce window cost one write")]
    public async Task ManyUpdatesCoalesceIntoOneWrite()
    {
        var (writer, store) = Create();
        for (var i = 0; i < 100; i++) writer.Update(State(@"C:\photos", $@"C:\photos\img{i}.jpg"));

        Assert.Equal(0, _metrics.Snapshot().SessionWriteCount);
        Assert.Equal(1, _timer.Requests);

        _timer.FireAll();
        await writer.WhenIdleAsync();

        Assert.Equal(1, _metrics.Snapshot().SessionWriteCount);
        Assert.Equal(@"C:\photos\img99.jpg", store.Load(@"C:\photos").CurrentPath);
    }

    [Fact(DisplayName = "An update after the write schedules a new write")]
    public async Task UpdateAfterWriteStartsNewWindow()
    {
        var (writer, _) = Create();
        writer.Update(State(@"C:\photos", "a"));
        _timer.FireAll();
        await writer.WhenIdleAsync();

        writer.Update(State(@"C:\photos", "b"));

        Assert.Equal(2, _timer.Requests);
        await writer.FlushAsync();
        Assert.Equal(2, _metrics.Snapshot().SessionWriteCount);
    }

    [Fact(DisplayName = "A newer snapshot wins when an older timer batch is already releasing")]
    public async Task NewerSnapshotWinsOverOlderTimerBatch()
    {
        var (writer, store) = Create();
        writer.Update(State(@"C:\photos", "old"));

        // Release the debounce continuation and immediately publish the newer state.
        // The writer must serialize the two batches so the old snapshot cannot remain
        // on disk after the newer update has been accepted.
        _timer.FireAll();
        writer.Update(State(@"C:\photos", "new"));

        await writer.FlushAsync();
        await writer.WhenIdleAsync();

        Assert.Equal("new", store.Load(@"C:\photos").CurrentPath);
    }

    [Fact(DisplayName = "Flush writes pending state immediately (shutdown) and the cancelled timer does not write again")]
    public async Task FlushWritesImmediatelyAndOnlyOnce()
    {
        var (writer, store) = Create();
        writer.Update(State(@"C:\photos", @"C:\photos\last.jpg"));

        writer.Flush();
        _timer.FireAll();
        await writer.WhenIdleAsync();

        Assert.Equal(1, _metrics.Snapshot().SessionWriteCount);
        Assert.Equal(@"C:\photos\last.jpg", store.Load(@"C:\photos").CurrentPath);
    }

    [Fact(DisplayName = "Flush before opening another folder persists the old folder's session")]
    public void FlushOnFolderChangePersistsOldFolder()
    {
        var (writer, store) = Create();
        writer.Update(State(@"C:\old", @"C:\old\5.jpg", @"C:\old\skip.jpg"));

        writer.Flush(); // what a folder change does before loading the next folder
        var loaded = store.Load(@"C:\old");

        Assert.Equal(@"C:\old\5.jpg", loaded.CurrentPath);
        Assert.Equal([@"C:\old\skip.jpg"], loaded.Skipped);
    }

    [Fact(DisplayName = "Pending states for different folders are all written")]
    public void EachFolderKeepsItsOwnPendingState()
    {
        var (writer, store) = Create();
        writer.Update(State(@"C:\a", "a1"));
        writer.Update(State(@"C:\b", "b1"));
        writer.Update(State(@"C:\a", "a2"));

        writer.Flush();

        Assert.Equal("a2", store.Load(@"C:\a").CurrentPath);
        Assert.Equal("b1", store.Load(@"C:\b").CurrentPath);
        Assert.Equal(2, _metrics.Snapshot().SessionWriteCount);
    }

    [Fact(DisplayName = "Later mutation of the caller's state does not change what was recorded")]
    public void UpdateCopiesTheState()
    {
        var (writer, store) = Create();
        var state = State(@"C:\photos", "first", "s1");
        writer.Update(state);
        state.CurrentPath = "mutated";
        state.Skipped.Add("s2");

        writer.Flush();
        var loaded = store.Load(@"C:\photos");

        Assert.Equal("first", loaded.CurrentPath);
        Assert.Equal(["s1"], loaded.Skipped);
    }

    [Fact(DisplayName = "Dispose flushes pending state")]
    public void DisposeFlushes()
    {
        var (writer, store) = Create();
        writer.Update(State(@"C:\photos", "x"));

        writer.Dispose();

        Assert.Equal("x", store.Load(@"C:\photos").CurrentPath);
    }

    [Fact(DisplayName = "Dispose returns within its bound while a write is in flight and skips the last write")]
    public async Task Dispose_ReturnsWithinBound_WhenWriteInFlight()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var (writer, store) = Create();
        _fs.WriteHook = _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(60));
            return null;
        };

        writer.Update(State(@"C:\photos", "a"));
        _timer.FireAll();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the debounced write never started");
        writer.Update(State(@"C:\photos", "b")); // pending while the write for "a" is blocked

        var dispose = Task.Run(writer.Dispose);
        var completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(20)));
        release.Set();
        Assert.Same(dispose, completed);

        await dispose;
        await writer.WhenIdleAsync();
        _fs.WriteHook = null;
        // The in-flight write of "a" finishes on the timer thread after release.Set(); WhenIdleAsync does not cover it.
        await Wait.UntilAsync(() => store.Load(@"C:\photos").CurrentPath == "a", "the in-flight write of a to land");
        Assert.Equal("a", store.Load(@"C:\photos").CurrentPath); // "b" was skipped, not written
        writer.Dispose(); // double Dispose is a no-op
    }

    [Fact(DisplayName = "Dispose while the timer thread holds a drained batch does not lose that write (R2-A-04)")]
    public async Task Dispose_WhileTimerHoldsDrainedBatch_StillWritesIt()
    {
        using var drained = new ManualResetEventSlim(false);
        using var proceed = new ManualResetEventSlim(false);
        var (writer, store) = Create();
        writer.AfterDrainForTests = () =>
        {
            drained.Set();
            proceed.Wait(TimeSpan.FromSeconds(30));
        };
        writer.Update(State(@"C:\photos", "a"));
        _timer.FireAll();
        Assert.True(drained.Wait(TimeSpan.FromSeconds(10)), "the timer never drained its batch");

        writer.AfterDrainForTests = null;
        writer.Dispose(); // nothing pending, no write in flight: previously disposed the semaphore here
        proceed.Set();

        await writer.WhenIdleAsync(); // faulted with ObjectDisposedException before the fix
        Assert.Equal("a", store.Load(@"C:\photos").CurrentPath);
    }
}
