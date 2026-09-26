using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

[Trait("Category", "HotPath")]
public sealed class RecoveryRetryServiceTests
{
    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTime utcNow) => UtcNow = utcNow;
        public DateTime UtcNow { get; set; }
    }

    private sealed class FakeAppPaths : IAppPaths
    {
        public FakeAppPaths(string journalPath) => JournalFile = journalPath;
        public string ConfigFile => @"C:\data\config.json";
        public string JournalFile { get; }
        public string SessionsDir => @"C:\data\Sessions";
        public string LogFile => @"C:\data\logs\app.log";
        public string PreviewCacheDir => @"C:\data\cache";
        public string ThumbnailCacheDir => @"C:\data\thumbnails";
        public string WindowPlacementFile => @"C:\data\window-placement.json";
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly FakeClock _clock = new(new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc));
    private readonly OperationJournal _journal;
    private readonly RecoveryRetryService _service;

    public RecoveryRetryServiceTests()
    {
        _journal = new OperationJournal(new FakeAppPaths(@"C:\data\operations.jsonl"), _fs, _clock);
        _service = new RecoveryRetryService(_journal, _fs, _clock);
    }

    [Fact(DisplayName = "RetryMoveOrCopyAsync executes Move successfully")]
    public async Task RetryMove_Success()
    {
        var source = @"C:\photos\a.jpg";
        var dest = @"C:\photos\sub\a.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        var stat = _fs.GetFileStat(source)!;

        var failed = new JournalEntry("op-1", FileOperationType.Move, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow, "Previous error");

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.True(result.Succeeded);
        Assert.Equal("Thử lại thành công.", result.Message);
        Assert.NotNull(result.Entry);
        Assert.Equal(JournalState.Committed, result.Entry.State);
        Assert.False(_fs.FileExists(source));
        Assert.True(_fs.FileExists(dest));
    }

    [Fact(DisplayName = "Retried Move that copied but left the source is a failure (MoveSourceNotRemoved), both copies kept")]
    public async Task RetryMove_SourceStillExistsAfterMove_FailsWithDistinctCode()
    {
        var source = @"C:\photos\a.jpg";
        var dest = @"D:\sorted\a.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        var stat = _fs.GetFileStat(source)!;
        _fs.MoveLeavesSource = true;

        var failed = new JournalEntry("op-1", FileOperationType.Move, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow, "Previous error");

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal(Core.Localization.Tr.CoreFileActionMoveSourceNotRemoved, result.Message);
        Assert.Equal(JournalState.Failed, result.Entry!.State);
        Assert.Equal(JournalErrors.MoveSourceNotRemoved, result.Entry.ErrorCode);
        Assert.True(_fs.FileExists(source));
        Assert.True(_fs.FileExists(dest));
        var latest = Assert.Single(_journal.ReadFailedOperations());
        Assert.Equal(JournalErrors.MoveSourceNotRemoved, latest.ErrorCode);
        Assert.Empty(_journal.ReadCommittedMoves());
    }

    [Fact(DisplayName = "RetryMoveOrCopyAsync executes Copy successfully leaving source")]
    public async Task RetryCopy_Success()
    {
        var source = @"C:\photos\a.jpg";
        var dest = @"C:\photos\sub\a.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        var stat = _fs.GetFileStat(source)!;

        var failed = new JournalEntry("op-copy", FileOperationType.Copy, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow, "Previous error");

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.True(result.Succeeded);
        Assert.Equal("Thử lại thành công.", result.Message);
        Assert.NotNull(result.Entry);
        Assert.Equal(JournalState.Committed, result.Entry.State);
        Assert.True(_fs.FileExists(source));
        Assert.True(_fs.FileExists(dest));
    }

    [Fact(DisplayName = "RetryMoveOrCopyAsync preserves completed move when commit journal fails")]
    public async Task RetryMove_CommitJournalFailure_PreservesCompletedOutcome()
    {
        var source = @"C:\photos\journal-failure.jpg";
        var dest = @"C:\photos\sub\journal-failure.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        var stat = _fs.GetFileStat(source)!;
        var appendCalls = 0;
        _fs.OpenAppendHook = _ => ++appendCalls == 2
            ? new IOException("journal unavailable")
            : null;
        var failed = new JournalEntry("op-journal-failure", FileOperationType.Move, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow, "Previous error");

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.True(result.Succeeded);
        Assert.False(result.JournalPersisted);
        Assert.Equal("journal unavailable", result.JournalError);
        Assert.False(_fs.FileExists(source));
        Assert.True(_fs.FileExists(dest));
    }

    [Fact(DisplayName = "RetryMoveOrCopyAsync preserves mutation failure when failed journal append also fails")]
    public async Task RetryMove_MutationAndFailureJournalFailure_PreservesOriginalFailure()
    {
        var source = @"C:\photos\mutation-failure.jpg";
        var dest = @"C:\photos\sub\mutation-failure.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        var stat = _fs.GetFileStat(source)!;
        var appendCalls = 0;
        _fs.OpenAppendHook = _ => ++appendCalls == 2
            ? new IOException("journal unavailable")
            : null;
        _fs.MoveHook = (_, _) => new IOException("move unavailable");
        var failed = new JournalEntry("op-mutation-journal-failure", FileOperationType.Move, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow, "Previous error");

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.False(result.JournalPersisted);
        Assert.Equal("journal unavailable", result.JournalError);
        Assert.Contains("move unavailable", result.Message);
        Assert.True(_fs.FileExists(source));
        Assert.False(_fs.FileExists(dest));
    }

    [Fact(DisplayName = "RetryMoveOrCopyAsync rejects Recycle operation")]
    public async Task Retry_RejectsRecycle()
    {
        var failed = new JournalEntry("op-recycle", FileOperationType.Recycle, JournalState.Failed,
            @"C:\photos\a.jpg", null, 100, _clock.UtcNow, _clock.UtcNow);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Contains("Chỉ có thể thử lại Di chuyển/Sao chép", result.Message);
    }

    [Fact(DisplayName = "RetryMoveOrCopyAsync rejects missing destination")]
    public async Task Retry_RejectsMissingDestination()
    {
        var failed = new JournalEntry("op-nodest", FileOperationType.Move, JournalState.Failed,
            @"C:\photos\a.jpg", null, 100, _clock.UtcNow, _clock.UtcNow);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal("Thao tác không có đích.", result.Message);
    }

    [Fact(DisplayName = "RetryMoveOrCopyAsync rejects when source no longer exists")]
    public async Task Retry_RejectsMissingSource()
    {
        var failed = new JournalEntry("op-nosrc", FileOperationType.Move, JournalState.Failed,
            @"C:\photos\missing.jpg", @"C:\photos\dest.jpg", 100, _clock.UtcNow, _clock.UtcNow);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal("Nguồn không còn tồn tại.", result.Message);
    }

    [Fact(DisplayName = "RetryMoveOrCopyAsync rejects when source fingerprint has changed")]
    public async Task Retry_RejectsChangedSource()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "new content with different length");

        var failed = new JournalEntry("op-changed", FileOperationType.Move, JournalState.Failed,
            source, @"C:\photos\dest.jpg", 5, _clock.UtcNow, _clock.UtcNow);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal("Nguồn đã thay đổi; từ chối thử lại để bảo vệ dữ liệu.", result.Message);
    }

    [Fact(DisplayName = "RetryMoveOrCopyAsync rejects when destination already exists")]
    public async Task Retry_RejectsExistingDestination()
    {
        var source = @"C:\photos\a.jpg";
        var dest = @"C:\photos\dest.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        _fs.WriteAllTextAtomic(dest, "already exists");
        var stat = _fs.GetFileStat(source)!;

        var failed = new JournalEntry("op-destexists", FileOperationType.Move, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal("Đích đã tồn tại; không ghi đè.", result.Message);
    }

    [Fact(DisplayName = "RetryMoveOrCopyAsync runs the file mutation off the caller thread")]
    public async Task RetryMoveOrCopyAsync_RunsMutationOffCallerThread()
    {
        var source = @"C:\photos\a.jpg";
        var dest = @"C:\photos\sub\a.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        var stat = _fs.GetFileStat(source)!;
        var failed = new JournalEntry("op-thread", FileOperationType.Copy, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow);

        var mutationThread = -1;
        _fs.CopyHook = (_, _) =>
        {
            mutationThread = Environment.CurrentManagedThreadId;
            return null;
        };

        // A dedicated thread with a single-threaded context stands in for the UI thread: a mutation that runs
        // synchronously would record this thread's id.
        var callerThread = -1;
        var done = new TaskCompletionSource<RecoveryRetryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            try { done.SetResult(_service.RetryMoveOrCopyAsync(failed).GetAwaiter().GetResult()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.Start();
        var result = await done.Task;
        thread.Join();

        Assert.True(result.Succeeded);
        Assert.NotEqual(-1, mutationThread);
        Assert.NotEqual(callerThread, mutationThread);
    }

    [Fact(DisplayName = "Constructor validates null arguments")]
    public void Constructor_NullValidation()
    {
        Assert.Throws<ArgumentNullException>(() => new RecoveryRetryService(null!, _fs, _clock));
        Assert.Throws<ArgumentNullException>(() => new RecoveryRetryService(_journal, null!, _clock));
        Assert.Throws<ArgumentNullException>(() => new RecoveryRetryService(_journal, _fs, null!));
    }
}
