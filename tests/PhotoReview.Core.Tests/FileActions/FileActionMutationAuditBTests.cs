using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Audit B (data safety, manual mutation testing 2026-10-10): tests that kill the mutants that survived the existing suite.
/// Each test names the mutation it pins. Fakes only; the real Recycle Bin is never touched.
/// </summary>
public sealed class FileActionMutationAuditBTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);
    private const string Jpeg = @"C:\photos\a.jpg";
    private const string Raw = @"C:\photos\a.cr2";

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow => now;
    }

    /// <summary>Fake bin whose "restore" reports success and puts back a file with a chosen size and write time (or nothing).</summary>
    private sealed class PutBackBin(InMemoryFileSystem fs) : IRecycleBin
    {
        public DateTime PutBackStamp { get; set; }
        public int SizeDelta { get; set; }
        public int RestoreCalls { get; private set; }
        public void SendToRecycleBin(string path) => throw new NotSupportedException();

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
        {
            RestoreCalls++;
            fs.AddFile(originalPath, new string('x', checked((int)expectedSize) + SizeDelta), PutBackStamp);
            return true;
        }
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly OperationJournal _journal;
    private readonly PutBackBin _bin;
    private readonly FileActionService _service;
    private readonly UndoService _undo;

    public FileActionMutationAuditBTests()
    {
        var clock = new FixedClock(Stamp);
        _journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), _fs, clock);
        _bin = new PutBackBin(_fs);
        _service = new FileActionService(_journal, _fs, clock, _bin);
        _undo = new UndoService(_journal, _fs, _bin, _service, clock: clock);
    }

    private static CaptureGroupActionResult RecycledGroup(params JournalGroupMember[] members)
    {
        var entry = new JournalEntry("g", FileOperationType.Recycle, JournalState.Committed, members[0].Source, null,
            members[0].Size, Stamp, Stamp, GroupId: "g", GroupMembers: members);
        return new(true, false, FileOperationType.Recycle, "g", entry,
            members.Select(member => new CaptureGroupMemberResult(member, true, false)).ToArray(), null);
    }

    private static CaptureGroupActionResult MovedGroup(params JournalGroupMember[] members)
    {
        var entry = new JournalEntry("g", FileOperationType.Move, JournalState.Committed, members[0].Source, members[0].Destination,
            members[0].Size, Stamp, Stamp, GroupId: "g", GroupMembers: members);
        return new(true, false, FileOperationType.Move, "g", entry,
            members.Select(member => new CaptureGroupMemberResult(member, true, false)).ToArray(), null);
    }

    // ---- M21b: group Move undo re-proves EVERY file's identity before the first move ----

    [Fact]
    public async Task UndoMove_Group_SecondFileReplacedAfterPreflight_NothingIsRestored()
    {
        // Arrange: a Move of two files, both at their destination, undo registered.
        const string movedJpeg = @"C:\photos\selected\a.jpg";
        const string movedRaw = @"C:\photos\selected\a.cr2";
        _fs.AddFile(movedJpeg, "jpeg", Stamp);
        _fs.AddFile(movedRaw, "raw!", Stamp);
        _undo.RegisterGroup(MovedGroup(
            new JournalGroupMember(Jpeg, movedJpeg, 4, Stamp),
            new JournalGroupMember(Raw, movedRaw, 4, Stamp)));
        // The preflight passed; a foreign file (same size, other write time) takes the second path just as Prepared is journaled.
        var replaced = false;
        _fs.OpenAppendHook = _ =>
        {
            if (!replaced)
            {
                replaced = true;
                _fs.AddFile(movedRaw, "evil", Stamp.AddHours(2));
            }
            return null;
        };

        // Act
        var result = await _undo.UndoLastAsync();

        // Assert: the whole undo fails BEFORE the first move; no partial capture, the foreign file is untouched.
        Assert.True(replaced);
        Assert.False(result.Succeeded);
        Assert.False(_fs.FileExists(Jpeg));
        Assert.True(_fs.FileExists(movedJpeg));
        Assert.False(_fs.FileExists(Raw));
        Assert.Equal(Stamp.AddHours(2), _fs.GetFileStat(movedRaw)!.LastWriteUtc);
    }

    // ---- M21c: group Move undo re-proves each file's identity right before ITS move ----

    [Fact]
    public async Task UndoMove_Group_SecondFileReplacedAfterTheFirstMove_ForeignFileIsNeverRestored()
    {
        // Arrange: the preflight and the up-front pass succeed; the replacement happens while the first file is being moved.
        const string movedJpeg = @"C:\photos\selected\a.jpg";
        const string movedRaw = @"C:\photos\selected\a.cr2";
        _fs.AddFile(movedJpeg, "jpeg", Stamp);
        _fs.AddFile(movedRaw, "raw!", Stamp);
        var calls = 0;
        var undo = new UndoService(_journal, _fs, _bin, _service, (source, destination) =>
        {
            calls++;
            _fs.Move(source, destination);
            if (calls == 1) _fs.AddFile(movedRaw, "evil", Stamp.AddHours(2));
            return Task.CompletedTask;
        }, new FixedClock(Stamp));
        undo.RegisterGroup(MovedGroup(
            new JournalGroupMember(Jpeg, movedJpeg, 4, Stamp),
            new JournalGroupMember(Raw, movedRaw, 4, Stamp)));

        // Act
        var result = await undo.UndoLastAsync();

        // Assert: the foreign file stays where it is, nothing is restored under this undo's name.
        Assert.Equal(1, calls);
        Assert.False(result.Succeeded);
        Assert.False(_fs.FileExists(Raw));
        Assert.Equal(Stamp.AddHours(2), _fs.GetFileStat(movedRaw)!.LastWriteUtc);
    }

    // ---- M23: PartialDestinationCleanup deletes only a STRICTLY shorter destination ----

    [Fact]
    public void RemoveIfPartial_DestinationAsLongAsTheSource_IsKeptForRecoveryToJudge()
    {
        const string destination = @"C:\photos\selected\a.jpg";
        _fs.AddFile(destination, "jpeg", Stamp);
        var created = _fs.GetFileStat(destination)!;

        PartialDestinationCleanup.RemoveIfPartial(_fs, destination, sourceSize: 4, created);

        Assert.True(_fs.FileExists(destination)); // complete copy: never deleted by the cleanup
    }

    [Fact]
    public void RemoveIfPartial_DestinationShorterThanTheSource_IsDeleted()
    {
        const string destination = @"C:\photos\selected\a.jpg";
        _fs.AddFile(destination, "jp", Stamp);
        var created = _fs.GetFileStat(destination)!;

        PartialDestinationCleanup.RemoveIfPartial(_fs, destination, sourceSize: 4, created);

        Assert.False(_fs.FileExists(destination));
    }

    /// <summary>Fake shell: "Send to Recycle Bin" reports success but leaves the file in place; per-path no-bin switch for permanent deletes.</summary>
    private sealed class ShellBin(InMemoryFileSystem fs) : IRecycleBin
    {
        public bool SendLeavesFile { get; set; }
        public HashSet<string> NoBin { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Recycled { get; } = [];
        public List<string> PermanentlyDeleted { get; } = [];

        public bool CanRecycle(string path) => !NoBin.Contains(path);

        public void SendToRecycleBin(string path)
        {
            Recycled.Add(path);
            if (!SendLeavesFile) fs.Delete(path);
        }

        public void DeletePermanently(string path)
        {
            PermanentlyDeleted.Add(path);
            fs.Delete(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    // ---- M28: group Recycle verifies that every member really left its source ----

    [Fact]
    public async Task ExecuteGroupAsync_Recycle_ShellReportsSuccessButFileIsStillThere_FailsAndIsNotCommitted()
    {
        // Arrange
        _fs.AddFile(Jpeg, "jpeg", Stamp);
        _fs.AddFile(Raw, "raw!", Stamp);
        var bin = new ShellBin(_fs) { SendLeavesFile = true };
        var service = new FileActionService(_journal, _fs, new FixedClock(Stamp), bin);

        // Act
        var result = await service.ExecuteGroupAsync(new CaptureGroupActionRequest(new CaptureGroup(Jpeg, Raw), FileOperationType.Recycle, null));

        // Assert: nothing is reported as recycled; the journal keeps one Failed Recovery item, no Committed line.
        Assert.False(result.Succeeded);
        Assert.False(result.PermanentlyDeleted);
        Assert.Single(bin.Recycled); // stopped at the first member
        Assert.True(_fs.FileExists(Jpeg));
        Assert.True(_fs.FileExists(Raw));
        var failed = Assert.Single(_journal.ReadFailedOperations());
        Assert.Equal(JournalErrors.SourceStillExistsAfterRecovery, failed.ErrorCode);
    }

    // ---- M30: a retry re-asks the bin right before deleting a journaled-Permanent member ----

    [Fact]
    public async Task RetryGroup_PermanentMemberWhoseDriveGainedARecycleBinAfterThePreCheck_IsNotDeleted()
    {
        // Arrange: Raw was deleted permanently on a drive without a bin; the group failed part-way and is retried with the setting on.
        _fs.AddFile(Jpeg, new string('j', 10), Stamp);
        _fs.AddFile(Raw, new string('r', 10), Stamp);
        var bin = new ShellBin(_fs);
        bin.NoBin.Add(Raw);
        var failed = new JournalEntry("delete-group", FileOperationType.Recycle, JournalState.Failed, Jpeg, null, 10, Stamp, Stamp,
            Error: "x", ErrorCode: JournalErrors.SourceStillExistsAfterRecovery, Permanent: true, GroupId: "capture-a",
            GroupMembers: [new JournalGroupMember(Jpeg, null, 10, Stamp), new JournalGroupMember(Raw, null, 10, Stamp, true)]);
        _journal.Append(failed);
        var service = new RecoveryRetryService(_journal, _fs, new FixedClock(Stamp.AddMinutes(2)), bin, () => true);
        // The pre-checks are done once Prepared is appended: the drive "gets" a Recycle Bin right then.
        _fs.OpenAppendHook = _ =>
        {
            bin.NoBin.Clear();
            return null;
        };

        // Act
        var result = await service.RetryMoveOrCopyAsync(failed);

        // Assert: the journaled "permanent" verdict no longer holds, so nothing is permanently deleted.
        Assert.False(result.Succeeded);
        Assert.Empty(bin.PermanentlyDeleted);
        Assert.True(_fs.FileExists(Raw));
    }

    // ---- M18: Undo Recycle (single) proves the restore by size only ----

    [Fact]
    public async Task UndoRecycle_Single_BinPutsBackSameSizeFileWithOtherWriteTime_IsNotAnUndoAndStaysRetryable()
    {
        // Arrange: a Recycle of a 4-byte file stamped Stamp; the bin "restores" a DIFFERENT file of the same size.
        _bin.PutBackStamp = Stamp.AddHours(1);
        _undo.Register(new FileActionResult(true, FileOperationType.Recycle, Jpeg, null, 4, Stamp, null));

        // Act
        var result = await _undo.UndoLastAsync();

        // Assert: the recycled file itself (size AND write time) is not at the path, so this is not a successful undo.
        Assert.False(result.Succeeded);
        Assert.Equal(1, _bin.RestoreCalls);
        Assert.NotNull(_undo.LastUndoAction); // still retryable
    }

    [Fact]
    public async Task UndoRecycle_Single_BinPutsBackTheRecycledFile_IsASuccessfulUndo()
    {
        _bin.PutBackStamp = Stamp;
        _undo.Register(new FileActionResult(true, FileOperationType.Recycle, Jpeg, null, 4, Stamp, null));

        var result = await _undo.UndoLastAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Null(_undo.LastUndoAction);
    }

    // ---- M18b: Undo Recycle (group) proves the restore by size only ----

    [Fact]
    public async Task UndoRecycle_Group_BinPutsBackSameSizeFileWithOtherWriteTime_IsNotAnUndo()
    {
        _bin.PutBackStamp = Stamp.AddHours(1);
        _undo.RegisterGroup(RecycledGroup(
            new JournalGroupMember(Jpeg, null, 4, Stamp, false),
            new JournalGroupMember(Raw, null, 4, Stamp, false)));

        var result = await _undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.RestoredPaths is null or { Count: 0 });
        Assert.NotNull(_undo.LastUndoAction);
    }
}
