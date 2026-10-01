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

    [Fact(DisplayName = "F3: a Failed size-mismatch Move (source gone, destination present) is DestinationChanged, Retry is refused and Dismiss works")]
    public async Task SizeMismatchMove_SourceGone_RecoveryRefusesRetryAndAllowsDismiss()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");
        var fileActions = new FileActionService(_journal, _fs, _clock, new NoRecycle(), (from, to) =>
        {
            _fs.Move(from, to);
            _fs.WriteAllTextAtomic(to, "x");
            return Task.CompletedTask;
        });
        var result = await fileActions.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"D:\Backup"));
        Assert.True(result.SourceRemoved);
        var failed = Assert.Single(_journal.ReadFailedOperations());

        var check = new RecoveryFileCheck(_fs).Check(failed);
        Assert.Equal(RecoveryVerdict.DestinationChanged, check.Verdict);

        var retry = await _service.RetryMoveOrCopyAsync(failed);
        Assert.False(retry.Succeeded);
        Assert.Equal(Core.Localization.Tr.CoreRecoverySourceMissing, retry.Message);
        Assert.False(_fs.FileExists(source));
        Assert.True(_fs.FileExists(@"D:\Backup\a.jpg"));
        Assert.Equal(1, _fs.GetFileStat(@"D:\Backup\a.jpg")!.Length);

        _journal.Dismiss([failed]);
        Assert.Empty(_journal.ReadFailedOperations());
        Assert.True(_fs.FileExists(@"D:\Backup\a.jpg"));
    }

    private sealed class NoRecycle : IRecycleBin
    {
        public void SendToRecycleBin(string path) => throw new InvalidOperationException("must not recycle");
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    [Fact(DisplayName = "Retrying a failed undo-Move keeps the Undo flag on the committed entry (no redo after restart)")]
    public async Task RetryMove_FailedUndoMove_CommittedEntryKeepsUndoFlag()
    {
        var source = @"D:\sorted\a.jpg";
        var dest = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        var stat = _fs.GetFileStat(source)!;

        var failed = new JournalEntry("op-undo", FileOperationType.Move, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow, "Previous error", Undo: true);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.True(result.Succeeded);
        Assert.True(result.Entry!.Undo);
        Assert.All(_journal.ReadCommittedMoves().Where(e => e.Id == "op-undo"), e => Assert.True(e.Undo));
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "worker thread hung");

        Assert.True(result.Succeeded);
        Assert.NotEqual(-1, mutationThread);
        Assert.NotEqual(callerThread, mutationThread);
    }

    [Fact]
    public async Task Retry_WhileFileActionInProgress_RefusesWithBusy()
    {
        // RV-C08: Recovery is a file action too (INV-4); only the modal window kept it from overlapping a running one.
        var source = @"C:\photos\a.jpg";
        var dest = @"C:\photos\sub\a.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        var stat = _fs.GetFileStat(source)!;
        var failed = new JournalEntry("op-busy", FileOperationType.Move, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow, "Previous error");
        _journal.Append(failed);
        var gate = new FileActionService(_journal, _fs, _clock, new NoRecycle());
        var service = new RecoveryRetryService(_journal, _fs, _clock, fileActionGate: gate);
        Assert.True(gate.TryBegin()); // a Move is running
        var stats = 0;
        _fs.StatHook = _ => { Interlocked.Increment(ref stats); return null; };
        var eventsBefore = _fs.Events.Count;

        var busy = await service.RetryMoveOrCopyAsync(failed);

        Assert.False(busy.Succeeded);
        Assert.False(busy.Superseded);
        Assert.Null(busy.Entry);
        Assert.Equal(Core.Localization.Tr.CoreRecoveryBusy, busy.Message);
        Assert.Equal(0, stats);
        Assert.Equal(eventsBefore, _fs.Events.Count); // no journal append, no move
        Assert.True(_fs.FileExists(source));
        Assert.False(_fs.FileExists(dest));
        Assert.True(gate.IsBusy); // the running action still owns the gate

        gate.End();
        var retried = await service.RetryMoveOrCopyAsync(failed);
        Assert.True(retried.Succeeded, retried.Message);
        Assert.False(gate.IsBusy); // the retry released the gate it took
    }

    [Fact]
    public async Task Retry_GroupWhileFileActionInProgress_RefusesWithBusy()
    {
        var gate = new FileActionService(_journal, _fs, _clock, new NoRecycle());
        var service = new RecoveryRetryService(_journal, _fs, _clock, fileActionGate: gate);
        var members = new[]
        {
            new JournalGroupMember(@"C:\photos\a.jpg", @"C:\photos\sub\a.jpg", 5, _clock.UtcNow),
            new JournalGroupMember(@"C:\photos\a.cr2", @"C:\photos\sub\a.cr2", 5, _clock.UtcNow),
        };
        var failed = new JournalEntry("op-group", FileOperationType.Move, JournalState.Failed, members[0].Source,
            members[0].Destination, 5, _clock.UtcNow, _clock.UtcNow, "Previous error", GroupId: "g", GroupMembers: members);
        Assert.True(gate.TryBegin());
        var stats = 0;
        _fs.StatHook = _ => { Interlocked.Increment(ref stats); return null; };

        var busy = await service.RetryMoveOrCopyAsync(failed);

        Assert.Equal(Core.Localization.Tr.CoreRecoveryBusy, busy.Message);
        Assert.Equal(0, stats);
        Assert.True(gate.IsBusy);
    }

    [Fact]
    public async Task RetryCopy_FailsMidway_RemovesPartialDestination()
    {
        // RV-C03: the retry's own partial copy must not stay behind (it would turn the entry into a Conflict).
        var source = @"C:\photos\a.jpg";
        var dest = @"C:\photos\sub\a.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        var stat = _fs.GetFileStat(source)!;
        var failed = new JournalEntry("op-copy-partial", FileOperationType.Copy, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow, "Previous error");
        _journal.Append(failed);
        _fs.CopyFailsAfterBytes = 2;

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.True(_fs.FileExists(source));
        Assert.False(_fs.FileExists(dest));
        Assert.NotNull(result.Entry);
        Assert.Equal(RecoveryVerdict.CanRetry, new RecoveryFileCheck(_fs).Check(result.Entry).Verdict);
    }

    [Fact]
    public async Task RetryGroupCopy_FailsMidway_RemovesPartialDestinationOfTheFailingMember()
    {
        var jpeg = @"C:\photos\a.jpg";
        var raw = @"C:\photos\a.cr2";
        _fs.WriteAllTextAtomic(jpeg, "12345");
        _fs.WriteAllTextAtomic(raw, "1234567890");
        var members = new[]
        {
            new JournalGroupMember(jpeg, @"C:\photos\sub\a.jpg", 5, _fs.GetFileStat(jpeg)!.LastWriteUtc),
            new JournalGroupMember(raw, @"C:\photos\sub\a.cr2", 10, _fs.GetFileStat(raw)!.LastWriteUtc),
        };
        var failed = new JournalEntry("op-group-copy", FileOperationType.Copy, JournalState.Failed, jpeg, members[0].Destination,
            5, members[0].LastWriteUtc, _clock.UtcNow, "Previous error", GroupId: "g", GroupMembers: members);
        _journal.Append(failed);
        _fs.CopyHook = (from, to) =>
        {
            _fs.CopyFailsAfterBytes = string.Equals(from, raw, StringComparison.OrdinalIgnoreCase) ? 3 : null;
            return null;
        };

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.False(_fs.FileExists(@"C:\photos\sub\a.cr2"));
        Assert.True(_fs.FileExists(raw));
    }

    [Fact]
    public async Task RetryCopy_FailsAndPartialCleanupThrowsUnexpectedly_ReportsTheOriginalFailure()
    {
        var source = @"C:\photos\a.jpg";
        var dest = @"C:\photos\sub\a.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        var stat = _fs.GetFileStat(source)!;
        var failed = new JournalEntry("op-copy-cleanup", FileOperationType.Copy, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow, "Previous error");
        _journal.Append(failed);
        _fs.CopyFailsAfterBytes = 2;
        _fs.DeleteHook = _ => new InvalidOperationException("simulated filter driver failure");

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal("Simulated disk full during copy.", result.Message);
        Assert.NotNull(result.Entry);
        Assert.Equal(JournalState.Failed, result.Entry.State);
    }

    [Fact]
    public async Task RetryCopy_ForeignFileAppearsAtDestination_IsNotDeleted()
    {
        var source = @"C:\photos\a.jpg";
        var dest = @"C:\photos\sub\a.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        var stat = _fs.GetFileStat(source)!;
        var failed = new JournalEntry("op-copy-foreign", FileOperationType.Copy, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow, "Previous error");
        _journal.Append(failed);
        _fs.CopyHook = (_, to) =>
        {
            _fs.AddFile(to, "xy"); // someone else's shorter file, after the pre-check
            return null;
        };

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal("xy", _fs.ReadAllText(dest));
    }

    [Fact(DisplayName = "Constructor validates null arguments")]
    public void Constructor_NullValidation()
    {
        Assert.Throws<ArgumentNullException>(() => new RecoveryRetryService(null!, _fs, _clock));
        Assert.Throws<ArgumentNullException>(() => new RecoveryRetryService(_journal, null!, _clock));
        Assert.Throws<ArgumentNullException>(() => new RecoveryRetryService(_journal, _fs, null!));
    }
}
