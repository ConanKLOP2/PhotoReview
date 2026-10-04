using System.Diagnostics;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Session;

/// <summary>Mutation-testing gaps in <see cref="SessionWriter"/>: newest state wins, stale state is never written, shutdown bookkeeping.</summary>
[Trait("Category", "HotPath")]
public sealed class SessionWriterShutdownAndVersionTests
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

    private sealed class ManualDelay
    {
        private readonly List<TaskCompletionSource> _waiters = [];
        public List<TimeSpan> Requested { get; } = [];

        public Task Delay(TimeSpan span, CancellationToken token)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => tcs.TrySetCanceled(token));
            _waiters.Add(tcs);
            Requested.Add(span);
            return tcs.Task;
        }

        public void FireAll() { foreach (var w in _waiters) w.TrySetResult(); }
    }

    private readonly ReviewMetrics _metrics = new();
    private readonly InMemoryFileSystem _fs = new();
    private readonly ManualDelay _timer = new();
    private readonly MutationRecordingLog _log = new();

    private (SessionWriter Writer, SessionStore Store) Create(TimeSpan? debounce = null)
    {
        var store = new SessionStore(new Paths(), _fs, _metrics);
        return (new SessionWriter(store, _log, debounce, _timer.Delay), store);
    }

    private static SessionState State(string folder, string current) =>
        new() { Folder = folder, CurrentPath = current };

    [Fact]
    public void Update_CustomDebounce_IsPassedToTheTimer()
    {
        var (writer, _) = Create(TimeSpan.FromSeconds(7));

        writer.Update(State(@"C:\photos", "a"));

        Assert.Equal([TimeSpan.FromSeconds(7)], _timer.Requested);
    }

    [Fact]
    public void Update_NoDebounceGiven_UsesDefaultDebounce()
    {
        var (writer, _) = Create();

        writer.Update(State(@"C:\photos", "a"));

        Assert.Equal([SessionWriter.DefaultDebounce], _timer.Requested);
    }

    [Fact]
    public void Update_AfterDispose_IsIgnored()
    {
        var (writer, store) = Create();
        writer.Dispose();

        writer.Update(State(@"C:\photos", "late"));
        writer.Flush();

        Assert.Empty(_timer.Requested);
        Assert.Equal(0, _metrics.Snapshot().SessionWriteCount);
        Assert.Null(store.Load(@"C:\photos").CurrentPath);
    }

    [Fact]
    public void Update_FolderThatCannotBeCanonicalised_DoesNotThrowAndOtherFoldersAreStillWritten()
    {
        var (writer, store) = Create();

        var ex = Record.Exception(() => writer.Update(State("C:\\bad\0folder", "x")));
        writer.Update(State(@"C:\good", "y"));
        writer.Flush();

        Assert.Null(ex);
        Assert.Equal("y", store.Load(@"C:\good").CurrentPath);
    }

    [Fact]
    public void Flush_StaleBatchDrainedBeforeNewerUpdate_DoesNotOverwriteTheNewerState()
    {
        var (writer, store) = Create();
        writer.Update(State(@"C:\photos", "old"));
        writer.AfterDrainForTests = () =>
        {
            writer.AfterDrainForTests = null;
            writer.Update(State(@"C:\photos", "new"));
            writer.Flush(); // writes "new" while the outer flush still holds the drained "old" batch
        };

        writer.Flush();

        Assert.Equal("new", store.Load(@"C:\photos").CurrentPath);
        Assert.Equal(1, _metrics.Snapshot().SessionWriteCount);
    }

    [Fact]
    public void Flush_SaveWorkerCannotBeStarted_ReleasesTheWriterLockAndTheStateIsWrittenByTheNextFlush()
    {
        var (writer, store) = Create();
        writer.BoundedWaitForTests = TimeSpan.FromMilliseconds(200); // a leaked lock would make the second flush give up after this
        var defaultStart = writer.StartSaveForTests;
        writer.StartSaveForTests = _ => throw new InvalidOperationException("cannot start the save worker");
        writer.Update(State(@"C:\photos", "kept"));

        var ex = Record.Exception(writer.Flush);
        writer.StartSaveForTests = defaultStart;
        writer.Flush();

        Assert.Null(ex);
        Assert.Contains(_log.Errors, e => e.Message.Contains("could not be started", StringComparison.Ordinal));
        Assert.Equal("kept", store.Load(@"C:\photos").CurrentPath);
    }

    [Fact]
    public async Task Flush_TimedOutBatchWhoseStateWasSuperseded_IsNotRequeuedOverTheNewerState()
    {
        var (writer, store) = Create();
        writer.BoundedWaitForTests = TimeSpan.FromMilliseconds(100);
        using var drained = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        writer.Update(State(@"C:\photos", "s1"));
        writer.AfterDrainForTests = () =>
        {
            drained.Set();
            go.Wait(TimeSpan.FromSeconds(30));
        };
        var flush1 = Task.Run(writer.Flush); // drains "s1", then pauses before taking the writer lock
        Assert.True(drained.Wait(TimeSpan.FromSeconds(10)), "the first flush never drained its batch");
        writer.AfterDrainForTests = null;

        writer.Update(State(@"C:\photos", "s2"));
        _fs.WriteHook = _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return null;
        };
        var flush2 = Task.Run(writer.Flush); // drains "s2" and blocks inside the write while holding the writer lock
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the second flush never started writing");
        _fs.WriteHook = null;

        go.Set();
        await flush1.WaitAsync(TimeSpan.FromSeconds(20)); // bounded wait on the held lock times out; "s1" is obsolete and must be dropped
        release.Set();
        await flush2.WaitAsync(TimeSpan.FromSeconds(20));
        writer.Flush();

        Assert.Equal("s2", store.Load(@"C:\photos").CurrentPath);
    }

    [Fact]
    public void Flush_FailingWrite_IsLoggedWithTheFolderAndTheException()
    {
        var (writer, store) = Create();
        var badKey = Path.GetFileNameWithoutExtension(store.GetPath(@"C:\bad"));
        var boom = new InvalidDataException("boom"); // not one of the types SessionStore.Save swallows itself
        _fs.WriteHook = path => path.Contains(badKey, StringComparison.OrdinalIgnoreCase) ? boom : null;
        writer.Update(State(@"C:\bad", "x"));

        writer.Flush();

        var error = Assert.Single(_log.Errors);
        Assert.Contains(@"C:\bad", error.Message, StringComparison.Ordinal);
        Assert.Same(boom, error.Exception);
    }

    [Fact]
    public void Dispose_NothingEverPending_LogsNoError()
    {
        var (writer, _) = Create();
        writer.Flush(); // an empty batch must not count as an in-flight write

        writer.Dispose();

        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void Dispose_AfterACompletedWrite_LogsNoError()
    {
        var (writer, _) = Create();
        writer.Update(State(@"C:\photos", "a"));
        writer.Flush();

        writer.Dispose();

        Assert.Empty(_log.Errors); // the finished write must have released its in-flight slot
    }

    [Fact]
    public void Dispose_WhenTheDrainedBatchIsWrittenWhileWaiting_ReturnsWithoutTimeoutError()
    {
        using var drained = new ManualResetEventSlim();
        using var disposeWaiting = new ManualResetEventSlim();
        var (writer, store) = Create();
        writer.AfterDrainForTests = () =>
        {
            drained.Set();
            disposeWaiting.Wait(TimeSpan.FromSeconds(10));
        };
        writer.BeforeShutdownWaitForTests = disposeWaiting.Set;
        writer.Update(State(@"C:\photos", "a"));
        _timer.FireAll();
        Assert.True(drained.Wait(TimeSpan.FromSeconds(10)), "the timer never drained its batch");

        writer.Dispose();

        Assert.Equal("a", store.Load(@"C:\photos").CurrentPath);
        Assert.Empty(_log.Errors); // the wait loop ends as soon as the write finished, it does not run into the deadline
    }

    [Fact]
    public async Task Dispose_WriteStuckLongerThanShutdownWait_GivesUpAfterTheBoundIncludingTimeAlreadySpent()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var (writer, _) = Create();
        // The flush inside Dispose waits 1.9 s for the writer lock before giving up; the shutdown bound is 2 s in total,
        // so only ~0.1 s may remain. Waiting the full bound again (or bound + elapsed) would end after at least 5.8 s.
        writer.BoundedWaitForTests = TimeSpan.FromMilliseconds(1900);
        _fs.WriteHook = _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(60));
            return null;
        };
        writer.Update(State(@"C:\photos", "a"));
        _timer.FireAll();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the debounced write never started");
        writer.Update(State(@"C:\photos", "b"));

        var watch = Stopwatch.StartNew();
        var dispose = Task.Factory.StartNew(writer.Dispose, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); // own thread: no thread-pool queueing delay
        await dispose.WaitAsync(TimeSpan.FromSeconds(30));
        watch.Stop();
        release.Set();
        await writer.WhenIdleAsync();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5.3), $"Dispose took {watch.Elapsed}");
        Assert.Contains(_log.Errors, e => e.Message.Contains("may be lost at shutdown", StringComparison.Ordinal));
    }
    [Fact]
    public async Task Dispose_SaveStallsAfterTheLockWasAcquired_StillReturnsWithinTheBoundAndWritesLaterInTheBackground()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var (writer, store) = Create();
        writer.BoundedWaitForTests = TimeSpan.FromMilliseconds(200); // the shutdown budget, shortened so a regression fails fast
        _fs.WriteHook = _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(60)); // a disk that stalls only now: the writer lock is free, so the lock wait cannot bound this
            return null;
        };
        writer.Update(State(@"C:\photos", "last"));

        // Dispose is what the DI container calls from App.Dispose (APP-01); own thread so a hang cannot starve the pool.
        var dispose = Task.Factory.StartNew(writer.Dispose, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var returned = false;
        try
        {
            await dispose.WaitAsync(TimeSpan.FromSeconds(20));
            returned = true;
        }
        catch (TimeoutException) { }
        finally { release.Set(); } // event-driven release; also frees the stalled thread when the bound was exceeded
        await dispose;

        Assert.True(entered.IsSet, "the save never started");
        Assert.True(returned, "Dispose blocked behind a stalled save instead of honouring the shutdown budget");
        Assert.Contains(_log.Errors, e => e.Message.Contains("may be lost at shutdown", StringComparison.Ordinal));
        // Durability contract (ADR 0007 s2): the write is only detached, not dropped; once the disk answers the state lands.
        Assert.True(SpinWait.SpinUntil(() => store.Load(@"C:\photos").CurrentPath == "last", TimeSpan.FromSeconds(20)), "the detached save never completed");
    }
}