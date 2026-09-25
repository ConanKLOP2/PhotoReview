using System.IO;
using System.Text;
using System.Text.Json;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Crash-at-every-step matrix: the process dies right before each mutating I/O call of an action (and, separately,
/// a journal append is torn half-way). After a "restart" (fresh journal instance on the surviving files, startup
/// reconcile) the invariants below must hold for every crash point: no photo is lost, no operation stays Prepared, and
/// the recorded verdict agrees with what is really on disk.
/// </summary>
public sealed class CrashMatrixTests
{
    private const string Source = @"C:\photos\a.jpg";
    private const string Destination = @"C:\photos\sel\a.jpg";
    private const string Content = "0123456789";
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");

    private sealed class Clock : IClock
    {
        public DateTime UtcNow { get; } = new(2026, 9, 26, 1, 0, 0, DateTimeKind.Utc);
        public long Timestamp => 0;
    }

    /// <summary>Recycle Bin fake whose "send" is a real (crash-numbered) delete on the fake file system.</summary>
    private sealed class DeletingBin(IFileSystem fs) : IRecycleBin
    {
        public void SendToRecycleBin(string path) => fs.Delete(path);
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    private sealed record Rig(InMemoryFileSystem Disk, CrashPointFileSystem Fs, OperationJournal Journal, FileActionService Service);

    private static Rig NewRig(bool sourceExists = true)
    {
        var disk = new InMemoryFileSystem();
        if (sourceExists) disk.AddFile(Source, Content, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var fs = new CrashPointFileSystem(disk);
        var clock = new Clock();
        var journal = new OperationJournal(Paths, fs, clock);
        return new Rig(disk, fs, journal, new FileActionService(journal, fs, clock, new DeletingBin(fs)));
    }

    /// <summary>Independent reference: latest journal record per Id, parsed straight from the file (torn lines skipped).</summary>
    private static Dictionary<string, JournalEntry> LatestById(InMemoryFileSystem disk)
    {
        var latest = new Dictionary<string, JournalEntry>();
        if (!disk.FileExists(Paths.JournalFile)) return latest;
        foreach (var line in disk.ReadAllText(Paths.JournalFile).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (JsonSerializer.Deserialize<JournalEntry>(line.Trim()) is { } entry) latest[entry.Id] = entry;
            }
            catch (JsonException) { }
        }
        return latest;
    }

    private static bool Has(InMemoryFileSystem disk, string path) => disk.FileExists(path);

    private static void AssertRestartInvariants(InMemoryFileSystem disk, FileOperationType type, string context)
    {
        // Restart: a healthy process on the surviving files.
        var journal = new OperationJournal(Paths, disk, new Clock());
        journal.ReconcilePendingOperations();

        Assert.True(journal.ReadPendingOperations().Count == 0, $"{context}: an operation stayed Prepared after reconcile");

        var sourcePresent = Has(disk, Source);
        var destinationPresent = Has(disk, Destination);
        if (type == FileOperationType.Recycle)
        {
            // Recycled == removed from the folder; nothing else is at stake.
            foreach (var entry in LatestById(disk).Values.Where(e => e.Type == FileOperationType.Recycle))
            {
                Assert.True(entry.State == JournalState.Committed == !sourcePresent, $"{context}: recycle state {entry.State} vs source present={sourcePresent}");
            }
            // A photo that is gone from the folder must have been journaled (Prepared before the delete), never silently.
            Assert.True(sourcePresent || LatestById(disk).Values.Any(e => e.Type == FileOperationType.Recycle && e.State == JournalState.Committed), $"{context}: recycled without any journal record");
            return;
        }

        Assert.True(sourcePresent || destinationPresent, $"{context}: the photo was lost (neither source nor destination exists)");
        if (destinationPresent) Assert.Equal(Content, disk.ReadAllText(Destination));
        if (sourcePresent) Assert.Equal(Content, disk.ReadAllText(Source));
        if (type == FileOperationType.Copy) Assert.True(sourcePresent, $"{context}: a Copy removed its source");

        var isDone = destinationPresent && (type == FileOperationType.Copy || !sourcePresent);
        Assert.True(!isDone || LatestById(disk).Values.Any(e => e.Type == type && e.State == JournalState.Committed), $"{context}: the file operation happened without a journal record");
        foreach (var entry in LatestById(disk).Values.Where(e => e.Type == type && e.Id != "later" && !string.Equals(e.Source, Destination, StringComparison.OrdinalIgnoreCase)))
        {
            var done = destinationPresent && (type == FileOperationType.Copy || !sourcePresent);
            Assert.True(entry.State == JournalState.Committed == done, $"{context}: journal says {entry.State} but done={done}");
        }
    }

    [Theory(DisplayName = "Crash before every I/O call of a Move/Copy/Recycle: restart loses nothing and the journal matches the disk")]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    [InlineData(FileOperationType.Recycle)]
    public async Task ActionCrashedBeforeEachCall_RestartIsConsistent(FileOperationType type)
    {
        var request = new FileActionRequest(Source, type, type == FileOperationType.Recycle ? null : "sel");
        var probe = NewRig();
        var ok = await probe.Service.ExecuteAsync(request);
        Assert.True(ok.Succeeded, ok.Error);
        var total = probe.Fs.Mutations;
        Assert.True(total >= 3, "the matrix must cover mkdir, Prepared, the file op and Committed");

        for (var crash = 1; crash <= total; crash++)
        {
            var rig = NewRig();
            rig.Fs.CrashBeforeMutation = crash;
            await rig.Service.ExecuteAsync(request); // the outcome is irrelevant: the process is dead
            AssertRestartInvariants(rig.Disk, type, $"{type} crash before call #{crash}/{total}");
        }
    }

    [Theory(DisplayName = "A torn journal append at every append position never loses the photo or glues records")]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    [InlineData(FileOperationType.Recycle)]
    public async Task TornJournalAppend_RestartIsConsistent(FileOperationType type)
    {
        var request = new FileActionRequest(Source, type, type == FileOperationType.Recycle ? null : "sel");
        var probe = NewRig();
        await probe.Service.ExecuteAsync(request);
        var appendPositions = probe.Fs.Mutations;

        for (var torn = 1; torn <= appendPositions; torn++)
        {
            var rig = NewRig();
            rig.Fs.TornAppendAtMutation = torn;
            await rig.Service.ExecuteAsync(request);
            // The same process keeps running after a failed append (disk full, then space freed): its next append must
            // start on a fresh line, otherwise two records are lost.
            rig.Journal.Append(new JournalEntry("later", FileOperationType.Copy, JournalState.Prepared, @"C:\x.jpg", @"C:\y\x.jpg", 1, DateTime.UnixEpoch, DateTime.UnixEpoch));
            Assert.True(LatestById(rig.Disk).ContainsKey("later"), $"{type} torn append #{torn}: the next append was glued to the torn line");
            AssertRestartInvariants(rig.Disk, type, $"{type} torn append #{torn}");
        }
    }

    [Fact(DisplayName = "A failed (torn) append later in the process life still repairs the tail for the next append")]
    public void TornAppendAfterSuccessfulAppends_NextAppendStartsOnNewLine()
    {
        var disk = new InMemoryFileSystem();
        var fs = new CrashPointFileSystem(disk);
        var journal = new OperationJournal(Paths, fs, new Clock());
        JournalEntry Entry(string id) => new(id, FileOperationType.Copy, JournalState.Prepared, @"C:\a.jpg", @"C:\b\a.jpg", 1, DateTime.UnixEpoch, DateTime.UnixEpoch);

        journal.Append(Entry("first"));
        fs.TornAppendAtMutation = fs.Mutations + 1; // first append from now on (CreateDirectory counts too)
        Assert.Throws<IOException>(() => journal.Append(Entry("torn")));
        journal.Append(Entry("third"));

        var ids = journal.ReadPendingOperations().Select(e => e.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(["first", "third"], ids);
    }

    [Fact(DisplayName = "Undo crashed before each I/O call: restart keeps the photo and never replays or re-offers the undo")]
    public async Task UndoCrashedBeforeEachCall_RestartIsConsistent()
    {
        var undoMutations = -1;
        for (var crash = 1; undoMutations < 0 || crash <= undoMutations; crash++)
        {
            var rig = NewRig();
            var moved = await rig.Service.ExecuteAsync(new FileActionRequest(Source, FileOperationType.Move, "sel"));
            Assert.True(moved.Succeeded, moved.Error);
            var undo = new UndoService(rig.Journal, rig.Fs, new DeletingBin(rig.Fs), rig.Service, clock: new Clock());
            undo.Register(moved);

            var before = rig.Fs.Mutations;
            if (undoMutations < 0)
            {
                var probe = await undo.UndoMoveAsync();
                Assert.True(probe.Succeeded, probe.ErrorMessage);
                undoMutations = rig.Fs.Mutations - before;
                Assert.True(undoMutations >= 3, "undo must journal Prepared, move and Committed");
                crash = 0;
                continue;
            }

            rig.Fs.CrashBeforeMutation = before + crash;
            await undo.UndoMoveAsync();

            var journal = new OperationJournal(Paths, rig.Disk, new Clock());
            journal.ReconcilePendingOperations();
            Assert.Empty(journal.ReadPendingOperations());
            Assert.True(Has(rig.Disk, Source) ^ Has(rig.Disk, Destination), $"undo crash #{crash}: photo must be in exactly one place");
            var history = new UndoService(journal, rig.Disk, new DeletingBin(rig.Disk)).ReadStartupHistory();
            Assert.Equal(Has(rig.Disk, Destination) ? 1 : 0, history.Count);
            Assert.DoesNotContain(history, h => h.Undo == true);
        }
    }

    [Fact(DisplayName = "Retry of a failed Move crashed before each I/O call: restart loses nothing and matches the disk")]
    public async Task RetryCrashedBeforeEachCall_RestartIsConsistent()
    {
        var totalMutations = -1;
        for (var crash = 1; totalMutations < 0 || crash <= totalMutations; crash++)
        {
            var disk = new InMemoryFileSystem();
            var stamp = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            disk.AddFile(Source, Content, stamp);
            var fs = new CrashPointFileSystem(disk);
            var clock = new Clock();
            var journal = new OperationJournal(Paths, fs, clock);
            var failed = new JournalEntry("retry1", FileOperationType.Move, JournalState.Failed, Source, Destination, Content.Length, stamp, clock.UtcNow, "boom");
            journal.Append(failed);
            var retry = new RecoveryRetryService(journal, fs, clock);

            var before = fs.Mutations;
            if (totalMutations < 0)
            {
                Assert.True((await retry.RetryMoveOrCopyAsync(failed)).Succeeded);
                totalMutations = fs.Mutations - before;
                crash = 0;
                continue;
            }

            fs.CrashBeforeMutation = before + crash;
            await retry.RetryMoveOrCopyAsync(failed);
            AssertRestartInvariants(disk, FileOperationType.Move, $"retry crash #{crash}/{totalMutations}");
        }
    }
}
