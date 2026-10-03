using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Mutation-testing gap fillers (Stryker, 2026-10-03) for <see cref="FileActionService.ExecuteAsync"/>: the flags of a
/// Recycle whose Committed line cannot be written, the outcome of a cancelled Copy depending on whether the source is still
/// untouched, and the catches around the post-failure disk inspection. Fakes only; the real Recycle Bin is never touched.
/// </summary>
public sealed class FileActionServiceSingleMutationGapTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
    private const string Jpeg = @"C:\photos\a.jpg";

    /// <summary>Any non-fatal failure (the post-cancel inspection swallows everything but OutOfMemory).</summary>
    public static TheoryData<string> Faults => new() { "io", "unauthorized", "argument", "notsupported", "invalid" };

    /// <summary>The documented file-system failures (IsSourceGone swallows exactly these).</summary>
    public static TheoryData<string> FileSystemFaults => new() { "io", "unauthorized", "argument", "notsupported" };

    private static Exception Fault(string kind) => kind switch
    {
        "io" => new IOException("fault"),
        "unauthorized" => new UnauthorizedAccessException("fault"),
        "argument" => new ArgumentException("fault"),
        "notsupported" => new NotSupportedException("fault"),
        "invalid" => new InvalidOperationException("fault"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Stamp;
    }

    /// <summary>Throws on its n-th read (1-based), like a clock that fails exactly when the Committed line is stamped.</summary>
    private sealed class ThrowOnNthReadClock(int nth) : IClock
    {
        private int _reads;

        public DateTime UtcNow => ++_reads == nth ? throw new InvalidOperationException("clock failure") : Stamp;
    }

    private sealed class Bin(InMemoryFileSystem disk) : IRecycleBin
    {
        public bool NoBin { get; set; }
        public bool CanRecycle(string path) => !NoBin;
        public void SendToRecycleBin(string path) => disk.Delete(path);
        public void DeletePermanently(string path) => disk.Delete(path);
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    private sealed class World
    {
        public InMemoryFileSystem Disk { get; } = new();
        public FaultableFileSystem Faulty { get; }
        public OperationJournal Journal { get; }
        public Bin RecycleBin { get; }
        public FileActionService Service { get; }

        public World(bool faulty = false, IClock? serviceClock = null)
        {
            Disk.AddFile(Jpeg, "jpeg", Stamp);
            Faulty = new FaultableFileSystem(Disk);
            IFileSystem fs = faulty ? Faulty : Disk;
            Journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), fs, new FixedClock());
            RecycleBin = new Bin(Disk);
            Service = new FileActionService(Journal, fs, serviceClock ?? new FixedClock(), RecycleBin);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_RecycleCommitAppendFails_ReportsSuccessWithJournalNotPersisted(bool permanent)
    {
        var world = new World();
        world.RecycleBin.NoBin = permanent;
        var appends = 0;
        world.Disk.OpenAppendHook = _ => ++appends == 2 ? new IOException("journal unavailable") : null;

        var result = await world.Service.ExecuteAsync(new FileActionRequest(Jpeg, FileOperationType.Recycle, AllowPermanentDelete: permanent));

        Assert.True(result.Succeeded, result.Error);
        Assert.False(result.JournalPersisted);
        Assert.Equal("journal unavailable", result.JournalError);
        Assert.Null(result.Error);
        Assert.Equal(permanent, result.PermanentlyDeleted);
        Assert.False(world.Disk.FileExists(Jpeg));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownOperation_FailsAsUnsupportedAndWritesNoJournalLine()
    {
        var world = new World();
        const FileOperationType unknown = (FileOperationType)99;

        var result = await world.Service.ExecuteAsync(new FileActionRequest(Jpeg, unknown));

        Assert.False(result.Succeeded);
        Assert.False(result.Rejected);
        Assert.Equal(Tr.CoreFileActionUnsupportedOperation(unknown), result.Error);
        Assert.Empty(world.Journal.ReadPendingAndFailedOperations());
        Assert.True(world.Disk.FileExists(Jpeg));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ClockFailsAfterTheRecycleCompleted_StillReportsTheCompletedOutcome(bool permanent)
    {
        // Read 1 stamps Prepared, read 2 (the Committed line) throws after the file is already gone.
        var world = new World(serviceClock: new ThrowOnNthReadClock(2));
        world.RecycleBin.NoBin = permanent;

        var result = await world.Service.ExecuteAsync(new FileActionRequest(Jpeg, FileOperationType.Recycle, AllowPermanentDelete: permanent));

        Assert.False(world.Disk.FileExists(Jpeg));
        Assert.True(result.Succeeded); // the mutation completed: the file is gone whatever the bookkeeping did afterwards
        Assert.Null(result.Error);
        Assert.Equal(permanent, result.PermanentlyDeleted);
        Assert.False(result.SourceRemoved);
    }

    [Theory]
    [InlineData(FileOperationType.Copy, false)]
    [InlineData(FileOperationType.Move, true)]
    public async Task ExecuteAsync_FailureAfterTheSourceVanished_ReportsSourceRemovedOnlyForAMove(FileOperationType operation, bool expectedSourceRemoved)
    {
        var world = new World();
        Exception? Vanish(string source)
        {
            world.Disk.Delete(source);
            return new IOException("failed after the source vanished");
        }
        world.Disk.CopyHook = (source, _) => Vanish(source);
        world.Disk.MoveHook = (source, _) => Vanish(source);

        var result = await world.Service.ExecuteAsync(new FileActionRequest(Jpeg, operation, "selected"));

        Assert.False(result.Succeeded);
        Assert.Equal(expectedSourceRemoved, result.SourceRemoved);
    }

    [Theory]
    [InlineData("jpeg", 0, true)]               // untouched: a clean cancel, nothing to recover
    [InlineData("jpeg edited meanwhile", 0, false)] // another length, same stamp
    [InlineData("jpeg", 60, false)]             // same length, another stamp
    public async Task ExecuteAsync_CancelledCopy_IsDismissedOnlyWhileTheSourceIsUntouched(string content, int stampOffsetSeconds, bool dismissed)
    {
        var world = new World();
        world.Disk.CopyHook = (source, _) =>
        {
            world.Disk.AddFile(Jpeg, content, Stamp.AddSeconds(stampOffsetSeconds));
            return new OperationCanceledException();
        };

        var result = await world.Service.ExecuteAsync(new FileActionRequest(Jpeg, FileOperationType.Copy, "selected"));

        Assert.False(result.Succeeded);
        Assert.Empty(world.Journal.ReadPendingOperations());
        Assert.Equal(dismissed ? 0 : 1, world.Journal.ReadFailedOperations().Count);
    }

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task ExecuteAsync_CancelledCopyAndSourceCannotBeInspected_KeepsTheFailedRecoveryItemAndDoesNotThrow(string kind)
    {
        var world = new World();
        var cancelled = false;
        world.Disk.CopyHook = (_, _) =>
        {
            cancelled = true;
            return new OperationCanceledException();
        };
        world.Disk.StatHook = path => cancelled && path == Jpeg ? Fault(kind) : null; // only the post-failure inspection fails

        var result = await world.Service.ExecuteAsync(new FileActionRequest(Jpeg, FileOperationType.Copy, "selected"));

        Assert.False(result.Succeeded);
        Assert.Single(world.Journal.ReadFailedOperations()); // inspection failed: treated as "touched", never as a clean cancel
    }

    [Theory]
    [MemberData(nameof(FileSystemFaults))]
    public async Task ExecuteAsync_MoveFailsAndTheSourceCannotBeChecked_DoesNotThrowAndKeepsTheSource(string kind)
    {
        var world = new World(faulty: true);
        var failed = false;
        world.Disk.MoveHook = (_, _) =>
        {
            failed = true;
            return new IOException("simulated");
        };
        world.Faulty.FileExistsHook = path =>
        {
            if (failed && path == Jpeg) throw Fault(kind);
        };

        var result = await world.Service.ExecuteAsync(new FileActionRequest(Jpeg, FileOperationType.Move, "selected"));

        Assert.False(result.Succeeded);
        Assert.False(result.SourceRemoved); // an inspection error counts as "not gone": the catalog keeps the entry
        Assert.Equal("simulated", result.Error);
    }
}