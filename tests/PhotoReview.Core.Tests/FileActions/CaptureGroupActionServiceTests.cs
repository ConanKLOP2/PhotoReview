using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

public sealed class CaptureGroupActionServiceTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ExecuteGroupAsync_PreflightsEverySourceBeforeJournalOrMutation()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllTextAtomic(@"C:\photos\a.jpg", "jpeg");
        fileSystem.WriteAllTextAtomic(@"C:\photos\a.cr2", "raw data");
        fileSystem.WriteAllTextAtomic(@"C:\photos\selected\a.cr2", "already there"); // second member's destination exists
        var journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), fileSystem, new FixedClock());
        var service = CreateService(fileSystem, journal);

        var result = await service.ExecuteGroupAsync(new CaptureGroupActionRequest(
            new CaptureGroup(@"C:\photos\a.jpg", @"C:\photos\a.cr2"), FileOperationType.Move, "selected"));

        Assert.False(result.Succeeded);
        Assert.Empty(result.Members);
        Assert.True(fileSystem.FileExists(@"C:\photos\a.jpg"));
        Assert.False(fileSystem.FileExists(@"C:\photos\selected\a.jpg"));
        Assert.Empty(journal.ReadPendingAndFailedOperations());
        Assert.DoesNotContain(fileSystem.Events, item => item.Kind is "move" or "copy");
    }

    [Fact]
    public async Task ExecuteGroupAsync_PreparedJournalAppendFails_DoesNotMutateAnyMember()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\photos\a.jpg", "jpeg", Stamp);
        fileSystem.AddFile(@"C:\photos\a.cr2", "raw data", Stamp);
        var paths = new AppPaths(@"C:\Users\test\AppData\Local");
        var journalPath = paths.JournalFile;
        fileSystem.OpenAppendHook = path => string.Equals(path, journalPath, StringComparison.OrdinalIgnoreCase)
            ? new IOException("simulated journal append failure")
            : null;
        var journal = new OperationJournal(paths, fileSystem, new FixedClock());
        var service = CreateService(fileSystem, journal);

        var result = await service.ExecuteGroupAsync(new CaptureGroupActionRequest(
            new CaptureGroup(@"C:\photos\a.jpg", @"C:\photos\a.cr2"), FileOperationType.Move, "selected"));

        Assert.False(result.Succeeded);
        Assert.True(fileSystem.FileExists(@"C:\photos\a.jpg"));
        Assert.True(fileSystem.FileExists(@"C:\photos\a.cr2"));
        Assert.False(fileSystem.FileExists(@"C:\photos\selected\a.jpg"));
        Assert.False(fileSystem.FileExists(@"C:\photos\selected\a.cr2"));
        Assert.DoesNotContain(fileSystem.Events, item => item.Kind is "move" or "copy");
    }

    [Fact]
    public async Task ExecuteGroupAsync_PreparesManifestBeforeMovingAndRollsBackFirstMemberOnFailure()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\photos\a.jpg", "jpeg", Stamp);
        fileSystem.AddFile(@"C:\photos\a.cr2", "raw data", Stamp);
        var journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), fileSystem, new FixedClock());
        var sawCompletePreparedManifest = false;
        fileSystem.MoveHook = (source, _) => source.EndsWith(".cr2", StringComparison.OrdinalIgnoreCase)
            ? new IOException("simulated second-member failure")
            : null;
        var service = CreateService(fileSystem, journal, async (source, destination) =>
        {
            var prepared = Assert.Single(journal.ReadPendingOperations());
            Assert.Equal(2, prepared.GroupMembers!.Count);
            sawCompletePreparedManifest = true;
            await Task.Run(() => fileSystem.Move(source, destination));
        });

        var result = await service.ExecuteGroupAsync(new CaptureGroupActionRequest(
            new CaptureGroup(@"C:\photos\a.jpg", @"C:\photos\a.cr2"), FileOperationType.Move, "selected"));

        Assert.False(result.Succeeded);
        Assert.True(sawCompletePreparedManifest);
        Assert.False(result.Members[0].Completed); // rolled back: nothing of the pair is left moved
        Assert.False(result.Members[1].Completed);
        Assert.False(result.Members[1].Conflict);
        Assert.True(fileSystem.FileExists(@"C:\photos\a.jpg"));
        Assert.True(fileSystem.FileExists(@"C:\photos\a.cr2"));
        Assert.False(fileSystem.FileExists(@"C:\photos\selected\a.jpg"));
        var failed = Assert.Single(journal.ReadFailedOperations());
        Assert.Equal(result.GroupId, failed.GroupId);
        Assert.Equal(2, failed.GroupMembers!.Count);
    }

    [Fact]
    public void RecoveryCheck_GroupAggregatesCompletedRetryableAndConflictingMembers()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\photos\a.jpg", "jpeg", Stamp);
        fileSystem.AddFile(@"C:\photos\a.cr2", "raw data", Stamp);
        var members = new[]
        {
            new JournalGroupMember(@"C:\photos\a.jpg", @"C:\photos\selected\a.jpg", 4, Stamp),
            new JournalGroupMember(@"C:\photos\a.cr2", @"C:\photos\selected\a.cr2", 8, Stamp),
        };
        var entry = new JournalEntry("group", FileOperationType.Move, JournalState.Failed,
            members[0].Source, members[0].Destination, members[0].Size, members[0].LastWriteUtc, Stamp,
            GroupId: "group", GroupMembers: members);
        var checker = new RecoveryFileCheck(fileSystem);

        var retryable = checker.Check(entry);
        Assert.Equal(RecoveryVerdict.CanRetry, retryable.Verdict);
        Assert.Equal(2, retryable.GroupMembers!.Count);
        Assert.Equal(RecoveryPathStatus.Exists, retryable.GroupMembers[0].Source.Status);
        Assert.Equal(RecoveryPathStatus.Missing, retryable.GroupMembers[0].Destination!.Status);

        fileSystem.CreateDirectory(@"C:\photos\selected");
        fileSystem.Move(members[0].Source, members[0].Destination!);
        Assert.Equal(RecoveryVerdict.CanRetry, checker.Check(entry).Verdict);

        fileSystem.WriteAllTextAtomic(members[1].Destination!, "unrelated");
        Assert.Equal(RecoveryVerdict.Conflict, checker.Check(entry).Verdict);
    }

    [Fact]
    public async Task RecoveryRetry_GroupSkipsCompletedMembersAndRetriesRemainingMembers()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\photos\a.jpg", "jpeg", Stamp);
        fileSystem.AddFile(@"C:\photos\a.cr2", "raw data", Stamp);
        var clock = new FixedClock();
        var journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), fileSystem, clock);
        var members = new[]
        {
            new JournalGroupMember(@"C:\photos\a.jpg", @"C:\photos\selected\a.jpg", 4, Stamp),
            new JournalGroupMember(@"C:\photos\a.cr2", @"C:\photos\selected\a.cr2", 8, Stamp),
        };
        var entry = new JournalEntry("group", FileOperationType.Move, JournalState.Failed,
            members[0].Source, members[0].Destination, members[0].Size, members[0].LastWriteUtc, Stamp,
            GroupId: "group", GroupMembers: members);
        fileSystem.CreateDirectory(@"C:\photos\selected");
        fileSystem.Move(members[0].Source, members[0].Destination!);
        journal.Append(entry with { TimestampUtc = Stamp.AddSeconds(-1) });
        journal.Append(entry);

        var result = await new RecoveryRetryService(journal, fileSystem, clock).RetryMoveOrCopyAsync(entry);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(fileSystem.FileExists(members[1].Source));
        Assert.True(fileSystem.FileExists(members[1].Destination!));
        Assert.True(fileSystem.FileExists(members[0].Destination!));
        var committed = Assert.Single(journal.ReadCommittedMoves());
        Assert.Equal(2, committed.GroupMembers!.Count);
    }

    [Fact]
    public async Task RecoveryRetry_GroupUndoMoveReversesManifestAndSkipsRestoredMember()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\photos\selected\a.jpg", "jpeg", Stamp);
        fileSystem.AddFile(@"C:\photos\selected\a.cr2", "raw data", Stamp);
        var clock = new FixedClock();
        var journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), fileSystem, clock);
        var members = new[]
        {
            new JournalGroupMember(@"C:\photos\selected\a.jpg", @"C:\photos\a.jpg", 4, Stamp),
            new JournalGroupMember(@"C:\photos\selected\a.cr2", @"C:\photos\a.cr2", 8, Stamp),
        };
        var failed = new JournalEntry("undo-group", FileOperationType.Move, JournalState.Failed,
            members[0].Source, members[0].Destination, 4, Stamp, Stamp, Undo: true,
            GroupId: "undo", GroupMembers: members);
        journal.Append(failed with { TimestampUtc = Stamp.AddSeconds(-1) });
        journal.Append(failed);
        fileSystem.CreateDirectory(@"C:\photos");
        var result = await new RecoveryRetryService(journal, fileSystem, clock).RetryMoveOrCopyAsync(failed);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(fileSystem.FileExists(@"C:\photos\selected\a.jpg"));
        Assert.True(fileSystem.FileExists(@"C:\photos\a.jpg"));
        Assert.False(fileSystem.FileExists(@"C:\photos\selected\a.cr2"));
        Assert.True(fileSystem.FileExists(@"C:\photos\a.cr2"));
        var committed = Assert.Single(journal.ReadCommittedMoves());
        Assert.True(committed.Undo);
    }

    [Fact]
    public async Task RecoveryRetry_GroupUndoRecycleUsesRestoreInsteadOfRecyclingAgain()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.CreateDirectory(@"C:\photos");
        var clock = new FixedClock();
        var journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), fileSystem, clock);
        var bin = new RestoringRecycleBin(fileSystem);
        var member = new JournalGroupMember(@"C:\photos\a.jpg", null, 4, Stamp);
        var failed = new JournalEntry("undo-recycle", FileOperationType.Recycle, JournalState.Failed,
            member.Source, null, member.Size, Stamp, Stamp, Undo: true, GroupId: "undo", GroupMembers: [member]);
        journal.Append(failed with { TimestampUtc = Stamp.AddSeconds(-1) });
        journal.Append(failed);
        var retry = new RecoveryRetryService(journal, fileSystem, clock, bin);

        var result = await retry.RetryMoveOrCopyAsync(failed);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, bin.RestoreCalls);
        Assert.Equal(0, bin.RecycleCalls);
        Assert.True(fileSystem.FileExists(member.Source));
    }

    [Fact]
    public async Task RecoveryRetry_GroupUndoMovePreflightsAllPendingTargetsBeforeMutation()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\photos\selected\a.jpg", "jpeg", Stamp);
        fileSystem.AddFile(@"C:\photos\selected\a.cr2", "raw data", Stamp);
        fileSystem.AddFile(@"C:\photos\a.cr2", "new unrelated file", Stamp);
        var clock = new FixedClock();
        var journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), fileSystem, clock);
        var members = new[]
        {
            new JournalGroupMember(@"C:\photos\selected\a.jpg", @"C:\photos\a.jpg", 4, Stamp),
            new JournalGroupMember(@"C:\photos\selected\a.cr2", @"C:\photos\a.cr2", 8, Stamp),
        };
        var failed = new JournalEntry("undo-conflict", FileOperationType.Move, JournalState.Failed,
            members[0].Source, members[0].Destination, 4, Stamp, Stamp, Undo: true,
            GroupId: "undo", GroupMembers: members);
        journal.Append(failed with { TimestampUtc = Stamp.AddSeconds(-1) });
        journal.Append(failed);

        var result = await new RecoveryRetryService(journal, fileSystem, clock).RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.True(fileSystem.FileExists(members[0].Source));
        Assert.True(fileSystem.FileExists(members[1].Source));
        Assert.DoesNotContain(fileSystem.Events, item => item.Kind == "move");
    }

    [Fact]
    public async Task RecoveryRetry_GroupRejectsStaleFailedSnapshotBeforeMutation()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\photos\a.jpg", "jpeg", Stamp);
        var clock = new FixedClock();
        var journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), fileSystem, clock);
        var member = new JournalGroupMember(@"C:\photos\a.jpg", @"C:\photos\selected\a.jpg", 4, Stamp);
        var failed = new JournalEntry("stale-group", FileOperationType.Move, JournalState.Failed,
            member.Source, member.Destination, member.Size, member.LastWriteUtc, Stamp,
            GroupId: "stale-group", GroupMembers: [member]);
        journal.Append(failed with { TimestampUtc = Stamp.AddSeconds(-1) });
        journal.Append(failed);
        journal.Append(failed with { State = JournalState.Dismissed, TimestampUtc = Stamp.AddSeconds(1) });

        var result = await new RecoveryRetryService(journal, fileSystem, clock).RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.True(result.Superseded);
        Assert.True(fileSystem.FileExists(member.Source));
        Assert.False(fileSystem.FileExists(member.Destination!));
    }

    private static FileActionService CreateService(
        InMemoryFileSystem fileSystem,
        OperationJournal journal,
        Func<string, string, Task>? moveOverride = null) =>
        new(journal, fileSystem, new FixedClock(), new FakeRecycleBin(), moveOverride);

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Stamp;
    }

    private sealed class FakeRecycleBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) => throw new NotSupportedException();
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    private sealed class RestoringRecycleBin(InMemoryFileSystem fileSystem) : IRecycleBin
    {
        public int RestoreCalls { get; private set; }
        public int RecycleCalls { get; private set; }
        public void SendToRecycleBin(string path) => RecycleCalls++;
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
        {
            RestoreCalls++;
            fileSystem.AddFile(originalPath, new string('x', checked((int)expectedSize)), expectedLastWriteUtc);
            return true;
        }
    }
}
