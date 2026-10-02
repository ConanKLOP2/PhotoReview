using System.IO;
using System.Threading.Tasks;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.Core.Tests.FileActions;

[Trait("Category", "HotPath")]
public sealed class FileActionServiceTests
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

    private sealed class FakeRecycleBin : IRecycleBin
    {
        public List<string> RecycledPaths { get; } = [];
        public Func<string, Exception?>? RecycleHook { get; set; }

        public void SendToRecycleBin(string path)
        {
            if (RecycleHook?.Invoke(path) is { } ex) throw ex;
            RecycledPaths.Add(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => true;
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly FakeClock _clock = new(new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc));
    private readonly OperationJournal _journal;
    private readonly FakeRecycleBin _recycleBin = new();
    private readonly FileActionService _service;

    public FileActionServiceTests()
    {
        _journal = new OperationJournal(new FakeAppPaths(@"C:\data\operations.jsonl"), _fs, _clock);
        _service = new FileActionService(_journal, _fs, _clock, _recycleBin);
    }

    [Theory]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    public async Task ExecuteAsync_MissingSource_DoesNotCreateDestinationFolder(FileOperationType operation)
    {
        var result = await _service.ExecuteAsync(new FileActionRequest(@"C:\photos\gone.jpg", operation, "sel"));

        Assert.False(result.Succeeded);
        Assert.False(_fs.DirectoryExists(@"C:\photos\sel"));
    }

    [Theory]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    public async Task ExecuteAsync_RelativeDestinationOutsideSource_FailsWithoutJournalOrMove(FileOperationType operation)
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, operation, @"..\outside"));

        Assert.False(result.Succeeded);
        Assert.Equal(Core.Localization.Tr.CoreFileActionDestinationOutsideSource, result.Error);
        Assert.True(_fs.FileExists(source));
        Assert.False(_fs.FileExists(@"C:\outside\a.jpg"));
        Assert.DoesNotContain(_fs.Events, e => e.Kind is "move" or "copy" or "append");
        Assert.False(_fs.FileExists(@"C:\data\operations.jsonl"));
        Assert.Empty(_journal.ReadFailedOperations());
    }

    [Theory(DisplayName = "SEC-01: a relative destination through an existing junction whose real target is outside the photo folder is rejected")]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    public async Task ExecuteAsync_RelativeDestinationThroughJunctionEscapesFolder_FailsWithoutJournalOrMove(FileOperationType operation)
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");
        // "link" is a junction inside the photo folder whose real target is outside it: the lexical destination
        // "link\processed" has no ".." and is not rooted, so ActionDestinationPolicy.Validate alone would pass it.
        _fs.AddReparsePoint(@"C:\photos\link", @"C:\outside");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, operation, @"link\processed"));

        Assert.False(result.Succeeded);
        Assert.Equal(Core.Localization.Tr.CoreFileActionDestinationOutsideSource, result.Error);
        Assert.True(_fs.FileExists(source));
        Assert.False(_fs.FileExists(@"C:\outside\processed\a.jpg"));
        Assert.DoesNotContain(_fs.Events, e => e.Kind is "move" or "copy" or "append");
        Assert.False(_fs.FileExists(@"C:\data\operations.jsonl"));
    }

    [Fact(DisplayName = "SEC-01: a junction inside the photo folder whose real target is also inside it stays allowed")]
    public async Task ExecuteAsync_RelativeDestinationThroughJunctionStaysInsideFolder_Succeeds()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");
        _fs.AddReparsePoint(@"C:\photos\link", @"C:\photos\real");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"link\processed"));

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    public async Task ExecuteAsync_DriveRelativeDestination_FailsWithoutJournalOrMove(FileOperationType operation)
    {
        var source = @"C:\photos.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, operation, "D:Backup"));

        Assert.False(result.Succeeded);
        Assert.Equal(Core.Localization.Tr.CoreFileActionDestinationOutsideSource, result.Error);
        Assert.True(_fs.FileExists(source));
        Assert.DoesNotContain(_fs.Events, e => e.Kind is "move" or "copy" or "append");
        Assert.False(_fs.FileExists(@"C:\data\operations.jsonl"));
    }

    [Theory(DisplayName = "A rooted destination without a drive (\\Loai-2) is refused at run time, nothing journaled or moved")]
    [InlineData(FileOperationType.Move, @"\Loai-2")]
    [InlineData(FileOperationType.Copy, @"\Loai-2")]
    [InlineData(FileOperationType.Move, "/Loai-2")]
    public async Task ExecuteAsync_RootedDestinationWithoutDrive_FailsWithoutJournalOrMove(FileOperationType operation, string destination)
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, operation, destination));

        Assert.False(result.Succeeded);
        Assert.Equal(Core.Localization.Tr.CoreFileActionDestinationOutsideSource, result.Error);
        Assert.True(_fs.FileExists(source));
        Assert.False(_fs.FileExists(@"C:\Loai-2\a.jpg"));
        Assert.DoesNotContain(_fs.Events, e => e.Kind is "move" or "copy" or "append");
        Assert.False(_fs.FileExists(@"C:\data\operations.jsonl"));
    }

    [Fact(DisplayName = "Cross-volume Move that copied but could not delete the source fails with MoveSourceNotRemoved and keeps both copies")]
    public async Task ExecuteAsync_MoveLeavesSource_FailsWithDistinctCodeAndKeepsBoth()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");
        _fs.MoveLeavesSource = true;

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"D:\Backup"));

        Assert.False(result.Succeeded);
        Assert.Equal(Core.Localization.Tr.CoreFileActionMoveSourceNotRemoved, result.Error);
        Assert.True(result.JournalPersisted);
        Assert.True(_fs.FileExists(source));
        Assert.True(_fs.FileExists(@"D:\Backup\a.jpg"));
        var failed = Assert.Single(_journal.ReadFailedOperations());
        Assert.Equal(JournalErrors.MoveSourceNotRemoved, failed.ErrorCode);
        Assert.Equal(JournalErrors.EnglishText(JournalErrors.MoveSourceNotRemoved), failed.Error);
        Assert.Empty(_journal.ReadCommittedMoves());
    }

    [Fact(DisplayName = "F3: a Move whose size changed and whose source is gone is Failed, flags SourceRemoved, keeps the destination and registers no committed move")]
    public async Task ExecuteAsync_MoveSizeChangedSourceGone_FailedJournalAndSourceRemoved()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");
        var service = new FileActionService(_journal, _fs, _clock, _recycleBin, (from, to) =>
        {
            _fs.Move(from, to);
            _fs.WriteAllTextAtomic(to, "x");
            return Task.CompletedTask;
        });

        var result = await service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"D:\Backup"));

        Assert.False(result.Succeeded);
        Assert.True(result.SourceRemoved);
        Assert.True(result.JournalPersisted);
        Assert.False(_fs.FileExists(source));
        Assert.True(_fs.FileExists(@"D:\Backup\a.jpg"));
        var failed = Assert.Single(_journal.ReadFailedOperations());
        Assert.Equal(JournalErrors.VerifySizeChanged, failed.ErrorCode);
        Assert.Equal(source, failed.Source);
        Assert.Empty(_journal.ReadCommittedMoves());
    }

    [Fact(DisplayName = "F3: a Move that leaves the source in place does not set SourceRemoved")]
    public async Task ExecuteAsync_MoveLeavesSource_SourceRemovedFalse()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");
        _fs.MoveLeavesSource = true;

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"D:\Backup"));

        Assert.False(result.Succeeded);
        Assert.False(result.SourceRemoved);
    }

    [Fact(DisplayName = "F3: a Move that fails before touching the file (OS error, source intact) does not set SourceRemoved")]
    public async Task ExecuteAsync_MoveOsFailureSourceIntact_SourceRemovedFalse()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");
        var service = new FileActionService(_journal, _fs, _clock, _recycleBin, (_, _) => Task.FromException(new IOException("OS says no")));

        var result = await service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"D:\Backup"));

        Assert.False(result.Succeeded);
        Assert.False(result.SourceRemoved);
    }

    [Fact(DisplayName = "F3: a Move whose source is missing before anything is journaled does not set SourceRemoved")]
    public async Task ExecuteAsync_MoveSourceMissingUpFront_SourceRemovedFalse()
    {
        var result = await _service.ExecuteAsync(new FileActionRequest(@"C:\photos\gone.jpg", FileOperationType.Move, @"D:\Backup"));

        Assert.False(result.Succeeded);
        Assert.False(result.SourceRemoved);
        Assert.Empty(_journal.ReadFailedOperations());
    }

    [Fact(DisplayName = "F3: a failed Copy never sets SourceRemoved (the source stays)")]
    public async Task ExecuteAsync_CopyFailure_SourceRemovedFalse()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");
        _fs.CopyHook = (_, _) => new IOException("copy went wrong");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Copy, @"D:\Backup"));

        Assert.False(result.Succeeded);
        Assert.False(result.SourceRemoved);
        Assert.True(_fs.FileExists(source));
    }

    [Fact(DisplayName = "F3: a failed Recycle never sets SourceRemoved")]
    public async Task ExecuteAsync_RecycleFailure_SourceRemovedFalse()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");
        _recycleBin.RecycleHook = _ => new IOException("bin error");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Recycle, null));

        Assert.False(result.Succeeded);
        Assert.False(result.SourceRemoved);
    }

    [Fact(DisplayName = "Copy is unaffected by the Move source check (source is expected to stay)")]
    public async Task ExecuteAsync_Copy_SourceStays_Succeeds()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Copy, @"D:\Backup"));

        Assert.True(result.Succeeded);
        Assert.True(_fs.FileExists(source));
    }

    [Fact]
    public async Task ExecuteAsync_RelativeDestinationInsideSource_Succeeds()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"sub\deeper"));

        Assert.True(result.Succeeded);
        Assert.Equal(@"C:\photos\sub\deeper\a.jpg", result.DestinationPath);
    }

    [Fact]
    public async Task ExecuteAsync_AbsoluteDestinationOutsideSource_StaysAllowed()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"D:\Backup"));

        Assert.True(result.Succeeded);
        Assert.Equal(@"D:\Backup\a.jpg", result.DestinationPath);
    }

    [Fact]
    public async Task ExecuteAsync_Move_Success()
    {
        var source = @"C:\photos\a.jpg";
        var destFolder = @"C:\photos\selected";
        var expectedDest = @"C:\photos\selected\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");

        var request = new FileActionRequest(source, FileOperationType.Move, destFolder);
        var result = await _service.ExecuteAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(FileOperationType.Move, result.Operation);
        Assert.Equal(source, result.Source);
        Assert.Equal(expectedDest, result.DestinationPath);
        Assert.Null(result.Error);
        Assert.False(_fs.FileExists(source));
        Assert.True(_fs.FileExists(expectedDest));

        var moves = _journal.ReadCommittedMoves();
        Assert.Single(moves);
        Assert.Equal(source, moves[0].Source);
        Assert.Equal(expectedDest, moves[0].Destination);
    }

    [Fact]
    public async Task ExecuteAsync_Copy_Success()
    {
        var source = @"C:\photos\a.jpg";
        var destFolder = @"C:\photos\copy_dest";
        var expectedDest = @"C:\photos\copy_dest\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello copy");

        var request = new FileActionRequest(source, FileOperationType.Copy, destFolder);
        var result = await _service.ExecuteAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(FileOperationType.Copy, result.Operation);
        Assert.Equal(expectedDest, result.DestinationPath);
        Assert.True(_fs.FileExists(source));
        Assert.True(_fs.FileExists(expectedDest));
    }

    [Fact]
    public async Task ExecuteAsync_Recycle_Success()
    {
        var source = @"C:\photos\to_delete.jpg";
        _fs.WriteAllTextAtomic(source, "delete me");

        var request = new FileActionRequest(source, FileOperationType.Recycle);
        var result = await _service.ExecuteAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(FileOperationType.Recycle, result.Operation);
        Assert.Contains(source, _recycleBin.RecycledPaths);
    }

    [Fact]
    public async Task ExecuteAsync_DestinationMissing_Fails()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "test");

        var request = new FileActionRequest(source, FileOperationType.Move, "   ");
        var result = await _service.ExecuteAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("Hành động chưa có thư mục đích.", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_SameFolder_Fails()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "test");

        var request = new FileActionRequest(source, FileOperationType.Move, @"C:\photos");
        var result = await _service.ExecuteAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("Không thể Di chuyển/Sao chép vào chính thư mục nguồn.", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_DestinationExists_Fails()
    {
        var source = @"C:\photos\a.jpg";
        var destFolder = @"C:\photos\dest";
        var existing = @"C:\photos\dest\a.jpg";
        _fs.WriteAllTextAtomic(source, "test");
        _fs.WriteAllTextAtomic(existing, "already exists");

        var request = new FileActionRequest(source, FileOperationType.Move, destFolder);
        var result = await _service.ExecuteAsync(request);

        Assert.False(result.Succeeded);
        Assert.Contains("Đích đã tồn tại:", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_SourceNotFound_FailsWithoutPreparedInJournal()
    {
        var source = @"C:\photos\nonexistent.jpg";
        var request = new FileActionRequest(source, FileOperationType.Move, @"C:\photos\dest");
        var result = await _service.ExecuteAsync(request);

        Assert.False(result.Succeeded);
        Assert.Contains("Nguồn không tồn tại", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_IOErrorDuringMove_RecordsFailedInJournal()
    {
        var source = @"C:\photos\error.jpg";
        _fs.WriteAllTextAtomic(source, "will fail");

        _fs.MoveHook = (src, dst) => new IOException("Disk error simulated");

        var request = new FileActionRequest(source, FileOperationType.Move, @"C:\photos\dest");
        var result = await _service.ExecuteAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("Disk error simulated", result.Error);

        var failedEntries = _journal.ReadFailedOperations();
        Assert.Single(failedEntries);
        Assert.Equal(JournalState.Failed, failedEntries[0].State);
        Assert.Equal("Disk error simulated", failedEntries[0].Error);
    }

    [Fact]
    public async Task ExecuteAsync_MoveCompletedButCommitJournalFails_PreservesCompletedOutcome()
    {
        var source = @"C:\photos\journal-failure.jpg";
        var destination = @"C:\photos\dest\journal-failure.jpg";
        _fs.WriteAllTextAtomic(source, "content");
        var appendCalls = 0;
        _fs.OpenAppendHook = _ => ++appendCalls == 2
            ? new IOException("journal unavailable")
            : null;

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"C:\photos\dest"));

        Assert.True(result.Succeeded);
        Assert.False(result.JournalPersisted);
        Assert.Equal("journal unavailable", result.JournalError);
        Assert.Null(result.Error);
        Assert.False(_fs.FileExists(source));
        Assert.True(_fs.FileExists(destination));
    }

    [Fact]
    public async Task ExecuteAsync_WhenGateBusy_RejectsSecondAction()
    {
        var source1 = @"C:\photos\a.jpg";
        var source2 = @"C:\photos\b.jpg";
        _fs.WriteAllTextAtomic(source1, "content1");
        _fs.WriteAllTextAtomic(source2, "content2");

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fs.MoveHook = (src, dst) =>
        {
            // Bounded: a failed assertion below must not leave a pool thread parked for the rest of the run.
            tcs.Task.Wait(TimeSpan.FromSeconds(30));
            return null;
        };

        Task<FileActionResult>? task1 = null;
        try
        {
            task1 = Task.Run(() => _service.ExecuteAsync(new FileActionRequest(source1, FileOperationType.Move, @"C:\photos\dest")));

            // Wait until task1 has acquired the gate
            await Wait.UntilAsync(() => _service.IsBusy || task1.IsCompleted, "first action holds the gate");
            Assert.True(_service.IsBusy, "First action ended before it held the gate.");

            var result2 = await _service.ExecuteAsync(new FileActionRequest(source2, FileOperationType.Move, @"C:\photos\dest"));

            Assert.True(result2.Rejected);
            Assert.False(result2.Succeeded);
            Assert.Equal("Thao tác trước đó vẫn đang chạy.", result2.Error);
        }
        finally
        {
            tcs.TrySetResult(true);
        }

        var result1 = await task1.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result1.Succeeded);
        Assert.False(_service.IsBusy);
    }

    [Fact]
    public void TryBegin_And_End_ManageBusyGate()
    {
        Assert.False(_service.IsBusy);
        Assert.True(_service.TryBegin());
        Assert.True(_service.IsBusy);
        Assert.False(_service.TryBegin());

        _service.End();
        Assert.False(_service.IsBusy);
        Assert.True(_service.TryBegin());
        _service.End();
    }
    // RV-C02: a Task.Run cancelled before its delegate starts leaves the disk untouched, so the operation is not a Recovery item.
    [Theory]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    [InlineData(FileOperationType.Recycle)]
    public async Task ExecuteAsync_CancelledBeforeStart_DismissesJournalEntry(FileOperationType operation)
    {
        var source = @"C:\photos\a.jpg";
        _fs.AddFile(source, "hello photo", new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _service.ExecuteAsync(
            new FileActionRequest(source, operation, operation == FileOperationType.Recycle ? null : "sel"), cts.Token);

        Assert.False(result.Succeeded);
        Assert.False(result.SourceRemoved);
        Assert.True(_fs.FileExists(source));
        Assert.False(_fs.FileExists(@"C:\photos\sel\a.jpg"));
        Assert.Empty(_recycleBin.RecycledPaths);
        Assert.Empty(_journal.ReadPendingAndFailedOperations());
        Assert.Empty(_journal.ReconcilePendingOperations()); // reconcile never resurrects it
        Assert.Contains(_fs.ReadLines(@"C:\data\operations.jsonl"), line => line.Contains("Dismissed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_MoveCancelledAfterMove_KeepsFailedEntry()
    {
        var source = @"C:\photos\a.jpg";
        _fs.AddFile(source, "hello photo", new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc));
        // The move itself completed, then the cancellation surfaced: the file is at the destination, so this is NOT a no-op.
        var service = new FileActionService(_journal, _fs, _clock, _recycleBin, (from, to) =>
        {
            _fs.Move(from, to);
            throw new OperationCanceledException();
        });

        var result = await service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, "sel"));

        Assert.False(result.Succeeded);
        Assert.True(result.SourceRemoved);
        var failed = Assert.Single(_journal.ReadPendingAndFailedOperations());
        Assert.Equal(JournalState.Failed, failed.State);
        Assert.Equal(JournalErrors.CancelledByUser, failed.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_CopyCancelledAfterCompleteCopy_KeepsFailedEntry()
    {
        var source = @"C:\photos\a.jpg";
        var destination = @"C:\photos\sel\a.jpg";
        _fs.AddFile(source, "hello photo", new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc));
        // The copy wrote every byte, then the cancellation surfaced: a complete copy is kept, so this is not a no-op.
        _fs.CopyHook = (_, to) =>
        {
            _fs.AddFile(to, "hello photo");
            return new OperationCanceledException();
        };

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Copy, "sel"));

        Assert.False(result.Succeeded);
        Assert.True(_fs.FileExists(destination));
        var failed = Assert.Single(_journal.ReadPendingAndFailedOperations());
        Assert.Equal(JournalErrors.CancelledByUser, failed.ErrorCode);
    }

    // RV-C03: a single Copy cut short (disk full) must not leave its partial file behind: Recovery would call it a Conflict and
    // the retry would refuse with "destination exists".
    [Fact]
    public async Task ExecuteAsync_CopyFailsMidway_RemovesPartialDestination()
    {
        var source = @"C:\photos\a.jpg";
        var destination = @"C:\photos\sel\a.jpg";
        _fs.AddFile(source, "hello photo", new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc));
        _fs.CopyFailsAfterBytes = 4;

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Copy, "sel"));

        Assert.False(result.Succeeded);
        Assert.True(_fs.FileExists(source));
        Assert.False(_fs.FileExists(destination));
        var failed = Assert.Single(_journal.ReadPendingAndFailedOperations());
        Assert.Equal(JournalState.Failed, failed.State);
        Assert.Equal(RecoveryVerdict.CanRetry, new RecoveryFileCheck(_fs).Check(failed).Verdict);
    }

    [Fact]
    public async Task ExecuteAsync_CopyFailsAndPartialCleanupThrowsUnexpectedly_StillJournalsTheOriginalFailure()
    {
        var source = @"C:\photos\a.jpg";
        _fs.AddFile(source, "hello photo", new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc));
        _fs.CopyFailsAfterBytes = 4;
        // The cleanup is best effort: whatever it throws must not replace the copy failure or skip the journal outcome.
        _fs.DeleteHook = _ => new InvalidOperationException("simulated filter driver failure");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Copy, "sel"));

        Assert.False(result.Succeeded);
        Assert.Equal("Simulated disk full during copy.", result.Error);
        var failed = Assert.Single(_journal.ReadPendingAndFailedOperations());
        Assert.Equal(JournalState.Failed, failed.State);
    }

    [Fact]
    public async Task ExecuteAsync_CopyFails_DestinationExistedBefore_IsNotDeleted()
    {
        var source = @"C:\photos\a.jpg";
        var destination = @"C:\photos\sel\a.jpg";
        _fs.AddFile(source, "hello photo", new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc));
        // Someone else's (shorter) file lands at the destination between the preflight and the copy: never ours to delete.
        _fs.CopyHook = (_, to) =>
        {
            _fs.AddFile(to, "foreign");
            return null;
        };

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Copy, "sel"));

        Assert.False(result.Succeeded);
        Assert.True(_fs.FileExists(destination));
        Assert.Equal("foreign", _fs.ReadAllText(destination));
    }

    [Fact]
    public async Task ExecuteAsync_CopyFailsAfterCompleteCopy_KeepsCompleteDestination()
    {
        var source = @"C:\photos\a.jpg";
        var destination = @"C:\photos\sel\a.jpg";
        _fs.AddFile(source, "hello photo", new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc));
        // Every byte was written before the failure: a complete copy is left for Recovery to judge, never deleted.
        _fs.CopyFailsAfterBytes = 11;

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Copy, "sel"));

        Assert.False(result.Succeeded);
        Assert.Equal("hello photo", _fs.ReadAllText(destination));
    }

    [Fact]
    public async Task ExecuteAsync_MoveFailsLeavingPartialDestination_RemovesItAndKeepsSource()
    {
        var source = @"C:\photos\a.jpg";
        var destination = @"C:\photos\sel\a.jpg";
        _fs.AddFile(source, "hello photo", new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc));
        // A cross-volume move cut short: the copy half left a shorter file at the destination, the source is untouched.
        _fs.MoveHook = (_, to) =>
        {
            _fs.AddFile(to, "hel");
            return new IOException("simulated disk full during move");
        };

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, "sel"));

        Assert.False(result.Succeeded);
        Assert.True(_fs.FileExists(source));
        Assert.False(_fs.FileExists(destination));
    }

    [Fact]
    public async Task ExecuteAsync_MoveFailsAfterSourceGone_KeepsTheDestination()
    {
        var source = @"C:\photos\a.jpg";
        var destination = @"C:\photos\sel\a.jpg";
        _fs.AddFile(source, "hello photo", new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc));
        // The source is already gone: a shorter destination is then the only copy left and must never be deleted.
        _fs.MoveHook = (from, to) =>
        {
            _fs.Delete(from);
            _fs.AddFile(to, "hel");
            return new IOException("simulated failure after source removal");
        };

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, "sel"));

        Assert.False(result.Succeeded);
        Assert.True(_fs.FileExists(destination));
    }
}
