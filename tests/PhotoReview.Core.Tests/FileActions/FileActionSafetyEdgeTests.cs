using System.IO;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>Adversarial edge cases found by review: undo history duplicates, duplicate finder inputs, gate leaks, retry idempotency.</summary>
public sealed class FileActionSafetyEdgeTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Clock : IClock
    {
        public DateTime UtcNow { get; } = new(2026, 9, 26, 1, 0, 0, DateTimeKind.Utc);
        public long Timestamp => 0;
    }

    private sealed class Bin : IRecycleBin
    {
        public void SendToRecycleBin(string path) { }
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => true;
    }

    private static JournalEntry Move(string id, string from, string to, JournalState state = JournalState.Committed, bool? undo = null) =>
        new(id, FileOperationType.Move, state, from, to, 5, Stamp, Stamp, Undo: undo);

    [Fact(DisplayName = "Startup undo history has one entry per destination after move, undo, move again")]
    public void ReadStartupHistory_MoveUndoMoveAgain_ListsTheFileOnce()
    {
        var disk = new InMemoryFileSystem();
        disk.AddFile(@"C:\photos\sel\a.jpg", "12345", Stamp);
        var journal = new OperationJournal(Paths, disk, new Clock());
        journal.Append(Move("m1", @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg"));
        journal.Append(Move("u1", @"C:\photos\sel\a.jpg", @"C:\photos\a.jpg", undo: true));
        journal.Append(Move("m2", @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg"));
        var undo = new UndoService(journal, disk, new Bin());

        var history = undo.ReadStartupHistory();

        // Two identical stack entries would make the second Ctrl+Z fail on a stale entry and block every older undo.
        Assert.Equal("m2", Assert.Single(history).Id);
    }

    [Fact(DisplayName = "Startup undo history keeps separate files and their order")]
    public void ReadStartupHistory_DistinctFiles_KeepOrder()
    {
        var disk = new InMemoryFileSystem();
        disk.AddFile(@"C:\photos\sel\a.jpg", "12345", Stamp);
        disk.AddFile(@"C:\photos\sel\b.jpg", "12345", Stamp);
        var journal = new OperationJournal(Paths, disk, new Clock());
        journal.Append(Move("m1", @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg"));
        journal.Append(Move("m2", @"C:\photos\b.jpg", @"C:\photos\sel\b.jpg"));

        Assert.Equal(["m1", "m2"], new UndoService(journal, disk, new Bin()).ReadStartupHistory().Select(e => e.Id));
    }

    [Fact(DisplayName = "Recycle undo releases the busy gate even when the filesystem probe throws")]
    public async Task UndoRecycle_FileSystemThrows_GateIsReleased()
    {
        var disk = new InMemoryFileSystem();
        var fs = new CrashPointFileSystem(disk);
        var clock = new Clock();
        var journal = new OperationJournal(Paths, disk, clock);
        var service = new FileActionService(journal, fs, clock, new Bin());
        var undo = new UndoService(journal, fs, new Bin(), service, clock: clock);
        undo.Register(new FileActionResult(true, FileOperationType.Recycle, @"C:\photos\a.jpg", null, 5, Stamp, null));

        fs.CrashBeforeMutation = 0; // every read now throws IOException
        await Assert.ThrowsAnyAsync<IOException>(() => undo.UndoLastAsync());

        Assert.False(service.IsBusy, "a throwing probe left the file-action gate locked for the rest of the session");
    }

    [Fact(DisplayName = "Duplicate finder never removes the only file when the same path is listed twice")]
    public async Task DuplicateFinder_SamePathTwice_KeepsTheFile()
    {
        var disk = new InMemoryFileSystem();
        disk.AddFile(@"C:\photos\a (1).jpg", "same");

        var doubled = await DuplicateFinder.FindAsync(
            [@"C:\photos\a (1).jpg", @"C:\photos\A (1).JPG"], removeNumbered: true, (_, _) => Task.FromResult("h"), disk);

        Assert.Empty(doubled);
    }

    [Fact(DisplayName = "Duplicate finder keeps one survivor per real content group even when the hash function collides across sizes")]
    public async Task DuplicateFinder_HashCollisionAcrossSizes_KeepsASurvivorPerSizeGroup()
    {
        var disk = new InMemoryFileSystem();
        string[] shortGroup = [@"C:\photos\x (1).jpg", @"C:\photos\y (1).jpg"];
        string[] longGroup = [@"C:\photos\p (1).jpg", @"C:\photos\q (1).jpg"];
        foreach (var path in shortGroup) disk.AddFile(path, "1234");
        foreach (var path in longGroup) disk.AddFile(path, "12345");

        var result = await DuplicateFinder.FindAsync(
            [.. shortGroup, .. longGroup], removeNumbered: true, (_, _) => Task.FromResult("collision"), disk);

        // Old behaviour merged both size groups into one hash group and recycled 3 of 4, i.e. a whole content group.
        Assert.Contains(shortGroup, path => !result.Contains(path));
        Assert.Contains(longGroup, path => !result.Contains(path));
    }

    [Fact(DisplayName = "Two concurrent retries of one failed entry: the loser cannot leave the journal saying Failed for a completed move")]
    public async Task RetryTwice_LoserDoesNotOverrideCommit()
    {
        var disk = new InMemoryFileSystem();
        disk.AddFile(@"C:\photos\a.jpg", "12345", Stamp);
        var clock = new Clock();
        var journal = new OperationJournal(Paths, disk, clock);
        var failed = Move("r1", @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg", JournalState.Failed);
        journal.Append(failed);
        var retry = new RecoveryRetryService(journal, disk, clock);

        var first = await retry.RetryMoveOrCopyAsync(failed);
        var second = await retry.RetryMoveOrCopyAsync(failed); // stale window: still holds the Failed entry

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Empty(journal.ReadFailedOperations());
        Assert.Empty(journal.ReadPendingOperations());
    }

    [Fact(DisplayName = "When the Prepared append fails nothing is moved and no orphan Failed record is written")]
    public async Task PreparedAppendFails_NoMove_NoOrphanFailedRecord()
    {
        var disk = new InMemoryFileSystem();
        disk.AddFile(@"C:\photos\a.jpg", "12345", Stamp);
        var clock = new Clock();
        var journal = new OperationJournal(Paths, disk, clock);
        var service = new FileActionService(journal, disk, clock, new Bin());
        var failures = 1;
        disk.OpenAppendHook = _ => failures-- > 0 ? new IOException("disk full") : null; // only the first append fails

        var result = await service.ExecuteAsync(new FileActionRequest(@"C:\photos\a.jpg", FileOperationType.Move, "sel"));

        Assert.False(result.Succeeded);
        Assert.True(disk.FileExists(@"C:\photos\a.jpg"));
        Assert.False(disk.FileExists(@"C:\photos\sel\a.jpg"));
        Assert.Empty(journal.ReadFailedOperations()); // a Failed record exists only for an operation whose Prepared is on disk
        Assert.Empty(journal.ReadPendingOperations());
    }

    [Theory(DisplayName = "A reconcile Failed that lands after the owner's Committed (cross-process race) does not resurrect the operation as failed")]
    [InlineData(JournalErrors.PendingUnconfirmed)]
    [InlineData(JournalErrors.SourceStillExistsAfterRecovery)]
    public void StaleReconcileFailedAfterCommitted_IsIgnored(string code)
    {
        var disk = new InMemoryFileSystem();
        var journal = new OperationJournal(Paths, disk, new Clock());
        var prepared = Move("race", @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg", JournalState.Prepared);
        journal.Append(prepared);
        journal.Append(prepared with { State = JournalState.Committed });
        journal.Append(prepared with { State = JournalState.Failed, ErrorCode = code, Error = JournalErrors.EnglishText(code) });

        Assert.Empty(journal.ReadFailedOperations());
        Assert.Empty(journal.ReadPendingOperations());
    }

    [Fact(DisplayName = "A genuine Failed after Committed (not a reconcile verdict) still counts")]
    public void NonReconcileFailedAfterCommitted_StillWins()
    {
        var disk = new InMemoryFileSystem();
        var journal = new OperationJournal(Paths, disk, new Clock());
        var prepared = Move("x", @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg", JournalState.Prepared);
        journal.Append(prepared with { State = JournalState.Committed });
        journal.Append(prepared with { State = JournalState.Failed, Error = "boom" });

        Assert.Single(journal.ReadFailedOperations());
    }
}
