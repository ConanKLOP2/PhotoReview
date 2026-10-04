using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Mutation-testing gap fillers (Stryker, 2026-10-03) for <see cref="FileActionService.ExecuteGroupAsync"/>: the Permanent /
/// PermanentlyDeleted / JournalPersisted / SkippedMissing flags, what the compensation reports and how it classifies each
/// member, and the catch filters around the file-system calls. Fakes only; the real Recycle Bin is never touched.
/// </summary>
public sealed class FileActionServiceGroupMutationGapTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
    private const string Jpeg = @"C:\photos\a.jpg";
    private const string Raw = @"C:\photos\a.cr2";
    private const string Xmp = @"C:\photos\a.xmp";
    private const string MovedJpeg = @"C:\photos\selected\a.jpg";
    private const string MovedRaw = @"C:\photos\selected\a.cr2";

    public static TheoryData<string> CaughtFaults => new() { "io", "unauthorized", "argument", "notsupported" };

    private static Exception Fault(string kind) => kind switch
    {
        "io" => new IOException("fault"),
        "unauthorized" => new UnauthorizedAccessException("fault"),
        "argument" => new ArgumentException("fault"),
        "notsupported" => new NotSupportedException("fault"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Stamp;
    }

    private sealed class World
    {
        public InMemoryFileSystem Disk { get; } = new();
        public FaultableFileSystem Faulty { get; }
        public OperationJournal Journal { get; }
        public ScriptedBin Bin { get; }
        public FileActionService Service { get; }

        public World(bool faulty = false)
        {
            Disk.AddFile(Jpeg, "jpeg", Stamp);
            Disk.AddFile(Raw, "raw data", Stamp);
            Faulty = new FaultableFileSystem(Disk);
            IFileSystem fs = faulty ? Faulty : Disk;
            Journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), fs, new FixedClock());
            Bin = new ScriptedBin(Disk);
            Service = new FileActionService(Journal, fs, new FixedClock(), Bin);
        }

        /// <summary>Fails the n-th journal append (1 = Prepared, 2 = the outcome line).</summary>
        public void FailJournalAppend(int nth)
        {
            var appends = 0;
            Disk.OpenAppendHook = _ => ++appends == nth ? new IOException("journal unavailable") : null;
        }
    }

    /// <summary>Fake bin: removes the file from the in-memory disk, can pretend a path has no bin (permanent delete) and can fail.</summary>
    private sealed class ScriptedBin(InMemoryFileSystem disk) : IRecycleBin
    {
        public HashSet<string> NoBin { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Func<string, Exception?>? SendHook { get; set; }
        public Func<string, Exception?>? PermanentHook { get; set; }
        public bool PermanentDeleteLeavesFile { get; set; }
        public List<string> Recycled { get; } = [];
        public List<string> PermanentlyDeleted { get; } = [];

        public bool CanRecycle(string path) => !NoBin.Contains(path);

        public void SendToRecycleBin(string path)
        {
            if (SendHook?.Invoke(path) is { } ex) throw ex;
            Recycled.Add(path);
            disk.Delete(path);
        }

        public void DeletePermanently(string path)
        {
            if (PermanentHook?.Invoke(path) is { } ex) throw ex;
            PermanentlyDeleted.Add(path);
            if (!PermanentDeleteLeavesFile) disk.Delete(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    private static CaptureGroupActionRequest Req(FileOperationType operation, string? destination = "selected",
        bool allowPermanent = false, CaptureGroup? group = null) =>
        new(group ?? new CaptureGroup(Jpeg, Raw), operation, destination, allowPermanent);

    // ---- Permanent / PermanentlyDeleted / JournalPersisted flags ----

    [Theory]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    public async Task ExecuteGroupAsync_MoveOrCopyOnDriveWithoutRecycleBin_SucceedsAndNothingIsPermanent(FileOperationType operation)
    {
        var world = new World();
        world.Bin.NoBin.Add(Jpeg);
        world.Bin.NoBin.Add(Raw);

        var result = await world.Service.ExecuteGroupAsync(Req(operation));

        Assert.True(result.Succeeded, result.Error);
        Assert.False(result.PermanentlyDeleted);
        Assert.All(result.Entry!.GroupMembers!, member => Assert.False(member.Permanent));
        Assert.Null(result.Entry.Permanent);
        Assert.Null(result.SkippedMissing); // nothing vanished: null, not an empty list
        Assert.True(result.JournalPersisted);
        Assert.Null(result.JournalError);
    }

    [Fact]
    public async Task ExecuteGroupAsync_RecycleEveryMemberOnDriveWithoutBin_DeletesPermanentlyAndFlagsEntryAndMembers()
    {
        var world = new World();
        world.Bin.NoBin.Add(Jpeg);
        world.Bin.NoBin.Add(Raw);

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Recycle, null, allowPermanent: true));

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.PermanentlyDeleted);
        Assert.True(result.Entry!.Permanent);
        Assert.All(result.Entry.GroupMembers!, member => Assert.True(member.Permanent));
        Assert.Equal([Jpeg, Raw], world.Bin.PermanentlyDeleted);
        Assert.Empty(world.Bin.Recycled);
        Assert.False(world.Disk.FileExists(Jpeg));
        Assert.False(world.Disk.FileExists(Raw));
    }

    [Fact]
    public async Task ExecuteGroupAsync_RecycleWithOneMemberOnDriveWithoutBin_FlagsPermanentDeletionButNotTheWholeEntry()
    {
        var world = new World();
        world.Bin.NoBin.Add(Raw);

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Recycle, null, allowPermanent: true));

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.PermanentlyDeleted); // at least one member is gone for good
        Assert.Null(result.Entry!.Permanent);   // ... but the entry as a whole is not "permanent": the JPEG is in the bin
        Assert.Equal([false, true], result.Entry.GroupMembers!.Select(member => member.Permanent));
        Assert.Equal([Jpeg], world.Bin.Recycled);
        Assert.Equal([Raw], world.Bin.PermanentlyDeleted);
    }

    [Fact]
    public async Task ExecuteGroupAsync_RecycleWithBin_IsNotFlaggedPermanent()
    {
        var world = new World();

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Recycle, null));

        Assert.True(result.Succeeded, result.Error);
        Assert.False(result.PermanentlyDeleted);
        Assert.Null(result.Entry!.Permanent);
        Assert.Equal([Jpeg, Raw], world.Bin.Recycled);
        Assert.Empty(world.Bin.PermanentlyDeleted);
    }

    [Fact]
    public async Task ExecuteGroupAsync_PermanentDeleteLeavesTheFileBehind_FailsAsSourceStillExists()
    {
        var world = new World();
        world.Bin.NoBin.Add(Jpeg);
        world.Bin.NoBin.Add(Raw);
        world.Bin.PermanentDeleteLeavesFile = true; // the delete "succeeded" but the file is still on disk

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Recycle, null, allowPermanent: true));

        Assert.False(result.Succeeded);
        var failed = Assert.Single(world.Journal.ReadFailedOperations());
        Assert.Equal(JournalErrors.SourceStillExistsAfterRecovery, failed.ErrorCode);
    }

    [Theory]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    [InlineData(FileOperationType.Recycle)]
    public async Task ExecuteGroupAsync_CommitAppendFails_ReportsSuccessWithJournalNotPersisted(FileOperationType operation)
    {
        var world = new World();
        world.FailJournalAppend(2);

        var result = await world.Service.ExecuteGroupAsync(Req(operation, operation == FileOperationType.Recycle ? null : "selected"));

        Assert.True(result.Succeeded, result.Error); // the files did move: a journal failure must not undo or hide that
        Assert.False(result.JournalPersisted);
        Assert.Equal("journal unavailable", result.JournalError);
        Assert.Null(result.Error);
        Assert.NotNull(result.Entry);
    }

    [Fact]
    public async Task ExecuteGroupAsync_RolledBackOutcomeAppendFails_ReportsJournalNotPersisted()
    {
        var world = new World();
        world.Disk.MoveHook = (source, _) => source == Raw ? new IOException("simulated") : null;
        world.FailJournalAppend(2);

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.False(result.JournalPersisted);
        Assert.Equal("journal unavailable", result.JournalError);
        Assert.Null(result.Entry); // no outcome line reached the journal
        Assert.Equal("simulated", result.Error);
        Assert.True(world.Disk.FileExists(Jpeg));
    }

    // ---- Failure flags of a Recycle group ----

    [Fact]
    public async Task ExecuteGroupAsync_RecycleSecondMemberFails_KeepsOriginalErrorAndReportsNoPermanentDeletion()
    {
        var world = new World();
        world.Bin.SendHook = path => path == Raw ? new IOException("boom") : null;

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Recycle, null));

        Assert.False(result.Succeeded);
        Assert.Equal("boom", result.Error); // no compensation for a Recycle: no rollback note
        Assert.False(result.PermanentlyDeleted);
        Assert.True(result.JournalPersisted);
        Assert.Single(world.Journal.ReadFailedOperations());
        Assert.True(Assert.Single(result.Members, member => member.Member.Source == Jpeg).Completed);
        Assert.False(Assert.Single(result.Members, member => member.Member.Source == Raw).Completed);
    }

    [Fact]
    public async Task ExecuteGroupAsync_RecycleFirstMemberFails_IsAFailedRecoveryItemNotADismissedRollback()
    {
        var world = new World();
        world.Bin.SendHook = path => path == Jpeg ? new IOException("boom") : null;

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Recycle, null));

        // Every file is untouched, but only a Move/Copy has a compensation to call "fully rolled back": a Recycle stays retryable.
        Assert.False(result.Succeeded);
        Assert.Equal("boom", result.Error);
        Assert.Equal(JournalState.Failed, result.Entry!.State);
        Assert.Single(world.Journal.ReadFailedOperations());
        Assert.True(world.Disk.FileExists(Jpeg));
        Assert.True(world.Disk.FileExists(Raw));
    }

    [Fact]
    public async Task ExecuteGroupAsync_PermanentDeleteSecondMemberFails_ReportsTheCompletedPermanentDeletion()
    {
        var world = new World();
        world.Bin.NoBin.Add(Jpeg);
        world.Bin.NoBin.Add(Raw);
        world.Bin.PermanentHook = path => path == Raw ? new IOException("boom") : null;

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Recycle, null, allowPermanent: true));

        Assert.False(result.Succeeded);
        Assert.True(result.PermanentlyDeleted); // the JPEG is gone for good
        Assert.False(world.Disk.FileExists(Jpeg));
    }

    [Fact]
    public async Task ExecuteGroupAsync_PermanentDeleteFirstMemberFails_ReportsNoPermanentDeletion()
    {
        var world = new World();
        world.Bin.NoBin.Add(Jpeg);
        world.Bin.NoBin.Add(Raw);
        world.Bin.PermanentHook = path => path == Jpeg ? new IOException("boom") : null;

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Recycle, null, allowPermanent: true));

        Assert.False(result.Succeeded);
        Assert.False(result.PermanentlyDeleted); // permanent members exist, but nothing was deleted yet
        Assert.True(world.Disk.FileExists(Jpeg));
    }

    // ---- SkippedMissing ----

    [Fact]
    public async Task ExecuteGroupAsync_MoveOfCompleteGroup_ReportsNoSkippedMissing()
    {
        var world = new World();

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Move));

        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.SkippedMissing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteGroupAsync_MoveFailsAndIsRolledBack_ReportsSkippedMissingOnlyWhenAPartnerVanished(bool sidecarMissing)
    {
        var world = new World();
        if (!sidecarMissing) world.Disk.AddFile(Xmp, "xmp", Stamp);
        world.Disk.MoveHook = (source, _) => source == Raw ? new IOException("simulated") : null;

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Move, group: new CaptureGroup(Jpeg, Raw, Xmp)));

        Assert.False(result.Succeeded);
        if (sidecarMissing) Assert.Equal([Xmp], result.SkippedMissing);
        else Assert.Null(result.SkippedMissing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteGroupAsync_CompensationThrowsUnexpectedType_ReportsSkippedMissingOnlyWhenAPartnerVanished(bool sidecarMissing)
    {
        var world = new World();
        if (!sidecarMissing) world.Disk.AddFile(Xmp, "xmp", Stamp);
        world.Disk.MoveHook = (source, _) => source switch
        {
            Raw => new IOException("simulated"),
            MovedJpeg => new InvalidOperationException("simulated unexpected fault"), // the rollback of the first member blows up
            _ => null,
        };

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Move, group: new CaptureGroup(Jpeg, Raw, Xmp)));

        Assert.False(result.Succeeded);
        Assert.All(result.Members, member => Assert.False(member.StateKnown));
        if (sidecarMissing) Assert.Equal([Xmp], result.SkippedMissing);
        else Assert.Null(result.SkippedMissing);
    }

    // ---- Copy compensation ----

    [Fact]
    public async Task ExecuteGroupAsync_CopyRollbackFindsTheCopyChanged_LeavesItAndCountsItAsStuck()
    {
        var world = new World();
        world.Disk.CopyHook = (source, _) =>
        {
            if (source != Raw) return null;
            world.Disk.AddFile(MovedJpeg, "changed meanwhile", Stamp); // the first copy is no longer the 4 bytes we copied
            return new IOException("simulated");
        };

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Copy));

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreGroupActionRollbackFailed("simulated", 1), result.Error);
        Assert.Equal(17, world.Disk.GetFileStat(MovedJpeg)!.Length); // never deleted: it is not (any longer) our copy
        Assert.Single(world.Journal.ReadFailedOperations());
    }

    [Theory]
    [MemberData(nameof(CaughtFaults))]
    public async Task ExecuteGroupAsync_CopyRollbackCannotDeleteACompletedCopy_CountsItAsStuckWithoutEscaping(string kind)
    {
        var world = new World();
        world.Disk.CopyHook = (source, _) => source == Raw ? new IOException("simulated") : null;
        world.Disk.DeleteHook = path => path == MovedJpeg ? Fault(kind) : null;

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Copy));

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreGroupActionRollbackFailed("simulated", 1), result.Error);
        Assert.True(world.Disk.FileExists(MovedJpeg));
        Assert.Single(world.Journal.ReadFailedOperations());
    }

    [Fact]
    public async Task ExecuteGroupAsync_CopyLeavesPartialFile_RollbackNeverTriesToDeleteIt()
    {
        var world = new World();
        // D-01 (decision c): a half-written copy of the second member is not provably ours, so the rollback never touches it.
        world.Disk.CopyPartialHook = (source, _) => source == Raw ? (3, new IOException("simulated")) : null;
        var partialDeleteAttempted = false;
        world.Disk.DeleteHook = path =>
        {
            if (path == MovedRaw) partialDeleteAttempted = true;
            return null;
        };

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Copy));

        Assert.False(partialDeleteAttempted);
        Assert.False(result.Succeeded);
        Assert.Equal("simulated", result.Error); // no rollback note: nothing the rollback owned is stuck (the JPEG copy was removed)
        Assert.True(world.Disk.FileExists(MovedRaw));
        Assert.False(world.Disk.FileExists(MovedJpeg));
        Assert.Single(world.Journal.ReadFailedOperations());
    }

    [Fact]
    public async Task ExecuteGroupAsync_CopyRollbackDeletedTheCopyButCannotConfirmIt_IsNotDismissedAsRolledBack()
    {
        var world = new World(faulty: true);
        var deleted = false;
        world.Disk.CopyHook = (source, _) => source == Raw ? new IOException("simulated") : null;
        // The rollback deletes the first copy, then the existence check that confirms it fails: stuck, although the disk is clean.
        world.Faulty.FileExistsHook = path =>
        {
            if (path == MovedJpeg && deleted) throw new IOException("cannot confirm");
        };
        world.Disk.DeleteHook = path =>
        {
            if (path == MovedJpeg) deleted = true;
            return null;
        };

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Copy));

        Assert.False(result.Succeeded);
        Assert.False(world.Disk.FileExists(MovedJpeg));
        Assert.Equal(Tr.CoreGroupActionRollbackFailed("simulated", 1), result.Error);
        Assert.Equal(JournalState.Failed, result.Entry!.State); // an unconfirmed rollback stays a Recovery item
        Assert.Single(world.Journal.ReadFailedOperations());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteGroupAsync_CopyDestinationMissingOrWrongSizeAfterCopy_FailsVerificationWithTheCodedError(bool wrongLength)
    {
        var world = new World(faulty: true);
        world.Faulty.StatFilter = (path, stat) => path == MovedJpeg && stat is not null
            ? (wrongLength ? stat with { Length = stat.Length + 10 } : null)
            : stat;

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Copy));

        Assert.False(result.Succeeded);
        Assert.Equal(new JournalCodedException(JournalErrors.VerifySizeChanged).Message, result.Error);
        Assert.False(world.Disk.FileExists(MovedJpeg)); // the unverifiable copy was removed again
    }

    // ---- Move compensation ----

    [Fact]
    public async Task ExecuteGroupAsync_MoveFailsAndTheMovedMemberIsAlreadyBackAtItsSource_IsFullyRolledBackWithoutRollbackNote()
    {
        var world = new World();
        world.Disk.MoveHook = (source, _) =>
        {
            if (source != Raw) return null;
            world.Disk.Move(MovedJpeg, Jpeg); // the user put the JPEG back by hand while the RAW was moving
            return new IOException("simulated");
        };

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.Equal("simulated", result.Error); // an already-back member is not "stuck"
        Assert.Equal(JournalState.Dismissed, result.Entry!.State);
        Assert.Empty(world.Journal.ReadFailedOperations());
        Assert.True(world.Disk.FileExists(Jpeg));
        Assert.True(world.Disk.FileExists(Raw));
    }

    [Fact]
    public async Task ExecuteGroupAsync_MoveReportsDestinationExistsForForeignFile_LeavesItAndAddsNoRollbackNote()
    {
        var world = new World();
        var exists = InMemoryFileSystem.DestinationExistsException(MovedRaw);
        world.Disk.MoveHook = (source, destination) =>
        {
            if (source != Raw) return null;
            world.Disk.AddFile(destination, "foreign file, longer than the raw source", Stamp);
            return exists;
        };

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.Equal(exists.Message, result.Error); // the foreign file is not a member that "could not be put back"
        var raw = Assert.Single(result.Members, member => member.Member.Source == Raw);
        Assert.False(raw.Completed);
        Assert.True(raw.Conflict);
        Assert.True(raw.SourceExists);
        Assert.True(raw.DestinationExists);
        Assert.Equal(40, world.Disk.GetFileStat(MovedRaw)!.Length);
        Assert.False(world.Disk.FileExists(MovedJpeg)); // the JPEG was still rolled back
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteGroupAsync_MoveRollbackCannotConfirmTheRestoredFile_CountsItAsStuck(bool hidden)
    {
        var world = new World(faulty: true);
        var movedBack = false;
        world.Faulty.AfterMove = (source, _) =>
        {
            if (source == MovedJpeg) movedBack = true; // the rollback move of the first member just ran
        };
        // After the rollback move the file system reports the restored file missing / with a different length.
        world.Faulty.StatFilter = (path, stat) => movedBack && path == Jpeg && stat is not null
            ? (hidden ? null : stat with { Length = stat.Length + 10 })
            : stat;
        world.Disk.MoveHook = (source, _) => source == Raw ? new IOException("simulated") : null;

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreGroupActionRollbackFailed("simulated", 1), result.Error);
        Assert.Single(world.Journal.ReadFailedOperations());
    }

    [Theory]
    [MemberData(nameof(CaughtFaults))]
    public async Task ExecuteGroupAsync_MoveRollbackMoveThrows_CountsTheMemberAsStuckWithoutEscaping(string kind)
    {
        var world = new World();
        world.Disk.MoveHook = (source, _) => source switch
        {
            Raw => new IOException("simulated"),
            MovedJpeg => Fault(kind), // the rollback move of the first member fails
            _ => null,
        };

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreGroupActionRollbackFailed("simulated", 1), result.Error);
        Assert.True(world.Disk.FileExists(MovedJpeg));
        Assert.False(world.Disk.FileExists(Jpeg));
        Assert.Single(world.Journal.ReadFailedOperations());
        Assert.All(result.Members, member => Assert.True(member.StateKnown)); // the inspection itself still worked
    }

    [Fact]
    public async Task ExecuteGroupAsync_MoveSourceKeptByTheMove_FailsAsMoveSourceNotRemoved()
    {
        var world = new World();
        world.Disk.MoveLeavesSource = true; // a cross-volume move that copied but could not delete the source

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Move));

        Assert.False(result.Succeeded);
        var failed = Assert.Single(world.Journal.ReadFailedOperations());
        Assert.Equal(JournalErrors.MoveSourceNotRemoved, failed.ErrorCode);
    }

    // ---- Member classification after a failure ----

    [Fact]
    public async Task ExecuteGroupAsync_MoveRollbackBlockedByTouchedDestination_ReportsTheMovedMemberAsCompletedNotConflict()
    {
        var world = new World();
        world.Disk.MoveHook = (source, _) =>
        {
            if (source != Raw) return null;
            world.Disk.AddFile(MovedJpeg, "jpeg", Stamp.AddMinutes(5)); // same length, but touched far beyond FAT rounding
            return new IOException("simulated");
        };

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreGroupActionRollbackFailed("simulated", 1), result.Error);
        var jpeg = Assert.Single(result.Members, member => member.Member.Source == Jpeg);
        Assert.True(jpeg.Completed);
        Assert.False(jpeg.Conflict);
        Assert.False(jpeg.SourceExists);
        Assert.True(jpeg.DestinationExists);
        var raw = Assert.Single(result.Members, member => member.Member.Source == Raw);
        Assert.False(raw.Completed);
        Assert.False(raw.Conflict);
        Assert.True(raw.SourceExists);
        Assert.False(raw.DestinationExists);
    }

    [Fact]
    public async Task ExecuteGroupAsync_MoveLeavesBothFilesOfAMember_ReportsItAsNotCompletedConflict()
    {
        var world = new World();
        world.Disk.MoveHook = (source, destination) =>
        {
            if (source != Jpeg) return null;
            world.Disk.AddFile(destination, "jpeg", Stamp); // copied, the source could not be removed
            return new JournalCodedException(JournalErrors.MoveSourceNotRemoved);
        };

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Move));

        Assert.False(result.Succeeded);
        var jpeg = Assert.Single(result.Members, member => member.Member.Source == Jpeg);
        Assert.False(jpeg.Completed); // the source is still there: a Move that left it is not a completed Move
        Assert.True(jpeg.Conflict);
        Assert.True(jpeg.SourceExists);
        Assert.True(jpeg.DestinationExists);
        var raw = Assert.Single(result.Members, member => member.Member.Source == Raw);
        Assert.False(raw.Completed);
        Assert.False(raw.Conflict);
    }

    [Fact]
    public async Task ExecuteGroupAsync_MoveFailsWithSourceEditedMeanwhile_ReportsTheMemberAsConflict()
    {
        var world = new World();
        world.Disk.MoveHook = (source, _) =>
        {
            if (source != Raw) return null;
            world.Disk.AddFile(Raw, "raw data edited meanwhile", Stamp); // no longer the manifest file
            return new IOException("simulated");
        };

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Move));

        Assert.False(result.Succeeded);
        var raw = Assert.Single(result.Members, member => member.Member.Source == Raw);
        Assert.False(raw.Completed);
        Assert.True(raw.Conflict);
        Assert.True(raw.SourceExists);
        Assert.Equal(JournalState.Failed, result.Entry!.State);
    }

    [Theory]
    [InlineData("raw data edited meanwhile", 0, true)] // another length, same stamp
    [InlineData("raw data", 60, true)]                 // same length, another stamp
    public async Task ExecuteGroupAsync_CopyFailsWithSourceChangedMeanwhile_ReportsTheMemberAsConflict(string content, int stampOffsetSeconds, bool conflict)
    {
        var world = new World();
        world.Disk.CopyHook = (source, _) =>
        {
            if (source != Raw) return null;
            world.Disk.AddFile(Raw, content, Stamp.AddSeconds(stampOffsetSeconds));
            return new IOException("simulated");
        };

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Copy));

        Assert.False(result.Succeeded);
        var raw = Assert.Single(result.Members, member => member.Member.Source == Raw);
        Assert.False(raw.Completed);
        Assert.Equal(conflict, raw.Conflict);
        Assert.Equal(JournalState.Failed, result.Entry!.State); // not dismissed: a changed source is not a clean rollback
    }

    [Theory]
    [InlineData("raw data", 0, false)]
    [InlineData("raw data edited meanwhile", 0, true)]
    [InlineData("raw data", 60, true)]
    public async Task ExecuteGroupAsync_RecycleSecondMemberFails_ClassifiesTheFailedMemberBySourceIdentity(string content, int stampOffsetSeconds, bool conflict)
    {
        var world = new World();
        world.Bin.SendHook = path =>
        {
            if (path != Raw) return null;
            world.Disk.AddFile(Raw, content, Stamp.AddSeconds(stampOffsetSeconds));
            return new IOException("boom");
        };

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Recycle, null));

        Assert.False(result.Succeeded);
        var jpeg = Assert.Single(result.Members, member => member.Member.Source == Jpeg);
        Assert.True(jpeg.Completed);
        Assert.False(jpeg.Conflict);
        Assert.Null(jpeg.Error);
        Assert.False(jpeg.SourceExists);
        var raw = Assert.Single(result.Members, member => member.Member.Source == Raw);
        Assert.False(raw.Completed);
        Assert.Equal(conflict, raw.Conflict);
        Assert.Equal("boom", raw.Error);
        Assert.True(raw.SourceExists);
    }

    [Theory]
    [MemberData(nameof(CaughtFaults))]
    public async Task ExecuteGroupAsync_InspectingAMemberFails_MarksOnlyThatMemberUnknownAndStillReturns(string kind)
    {
        var world = new World(faulty: true);
        var failed = false;
        world.Bin.SendHook = path =>
        {
            if (path != Raw) return null;
            failed = true;
            return new IOException("boom");
        };
        world.Faulty.StatThrowHook = path =>
        {
            if (failed && path == Raw) throw Fault(kind); // the inspection after the failure cannot stat the RAW
        };

        var result = await world.Service.ExecuteGroupAsync(Req(FileOperationType.Recycle, null));

        Assert.False(result.Succeeded);
        Assert.Equal("boom", result.Error);
        var jpeg = Assert.Single(result.Members, member => member.Member.Source == Jpeg);
        Assert.True(jpeg.StateKnown);
        Assert.True(jpeg.Completed);
        var raw = Assert.Single(result.Members, member => member.Member.Source == Raw);
        Assert.False(raw.StateKnown);
        Assert.True(raw.Conflict);
        Assert.False(raw.Completed);
        Assert.Equal("fault", raw.Error);
    }
}