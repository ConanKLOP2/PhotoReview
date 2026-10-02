﻿using System.IO;
using System.Threading.Tasks;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;
using Xunit;

namespace PhotoReview.Core.Tests.FileActions;

[Trait("Category", "HotPath")]
public sealed class UndoServiceTests
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
        public bool TryRestoreResult { get; set; } = true;
        public string? LastRestoredPath { get; private set; }
        public Action? OnTryRestore { get; set; }

        public void SendToRecycleBin(string path) => RecycledPaths.Add(path);

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
        {
            LastRestoredPath = originalPath;
            OnTryRestore?.Invoke();
            return TryRestoreResult;
        }
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly FakeClock _clock = new(new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc));
    private readonly OperationJournal _journal;
    private readonly FakeRecycleBin _recycleBin = new();
    private readonly FileActionService _fileActionService;
    private readonly UndoService _service;

    public UndoServiceTests()
    {
        _journal = new OperationJournal(new FakeAppPaths(@"C:\data\operations.jsonl"), _fs, _clock);
        _fileActionService = new FileActionService(_journal, _fs, _clock, _recycleBin);
        _service = new UndoService(_journal, _fs, _recycleBin, _fileActionService);
    }

    [Fact]
    public async Task UndoMoveAsync_Success()
    {
        var source = @"C:\photos\photo1.jpg";
        var destination = @"C:\photos\sorted\photo1.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _fs.AddFile(destination, "image-content", writeTime);

        _journal.Append(new JournalEntry("1", FileOperationType.Move, JournalState.Committed, source, destination, 13, writeTime, _clock.UtcNow));
        // P03 (2026-09-27): Undo is session-only; the journal entry above is not picked up automatically, so
        // register the Move as the in-session path (FileActionService/UndoService.Register) would.
        _service.Register(new FileActionResult(true, FileOperationType.Move, source, destination, 13, writeTime, null));

        Assert.True(_service.CanUndoMove);
        Assert.Equal(1, _service.MoveHistoryCount);

        var result = await _service.UndoMoveAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(FileOperationType.Move, result.Operation);
        Assert.Equal(source, result.Source);
        Assert.Equal(destination, result.Destination);
        Assert.Null(result.ErrorMessage);

        Assert.True(_fs.FileExists(source));
        Assert.False(_fs.FileExists(destination));
        Assert.False(_service.CanUndoMove);
    }

    [Fact]
    public async Task UndoLastAsync_GroupMoveRestoresEveryMemberAsOneJournaledUndo()
    {
        var stamp = _clock.UtcNow.AddMinutes(-1);
        var members = new[]
        {
            new JournalGroupMember(@"C:\photos\a.jpg", @"C:\photos\selected\a.jpg", 4, stamp),
            new JournalGroupMember(@"C:\photos\a.cr2", @"C:\photos\selected\a.cr2", 8, stamp),
        };
        _fs.AddFile(members[0].Destination!, "jpeg", stamp);
        _fs.AddFile(members[1].Destination!, "raw data", stamp);
        var entry = new JournalEntry("group", FileOperationType.Move, JournalState.Committed,
            members[0].Source, members[0].Destination, members[0].Size, stamp, _clock.UtcNow,
            GroupId: "group", GroupMembers: members);
        _service.RegisterGroup(new CaptureGroupActionResult(true, false, FileOperationType.Move, "group", entry,
            members.Select(member => new CaptureGroupMemberResult(member, true, false)).ToArray(), null));

        var result = await _service.UndoLastAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(members.Select(member => member.Source), result.RestoredPaths);
        Assert.True(_fs.FileExists(members[0].Source));
        Assert.True(_fs.FileExists(members[1].Source));
        var undo = Assert.Single(_journal.ReadCommittedMoves(), item => item.Undo == true);
        Assert.Equal(2, undo.GroupMembers!.Count);
        Assert.True(undo.GroupMembers.All(member => _fs.FileExists(member.Destination!)));
    }

    [Fact]
    public async Task UndoLastAsync_GroupRecyclePartialRestore_RemainsRecoverableAndUndoable()
    {
        var stamp = _clock.UtcNow.AddMinutes(-1);
        var members = new[]
        {
            new JournalGroupMember(@"C:\photos\a.jpg", null, 4, stamp),
            new JournalGroupMember(@"C:\photos\a.cr2", null, 8, stamp),
        };
        var entry = new JournalEntry("recycled-group", FileOperationType.Recycle, JournalState.Committed,
            members[0].Source, null, members[0].Size, stamp, _clock.UtcNow,
            GroupId: "recycled-group", GroupMembers: members);
        var bin = new PartialRestoreRecycleBin(_fs, members[0].Source);
        var undo = new UndoService(_journal, _fs, bin, _fileActionService);
        undo.RegisterGroup(new CaptureGroupActionResult(true, false, FileOperationType.Recycle, "recycled-group", entry,
            members.Select(member => new CaptureGroupMemberResult(member, true, false)).ToArray(), null));

        var result = await undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.True(_fs.FileExists(members[0].Source));
        Assert.False(_fs.FileExists(members[1].Source));
        Assert.True(undo.HasLastAction);
        var failed = Assert.Single(_journal.ReadFailedOperations());
        Assert.True(failed.Undo);
        Assert.Equal(2, failed.GroupMembers!.Count);
        Assert.Equal(members.Select(member => member.Source), failed.GroupMembers.Select(member => member.Source));
    }

    private sealed class PartialRestoreRecycleBin(InMemoryFileSystem fileSystem, string successfulPath) : IRecycleBin
    {
        public void SendToRecycleBin(string path) => throw new NotSupportedException();
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
        {
            if (!string.Equals(originalPath, successfulPath, StringComparison.OrdinalIgnoreCase)) return false;
            fileSystem.AddFile(originalPath, new string('x', checked((int)expectedSize)), expectedLastWriteUtc);
            return true;
        }
    }

    [Fact(DisplayName = "Q-R small-findings #2: _moveFingerprints does not grow across register+undo cycles (successful undo drops its entry)")]
    public async Task UndoMoveAsync_RepeatedRegisterAndUndo_DoesNotLeakFingerprints()
    {
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 10; i++)
        {
            var source = $@"C:\photos\cycle-{i}.jpg";
            var destination = $@"C:\photos\sorted\cycle-{i}.jpg";
            _fs.AddFile(destination, "image-content", writeTime);
            _service.Register(new FileActionResult(true, FileOperationType.Move, source, destination, 13, writeTime, null));

            // A successfully undone move's fingerprint must not linger: nothing in the map still
            // describes it (the file is back at Source, not at Destination).
            var result = await _service.UndoMoveAsync();
            Assert.True(result.Succeeded);

            Assert.Equal(0, _service.MoveFingerprintCount);
        }
    }

    [Fact(DisplayName = "Undo of a Move is journaled Prepared before the move and Committed after it")]
    public async Task UndoMoveAsync_JournalsReverseMove_PreparedBeforeMutationThenCommitted()
    {
        var source = @"C:\photos\photo1.jpg";
        var destination = @"C:\photos\sorted\photo1.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _fs.AddFile(destination, "image-content", writeTime);
        _service.Register(new FileActionResult(true, FileOperationType.Move, source, destination, 13, writeTime, null));
        IReadOnlyList<JournalEntry>? pendingAtMutation = null;
        _fs.MoveHook = (_, _) => { pendingAtMutation = _journal.ReadPendingOperations(); return null; };

        var result = await _service.UndoMoveAsync();

        Assert.True(result.Succeeded);
        // A crash during the move would leave exactly this pending entry for startup reconcile / Recovery.
        var pending = Assert.Single(pendingAtMutation!);
        Assert.Equal(FileOperationType.Move, pending.Type);
        Assert.Equal(destination, pending.Source);
        Assert.Equal(source, pending.Destination);
        Assert.Equal(13, pending.Size);
        Assert.True(pending.Undo);
        Assert.Empty(_journal.ReadPendingOperations());
        var committed = Assert.Single(_journal.ReadCommittedMoves());
        Assert.Equal(pending.Id, committed.Id);
        Assert.True(committed.Undo);
    }

    [Fact(DisplayName = "A failed undo move is journaled Failed and the move stays undoable")]
    public async Task UndoMoveAsync_MoveThrows_JournalsFailed()
    {
        var source = @"C:\photos\photo1.jpg";
        var destination = @"C:\photos\sorted\photo1.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _fs.AddFile(destination, "image-content", writeTime);
        _service.Register(new FileActionResult(true, FileOperationType.Move, source, destination, 13, writeTime, null));
        _fs.MoveHook = (_, _) => new IOException("locked");

        var result = await _service.UndoMoveAsync();

        Assert.False(result.Succeeded);
        Assert.True(_service.CanUndoMove);
        var failed = Assert.Single(_journal.ReadFailedOperations());
        Assert.Equal(destination, failed.Source);
        Assert.True(failed.Undo);
    }

    [Fact(DisplayName = "An undo move that copied back but left the moved file is a failure (MoveSourceNotRemoved)")]
    public async Task UndoMoveAsync_MoveLeavesSource_FailsWithDistinctCode()
    {
        var source = @"C:\photos\photo1.jpg";
        var destination = @"D:\sorted\photo1.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _fs.AddFile(destination, "image-content", writeTime);
        _fs.CreateDirectory(@"C:\photos");
        _service.Register(new FileActionResult(true, FileOperationType.Move, source, destination, 13, writeTime, null));
        _fs.MoveLeavesSource = true;

        var result = await _service.UndoMoveAsync();

        Assert.False(result.Succeeded);
        Assert.True(_fs.FileExists(destination));
        Assert.Equal(JournalErrors.MoveSourceNotRemoved, Assert.Single(_journal.ReadFailedOperations()).ErrorCode);
    }

    [Fact(DisplayName = "P03: a restarted UndoService has nothing to undo, journaled undo or not")]
    public async Task Restart_AfterUndo_HasNothingToUndo()
    {
        var source = @"C:\photos\photo1.jpg";
        var destination = @"C:\photos\sorted\photo1.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _fs.AddFile(destination, "image-content", writeTime);
        _journal.Append(new JournalEntry("1", FileOperationType.Move, JournalState.Committed, source, destination, 13, writeTime, _clock.UtcNow));
        _service.Register(new FileActionResult(true, FileOperationType.Move, source, destination, 13, writeTime, null));
        Assert.True((await _service.UndoMoveAsync()).Succeeded);

        // Simulates a restart: a brand-new UndoService over the same (now Committed-undo-laden) journal.
        var restarted = new UndoService(_journal, _fs, _recycleBin);

        Assert.False(restarted.CanUndoMove);
    }

    [Fact(DisplayName = "In-session undo retains more than the startup journal tail")]
    public async Task RegisterMoreThanStartupTail_UndoLatestUsesRegisteredFingerprint()
    {
        var writeTime = _clock.UtcNow.AddMinutes(-1);
        for (var i = 0; i < 250; i++)
        {
            var source = $@"C:\photos\source-{i}.jpg";
            var destination = $@"C:\photos\sorted\source-{i}.jpg";
            _fs.AddFile(destination, new string('x', 7 + i), writeTime);
            _journal.Append(new JournalEntry($"move-{i}", FileOperationType.Move, JournalState.Committed,
                source, destination, 7 + i, writeTime, _clock.UtcNow));
            _service.Register(new FileActionResult(true, FileOperationType.Move, source, destination,
                7 + i, writeTime, null));
        }

        Assert.Equal(250, _service.MoveHistoryCount);
        var result = await _service.UndoMoveAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(@"C:\photos\source-249.jpg", result.Source);
        Assert.Equal(249, _service.MoveHistoryCount);
    }

    [Fact]
    public async Task UndoMoveAsync_DestinationModified_FailsAndRestoresStack()
    {
        var source = @"C:\photos\photo1.jpg";
        var destination = @"C:\photos\sorted\photo1.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _fs.AddFile(destination, "modified-longer-content", writeTime);

        _journal.Append(new JournalEntry("1", FileOperationType.Move, JournalState.Committed, source, destination, 10, writeTime, _clock.UtcNow));
        _service.Register(new FileActionResult(true, FileOperationType.Move, source, destination, 10, writeTime, null));

        var result = await _service.UndoMoveAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("Tệp đích đã thay đổi sau khi Di chuyển", result.ErrorMessage);
        Assert.True(_service.CanUndoMove);
        Assert.False(_fs.FileExists(source));
        Assert.True(_fs.FileExists(destination));
    }

    [Fact]
    public async Task UndoMoveAsync_SourceAlreadyExists_FailsAndRestoresStack()
    {
        var source = @"C:\photos\photo1.jpg";
        var destination = @"C:\photos\sorted\photo1.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _fs.AddFile(source, "source-already-there", writeTime);
        _fs.AddFile(destination, "dest", writeTime);

        _service.Register(new FileActionResult(true, FileOperationType.Move, source, destination, 4, writeTime, null));

        var result = await _service.UndoMoveAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("Nguồn hoặc đích đã thay đổi", result.ErrorMessage);
        Assert.True(_service.CanUndoMove);
    }

    [Fact(DisplayName = "A Move whose destination was deleted outside the app is reported once and dropped, so older moves stay undoable")]
    public async Task UndoMoveAsync_DestinationDeletedExternally_ReportsOnceAndDropsEntry()
    {
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        var oldSource = @"C:\photos\old.jpg";
        var oldDestination = @"C:\photos\sorted\old.jpg";
        var brokenSource = @"C:\photos\broken.jpg";
        var brokenDestination = @"C:\photos\sorted\broken.jpg";
        _fs.AddFile(oldDestination, "old!", writeTime);
        _fs.AddFile(brokenDestination, "brok", writeTime);
        _service.Register(new FileActionResult(true, FileOperationType.Move, oldSource, oldDestination, 4, writeTime, null));
        _service.Register(new FileActionResult(true, FileOperationType.Move, brokenSource, brokenDestination, 4, writeTime, null));
        _fs.Delete(brokenDestination);

        var first = await _service.UndoLastAsync();

        Assert.False(first.Succeeded);
        Assert.Equal(1, _service.MoveHistoryCount);
        Assert.False(_service.HasLastAction);

        var second = await _service.UndoMoveAsync();

        Assert.True(second.Succeeded);
        Assert.Equal(oldSource, second.Source);
        Assert.True(_fs.FileExists(oldSource));
        Assert.Equal(0, _service.MoveHistoryCount);
    }

    [Fact(DisplayName = "A Move whose source name is temporarily occupied stays in the history")]
    public async Task UndoMoveAsync_SourceOccupied_KeepsEntryUntilSourceFreed()
    {
        var source = @"C:\photos\photo1.jpg";
        var destination = @"C:\photos\sorted\photo1.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _fs.AddFile(source, "blocker", writeTime);
        _fs.AddFile(destination, "dest", writeTime);
        _service.Register(new FileActionResult(true, FileOperationType.Move, source, destination, 4, writeTime, null));

        var blocked = await _service.UndoLastAsync();
        Assert.False(blocked.Succeeded);
        Assert.True(_service.HasLastAction);

        _fs.Delete(source);
        var retry = await _service.UndoLastAsync();

        Assert.True(retry.Succeeded);
    }

    [Fact]
    public async Task UndoMoveAsync_CaseInsensitiveDestinationMatch_P12()
    {
        var source = @"C:\Photos\Photo1.JPG";
        var destinationInJournal = @"C:\Photos\Sorted\Photo1.JPG";
        var destinationInMove = @"c:\photos\sorted\photo1.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _fs.AddFile(destinationInMove, "test-data", writeTime);

        _journal.Append(new JournalEntry("1", FileOperationType.Move, JournalState.Committed, source, destinationInJournal, 9, writeTime, _clock.UtcNow));
        _service.Register(new FileActionResult(true, FileOperationType.Move, source, destinationInMove, 9, writeTime, null));

        var result = await _service.UndoMoveAsync();

        Assert.True(result.Succeeded);
        Assert.True(_fs.FileExists(source));
    }

    [Fact]
    public async Task UndoMoveAsync_EmptyHistory_ReturnsError()
    {
        var result = await _service.UndoMoveAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("Không có thao tác Di chuyển nào để hoàn tác.", result.ErrorMessage);
    }

    [Fact]
    public async Task Register_Recycle_SetsLastAction_UndoLastRestores()
    {
        var source = @"C:\photos\deleted.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);

        _service.Register(new FileActionResult(true, FileOperationType.Recycle, source, null, 100, writeTime, null));

        Assert.True(_service.HasLastAction);
        Assert.False(_service.CanUndoMove);

        var result = await _service.UndoLastAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(FileOperationType.Recycle, result.Operation);
        Assert.Equal(source, result.Source);
        Assert.Equal(source, _recycleBin.LastRestoredPath);
        Assert.False(_service.HasLastAction);
    }

    [Fact]
    public async Task UndoLastAsync_Recycle_FailsWhenRestoreReturnsFalse()
    {
        var source = @"C:\photos\deleted.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _recycleBin.TryRestoreResult = false;

        _service.Register(new FileActionResult(true, FileOperationType.Recycle, source, null, 100, writeTime, null));

        var result = await _service.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("Không thể khôi phục từ Thùng rác", result.ErrorMessage);
    }

    [Fact(DisplayName = "C5: Undo of a Recycle whose restore reported failure but whose file is back at the source counts as success")]
    public async Task UndoLastAsync_Recycle_RestoreReportsFailureButFileIsBack_Succeeds()
    {
        var source = @"C:\photos\deleted.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _recycleBin.TryRestoreResult = false;
        _recycleBin.OnTryRestore = () => _fs.AddFile(source, new string('x', 100), writeTime);
        _service.Register(new FileActionResult(true, FileOperationType.Recycle, source, null, 100, writeTime, null));

        var result = await _service.UndoLastAsync();

        Assert.True(result.Succeeded);
        Assert.False(_service.HasLastAction);
    }

    [Fact(DisplayName = "Undo of a Recycle refuses to restore over a newer file at the original path and keeps the action")]
    public async Task UndoLastAsync_Recycle_SourceOccupied_DoesNotRestore()
    {
        var source = @"C:\photos\deleted.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _fs.AddFile(source, "a newer file", writeTime);
        _service.Register(new FileActionResult(true, FileOperationType.Recycle, source, null, 100, writeTime, null));

        var result = await _service.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.Null(_recycleBin.LastRestoredPath);
        Assert.True(_service.HasLastAction);
        Assert.Contains("deleted.jpg", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndoLastAsync_NoLastAction_ReturnsError()
    {
        var result = await _service.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("Không có thao tác Di chuyển/Xóa nào vừa thực hiện để hoàn tác.", result.ErrorMessage);
    }

    [Fact]
    public async Task UndoMoveAsync_WhenGateBusy_ReturnsRejected()
    {
        _fileActionService.TryBegin();
        Assert.True(_service.IsBusy);

        var result = await _service.UndoMoveAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.Rejected);
        Assert.Contains("Đang bận", result.ErrorMessage);

        _fileActionService.End();
        Assert.False(_service.IsBusy);
    }

    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }

    [Fact]
    public async Task UndoLastAsync_DoesNotResumeOnCapturedContext()
    {
        var source = @"C:\photos\photo1.jpg";
        var destination = @"C:\photos\sorted\photo1.jpg";
        var recycled = @"C:\photos\deleted.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _fs.AddFile(destination, "image-content", writeTime);

        var context = new CountingSynchronizationContext();
        var results = await Task.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            _service.Register(new FileActionResult(true, FileOperationType.Move, source, destination, 13, writeTime, null));
            var move = _service.UndoLastAsync().GetAwaiter().GetResult();
            _service.Register(new FileActionResult(true, FileOperationType.Recycle, recycled, null, 100, writeTime, null));
            var recycle = _service.UndoLastAsync().GetAwaiter().GetResult();
            return (move, recycle);
        });

        Assert.True(results.move.Succeeded);
        Assert.True(results.recycle.Succeeded);
        Assert.Equal(0, context.Posts);
    }

    // RV-C01: a FAT destination volume stores write times rounded to 2 s, so a moved file's stamp there is never exactly the
    // stamp the source had (the fingerprint Undo compares against).
    private static DateTime RoundToFat(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.FromSeconds(2).Ticks, DateTimeKind.Utc);

    private const string FatSource = @"C:\photos\fat.jpg";
    private const string FatDestination = @"F:\sorted\fat.jpg";
    private static readonly DateTime OddStamp = new DateTime(2026, 9, 19, 9, 0, 1, DateTimeKind.Utc).AddMilliseconds(735);

    private async Task<FileActionResult> MoveToFatAsync()
    {
        _fs.StampOnMove = (destination, stamp) =>
            destination.StartsWith(@"F:\", StringComparison.OrdinalIgnoreCase) ? RoundToFat(stamp) : stamp;
        _fs.AddFile(FatSource, "image-content", OddStamp);
        var moved = await _fileActionService.ExecuteAsync(new FileActionRequest(FatSource, FileOperationType.Move, @"F:\sorted"));
        Assert.True(moved.Succeeded, moved.Error);
        Assert.NotEqual(OddStamp, _fs.GetFileStat(FatDestination)!.LastWriteUtc); // precondition: the stamp really was rounded
        _service.Register(moved);
        return moved;
    }

    [Fact]
    public async Task UndoMoveAsync_DestinationOnFatRoundedStamp_RestoresFile()
    {
        await MoveToFatAsync();

        var result = await _service.UndoMoveAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(_fs.FileExists(FatSource));
        Assert.False(_fs.FileExists(FatDestination));
    }

    [Fact]
    public async Task UndoMoveAsync_DestinationSizeChanged_Refuses()
    {
        await MoveToFatAsync();
        _fs.AddFile(FatDestination, "image-content-edited", RoundToFat(OddStamp));

        var result = await _service.UndoMoveAsync();

        Assert.False(result.Succeeded);
        Assert.False(_fs.FileExists(FatSource));
        Assert.True(_fs.FileExists(FatDestination));
        Assert.True(_service.CanUndoMove);
    }

    [Fact]
    public async Task UndoMoveAsync_DestinationStampMovedBy3Seconds_Refuses()
    {
        await MoveToFatAsync();
        // Same size, but written 3 s after the recorded stamp: more than any volume rounds, so it is another file.
        _fs.AddFile(FatDestination, "image-CONTENT", OddStamp.AddSeconds(3));

        var result = await _service.UndoMoveAsync();

        Assert.False(result.Succeeded);
        Assert.False(_fs.FileExists(FatSource));
        Assert.True(_fs.FileExists(FatDestination));
        Assert.True(_service.CanUndoMove);
    }

    [Fact]
    public async Task UndoMoveAsync_CalledDirectlyWhileLastActionIsRecycle_KeepsRecycleUndo()
    {
        // RV-C06: UndoMoveAsync pops the Move history; it must not forget a later, unrelated Recycle.
        var source = @"C:\photos\photo1.jpg";
        var destination = @"C:\photos\sorted\photo1.jpg";
        var recycled = @"C:\photos\deleted.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _fs.AddFile(destination, "image-content", writeTime);
        _service.Register(new FileActionResult(true, FileOperationType.Move, source, destination, 13, writeTime, null));
        _service.Register(new FileActionResult(true, FileOperationType.Recycle, recycled, null, 100, writeTime, null));

        var move = await _service.UndoMoveAsync();
        Assert.True(move.Succeeded, move.ErrorMessage);

        Assert.True(_service.HasLastAction);
        var recycle = await _service.UndoLastAsync();
        Assert.True(recycle.Succeeded, recycle.ErrorMessage);
        Assert.Equal(FileOperationType.Recycle, recycle.Operation);
        Assert.Equal(recycled, _recycleBin.LastRestoredPath);
    }

    [Fact]
    public async Task UndoMoveAsync_SourceFolderDeletedAfterMove_RecreatesFolderAndRestores()
    {
        // RV-C07: the original folder was removed after the Move (it became empty); the group undo recreates it, so must this.
        var source = @"C:\photos\day1\photo1.jpg";
        var destination = @"D:\sorted\photo1.jpg";
        _fs.AddFile(destination, "image-content", OddStamp);
        Assert.False(_fs.DirectoryExists(@"C:\photos\day1"));
        _service.Register(new FileActionResult(true, FileOperationType.Move, source, destination, 13, OddStamp, null));

        var result = await _service.UndoMoveAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(_fs.FileExists(source));
        Assert.False(_fs.FileExists(destination));
    }
}

