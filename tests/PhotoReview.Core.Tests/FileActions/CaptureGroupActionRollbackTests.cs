using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>Compensation, cumulative Recycle Bin capacity and stale-group behavior of <see cref="FileActionService.ExecuteGroupAsync"/>.</summary>
public sealed class CaptureGroupActionRollbackTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
    private const string Jpeg = @"C:\photos\a.jpg";
    private const string Raw = @"C:\photos\a.cr2";
    private const string MovedJpeg = @"C:\photos\selected\a.jpg";
    private const string MovedRaw = @"C:\photos\selected\a.cr2";

    private static (InMemoryFileSystem Fs, OperationJournal Journal) CreateWorld()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(Jpeg, "jpeg", Stamp);
        fs.AddFile(Raw, "raw data", Stamp);
        var journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), fs, new FixedClock());
        return (fs, journal);
    }

    private static FileActionService CreateService(InMemoryFileSystem fs, OperationJournal journal, IRecycleBin? bin = null) =>
        new(journal, fs, new FixedClock(), bin ?? new CapacityBin(fs, long.MaxValue));

    private static CaptureGroupActionRequest Request(FileOperationType operation, string? destination = "selected") =>
        new(new CaptureGroup(Jpeg, Raw), operation, destination);

    [Fact]
    public async Task ExecuteGroupAsync_MoveSecondMemberFails_RollsFirstMemberBackAndJournalsFailure()
    {
        var (fs, journal) = CreateWorld();
        fs.MoveHook = (source, _) => source == Raw ? new IOException("simulated second-member failure") : null;

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.True(fs.FileExists(Jpeg));
        Assert.True(fs.FileExists(Raw));
        Assert.False(fs.FileExists(MovedJpeg));
        Assert.False(fs.FileExists(MovedRaw));
        Assert.All(result.Members, member =>
        {
            Assert.False(member.Completed);
            Assert.True(member.SourceExists);
            Assert.False(member.DestinationExists);
        });
        Assert.Equal("simulated second-member failure", result.Error);
        AssertFullyRolledBackAndNotRetryable(journal, result);
    }

    [Fact]
    public async Task ExecuteGroupAsync_CrossVolumeMoveLeavesPartialDestination_PartialIsRemovedAndCaptureIsRetryable()
    {
        var (fs, journal) = CreateWorld();
        // Cross-volume Move = copy + delete: the copy of the second member is cut short and the move fails, source intact.
        fs.MoveHook = (source, destination) =>
        {
            if (source != Raw) return null;
            fs.AddFile(destination, "raw", Stamp); // 3 of 8 bytes
            return new IOException("simulated cross-volume failure");
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.False(fs.FileExists(MovedRaw));  // our partial copy is gone
        Assert.False(fs.FileExists(MovedJpeg)); // first member rolled back
        Assert.True(fs.FileExists(Raw));
        Assert.True(fs.FileExists(Jpeg));
        AssertFullyRolledBackAndNotRetryable(journal, result);

        fs.MoveHook = null; // the user retries: no "destination exists" conflict is left behind
        var retry = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Move));
        Assert.True(retry.Succeeded);
    }

    [Fact]
    public async Task ExecuteGroupAsync_MoveFailsWithForeignLongerFileAtDestination_ForeignFileIsNeverDeleted()
    {
        var (fs, journal) = CreateWorld();
        fs.MoveHook = (source, destination) =>
        {
            if (source != Raw) return null;
            fs.AddFile(destination, "foreign file, longer than the raw source", Stamp); // appears after the preflight
            return new IOException("simulated failure");
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.True(fs.FileExists(MovedRaw)); // never ours to delete
        Assert.Equal(40,fs.GetFileStat(MovedRaw)!.Length);
        Assert.True(fs.FileExists(Raw));
    }

    [Theory]
    [InlineData("raw")]                                                  // SMALLER than the 8-byte source: the old size rule would call it a partial copy
    [InlineData("foreign file, longer than the raw source")]
    public async Task ExecuteGroupAsync_ForeignFileAppearsBeforeMoveAndMoveReportsDestinationExists_ForeignFileSurvivesAndIsConflict(string foreignContent)
    {
        var (fs, journal) = CreateWorld();
        fs.MoveHook = (source, destination) =>
        {
            if (source != Raw) return null;
            fs.AddFile(destination, foreignContent, Stamp); // created by someone else between the pre-check and the Move
            return InMemoryFileSystem.DestinationExistsException(destination);
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.Equal(foreignContent.Length, fs.GetFileStat(MovedRaw)!.Length); // never deleted
        Assert.True(fs.FileExists(Raw));
        var rawMember = Assert.Single(result.Members, member => member.Member.Source == Raw);
        Assert.False(rawMember.Completed);
        Assert.True(rawMember.Conflict);
        var failed = Assert.Single(journal.ReadFailedOperations()); // Recovery handles it: not dismissed as "rolled back"
        Assert.Equal(JournalState.Failed, failed.State);
    }

    [Fact]
    public async Task ExecuteGroupAsync_MoveFailsPartialDestinationButSourceChanged_PartialIsLeftAlone()
    {
        var (fs, journal) = CreateWorld();
        fs.MoveHook = (source, destination) =>
        {
            if (source != Raw) return null;
            fs.AddFile(destination, "raw", Stamp);
            fs.AddFile(Raw, "raw data edited meanwhile", Stamp.AddMinutes(1)); // the source is no longer the manifest file
            return new IOException("simulated failure");
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.True(fs.FileExists(MovedRaw));
        Assert.True(fs.FileExists(Raw));
    }

    /// <summary>A fully restored disk leaves nothing to retry: the outcome is a terminal Dismissed record, never a Failed Recovery item.</summary>
    [Fact]
    public async Task ExecuteGroupAsync_MoveFailsMidway_FatDestination_CompensationRestoresMovedMembers()
    {
        // RV-C01: the destination volume (FAT) rounds the moved JPEG's stamp to 2 s; the compensation must still recognize it
        // as the file it moved and put it back, instead of leaving the pair split.
        var fs = new InMemoryFileSystem();
        var odd = Stamp.AddMilliseconds(1735);
        fs.AddFile(Jpeg, "jpeg", odd);
        fs.AddFile(Raw, "raw data", odd);
        var journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), fs, new FixedClock());
        fs.StampOnMove = (destination, stamp) => destination.StartsWith(@"F:\", StringComparison.OrdinalIgnoreCase)
            ? new DateTime(stamp.Ticks - stamp.Ticks % TimeSpan.FromSeconds(2).Ticks, DateTimeKind.Utc)
            : stamp;
        fs.MoveHook = (source, _) => source == Raw ? new IOException("simulated second-member failure") : null;

        var result = await CreateService(fs, journal)
            .ExecuteGroupAsync(new CaptureGroupActionRequest(new CaptureGroup(Jpeg, Raw), FileOperationType.Move, @"F:\selected"));

        Assert.False(result.Succeeded);
        Assert.Equal("simulated second-member failure", result.Error); // no "could not be rolled back" note
        Assert.True(fs.FileExists(Jpeg));
        Assert.True(fs.FileExists(Raw));
        Assert.False(fs.FileExists(@"F:\selected\a.jpg"));
        Assert.False(fs.FileExists(@"F:\selected\a.cr2"));
        AssertFullyRolledBackAndNotRetryable(journal, result);
    }

    private static void AssertFullyRolledBackAndNotRetryable(OperationJournal journal, CaptureGroupActionResult result)
    {
        Assert.Equal(JournalState.Dismissed, result.Entry!.State);
        Assert.Equal(result.GroupId, result.Entry.GroupId);
        Assert.Null(result.Entry.Error);
        Assert.Empty(journal.ReadFailedOperations());
        Assert.Empty(journal.ReadPendingOperations());
        Assert.Empty(journal.ReconcilePendingOperations()); // reconcile never resurrects it
        Assert.Empty(journal.ReadPendingAndFailedOperations());
    }

    [Fact]
    public async Task ExecuteGroupAsync_MoveFailsAfterFirstMember_RollbackDoesNotUseTheForwardMoveOverride()
    {
        var (fs, journal) = CreateWorld();
        var overrideCalls = new List<string>();
        var service = new FileActionService(journal, fs, new FixedClock(), new CapacityBin(fs, long.MaxValue), (source, destination) =>
        {
            overrideCalls.Add(source);
            if (source == Raw) throw new IOException("simulated second-member failure");
            fs.Move(source, destination);
            return Task.CompletedTask;
        });

        await service.ExecuteGroupAsync(Request(FileOperationType.Move));

        Assert.Equal([Jpeg, Raw], overrideCalls); // the rollback is a plain file-system move, not a second override call
        Assert.True(fs.FileExists(Jpeg));
    }

    [Fact]
    public async Task ExecuteGroupAsync_MoveRollbackBlockedByChangedDestination_KeepsMovedFileAndReportsIt()
    {
        var (fs, journal) = CreateWorld();
        fs.MoveHook = (source, _) =>
        {
            if (source != Raw) return null;
            fs.AddFile(MovedJpeg, "edited elsewhere, different length", Stamp); // the moved JPEG changed meanwhile
            return new IOException("simulated second-member failure");
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.False(fs.FileExists(Jpeg)); // NOT moved back over an unknown file's identity
        Assert.True(fs.FileExists(MovedJpeg));
        Assert.True(fs.FileExists(Raw));
        // The journal keeps the ORIGINAL failure (invariant text, no localized rollback note); the user-facing message
        // carries the rollback note with the exact stuck count.
        var failed = Assert.Single(journal.ReadFailedOperations());
        Assert.Equal("simulated second-member failure", failed.Error);
        Assert.Null(failed.ErrorCode);
        Assert.Equal(Tr.CoreGroupActionRollbackFailed("simulated second-member failure", 1), result.Error);
        var conflict = Assert.Single(result.Members, member => member.Conflict);
        Assert.Equal(Jpeg, conflict.Member.Source);
    }

    [Fact]
    public async Task ExecuteGroupAsync_MoveRollbackIncomplete_JournalsOriginalCodedFailureNotLocalizedRollbackText()
    {
        var (fs, journal) = CreateWorld();
        // Second member: the cross-volume "copied but source not removed" case (coded). First member's moved file was edited,
        // so its rollback is blocked (stuck > 0).
        fs.MoveHook = (source, destination) =>
        {
            if (source != Raw) return null;
            fs.AddFile(MovedJpeg, "edited elsewhere, different length", Stamp);
            fs.AddFile(destination, "raw data", Stamp);
            return new JournalCodedException(JournalErrors.MoveSourceNotRemoved);
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Move));

        Assert.False(result.Succeeded);
        var failed = Assert.Single(journal.ReadFailedOperations());
        Assert.Equal(JournalErrors.MoveSourceNotRemoved, failed.ErrorCode);
        Assert.Equal(JournalErrors.EnglishText(JournalErrors.MoveSourceNotRemoved), failed.Error);
        Assert.NotEqual(result.Error, failed.Error); // the result message has the (localized) rollback note, the journal does not
    }

    [Fact]
    public async Task ExecuteGroupAsync_MoveRollback_NeverOverwritesFileThatAppearedAtSource()
    {
        var (fs, journal) = CreateWorld();
        fs.MoveHook = (source, _) =>
        {
            if (source != Raw) return null;
            fs.AddFile(Jpeg, "someone else's new a.jpg", Stamp);
            return new IOException("simulated second-member failure");
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Move));

        Assert.False(result.Succeeded);
        using var stream = fs.OpenReadShared(Jpeg);
        using var reader = new StreamReader(stream);
        Assert.Equal("someone else's new a.jpg", await reader.ReadToEndAsync());
        Assert.True(fs.FileExists(MovedJpeg));
        Assert.Single(journal.ReadFailedOperations());
    }

    [Fact]
    public async Task ExecuteGroupAsync_MoveCancelledAfterFirstMember_RollsBack()
    {
        var (fs, journal) = CreateWorld();
        using var cts = new CancellationTokenSource();
        var service = new FileActionService(journal, fs, new FixedClock(), new CapacityBin(fs, long.MaxValue), (source, destination) =>
        {
            fs.Move(source, destination);
            cts.Cancel(); // cancel while the first member is done
            return Task.CompletedTask;
        });

        var result = await service.ExecuteGroupAsync(Request(FileOperationType.Move), cts.Token);

        Assert.False(result.Succeeded);
        Assert.True(fs.FileExists(Jpeg));
        Assert.True(fs.FileExists(Raw));
        Assert.False(fs.FileExists(MovedJpeg));
        AssertFullyRolledBackAndNotRetryable(journal, result);
    }

    [Fact]
    public async Task ExecuteGroupAsync_MoveFailsButBothCopiesRemain_StaysARetryableFailedItem()
    {
        var (fs, journal) = CreateWorld();
        // A "moved" member whose source could not be removed: destination AND source exist, so the disk is NOT unchanged.
        fs.MoveHook = (source, destination) =>
        {
            if (source != Jpeg) return null;
            fs.AddFile(destination, "jpeg", Stamp);
            return new JournalCodedException(JournalErrors.MoveSourceNotRemoved);
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Move));

        Assert.False(result.Succeeded);
        var failed = Assert.Single(journal.ReadFailedOperations());
        Assert.Equal(JournalErrors.MoveSourceNotRemoved, failed.ErrorCode);
    }

    [Fact]
    public async Task ExecuteGroupAsync_CopySecondMemberFails_DeletesOnlyTheCopiesItCreatedAndAllowsRerun()
    {
        var (fs, journal) = CreateWorld();
        fs.CopyHook = (source, _) => source == Raw ? new IOException("simulated copy failure") : null;
        var service = CreateService(fs, journal);

        var failed = await service.ExecuteGroupAsync(Request(FileOperationType.Copy));

        Assert.False(failed.Succeeded);
        Assert.False(fs.FileExists(MovedJpeg));
        Assert.False(fs.FileExists(MovedRaw));
        Assert.True(fs.FileExists(Jpeg));
        Assert.True(fs.FileExists(Raw));

        fs.CopyHook = null;
        var rerun = await service.ExecuteGroupAsync(Request(FileOperationType.Copy));
        Assert.True(rerun.Succeeded, rerun.Error);
        Assert.True(fs.FileExists(MovedJpeg));
        Assert.True(fs.FileExists(MovedRaw));
    }

    [Fact]
    public async Task ExecuteGroupAsync_CopyCancelledAndRollbackLeavesACopy_JournalsTheCancellationCode()
    {
        var (fs, journal) = CreateWorld();
        using var cts = new CancellationTokenSource();
        fs.CopyHook = (source, _) =>
        {
            if (source == Jpeg) cts.Cancel(); // the user cancels while the first member is being copied
            return null;
        };
        fs.DeleteHook = path => string.Equals(path, MovedJpeg, StringComparison.OrdinalIgnoreCase)
            ? new IOException("simulated undeletable copy") : null; // the rollback cannot remove the first copy

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Copy), cts.Token);

        Assert.False(result.Succeeded);
        Assert.True(fs.FileExists(MovedJpeg)); // left behind: it needs Recovery
        var failed = Assert.Single(journal.ReadFailedOperations());
        Assert.Equal(JournalErrors.CancelledByUser, failed.ErrorCode);
        Assert.Equal(JournalErrors.EnglishText(JournalErrors.CancelledByUser), failed.Error);
    }

    [Fact]
    public async Task ExecuteGroupAsync_CopyCancelledMidWay_RemovesOnlyTheCopiesThisOperationMade()
    {
        var (fs, journal) = CreateWorld();
        using var cts = new CancellationTokenSource();
        // A pre-existing unrelated file in the destination folder, and a copy of the first member.
        fs.AddFile(@"C:\photos\selected\other.jpg", "someone else's", Stamp);
        fs.CopyHook = (source, _) =>
        {
            if (source == Jpeg) cts.Cancel();
            return null;
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Copy), cts.Token);

        Assert.False(result.Succeeded);
        Assert.False(fs.FileExists(MovedJpeg)); // our copy is gone
        Assert.False(fs.FileExists(MovedRaw));
        Assert.True(fs.FileExists(@"C:\photos\selected\other.jpg")); // never ours
        Assert.True(fs.FileExists(Jpeg));
        Assert.True(fs.FileExists(Raw));
        AssertFullyRolledBackAndNotRetryable(journal, result);
    }

    [Fact]
    public async Task ExecuteGroupAsync_CopyLeavesPartialFile_PartialFileIsDeleted()
    {
        var (fs, journal) = CreateWorld();
        fs.CopyHook = (source, destination) =>
        {
            if (source != Raw) return null;
            fs.AddFile(destination, "par", Stamp); // a half-written copy
            return new IOException("disk full");
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Copy));

        Assert.False(result.Succeeded);
        Assert.False(fs.FileExists(MovedRaw));
        Assert.False(fs.FileExists(MovedJpeg));
        AssertFullyRolledBackAndNotRetryable(journal, result);
    }

    [Fact]
    public async Task ExecuteGroupAsync_CopyDestinationAppearsBetweenCheckAndCopy_ForeignFileIsNeverDeleted()
    {
        var (fs, journal) = CreateWorld();
        // The foreign file appears exactly in the window between the last existence check and the copy itself
        // (the hook runs inside Copy/TryCopyNew, before the create-new step).
        fs.CopyHook = (source, _) =>
        {
            if (source == Raw) fs.AddFile(MovedRaw, "foreign file", Stamp);
            return null;
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Copy));

        Assert.False(result.Succeeded);
        Assert.Equal(12, fs.GetFileStat(MovedRaw)!.Length); // "foreign file": untouched
        Assert.False(fs.FileExists(MovedJpeg));              // our earlier copy was compensated
    }

    [Fact]
    public async Task ExecuteGroupAsync_CopyDestinationAppearsAfterPreflight_ForeignFileIsNeverDeleted()
    {
        var (fs, journal) = CreateWorld();
        fs.CopyHook = (source, _) =>
        {
            if (source == Jpeg) fs.AddFile(MovedRaw, "foreign file", Stamp); // appears after preflight, before member 2
            return null;
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Copy));

        Assert.False(result.Succeeded);
        Assert.False(fs.FileExists(MovedJpeg)); // our own copy was compensated
        Assert.Equal(12, fs.GetFileStat(MovedRaw)!.Length); // "foreign file": untouched
    }

    [Fact]
    public async Task ExecuteGroupAsync_RecycleBinThatFitsEachMemberButNotTheGroup_IsRefusedBeforeAnyMutation()
    {
        var (fs, journal) = CreateWorld(); // 4 + 8 bytes
        var bin = new CapacityBin(fs, 10);

        var result = await CreateService(fs, journal, bin).ExecuteGroupAsync(Request(FileOperationType.Recycle, null));

        Assert.False(result.Succeeded);
        Assert.Empty(result.Members);
        Assert.Equal(0, bin.RecycleCalls);
        Assert.True(fs.FileExists(Jpeg));
        Assert.True(fs.FileExists(Raw));
        Assert.Empty(journal.ReadPendingAndFailedOperations());
    }

    [Fact]
    public async Task ExecuteGroupAsync_RecycleBinThatFitsTheWholeGroup_Succeeds()
    {
        var (fs, journal) = CreateWorld();
        var bin = new CapacityBin(fs, 12);

        var result = await CreateService(fs, journal, bin).ExecuteGroupAsync(Request(FileOperationType.Recycle, null));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, bin.RecycleCalls);
    }

    [Theory]
    [InlineData(FileOperationType.Recycle)]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    public async Task ExecuteGroupAsync_PartnerVanishedExternally_ActsOnRemainingMembersAndReportsTheMissingOne(FileOperationType operation)
    {
        var (fs, journal) = CreateWorld();
        fs.Delete(Raw);
        var bin = new CapacityBin(fs, long.MaxValue);
        var destination = operation == FileOperationType.Recycle ? null : "selected";

        var result = await CreateService(fs, journal, bin).ExecuteGroupAsync(Request(operation, destination));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal([Raw], result.SkippedMissing);
        var member = Assert.Single(result.Members);
        Assert.Equal(Jpeg, member.Member.Source);
        Assert.Equal(operation != FileOperationType.Copy, !fs.FileExists(Jpeg));
        Assert.Equal(operation == FileOperationType.Recycle ? 1 : 0, bin.RecycleCalls);
        Assert.Empty(journal.ReadPendingAndFailedOperations());
        if (operation == FileOperationType.Move)
            Assert.Equal([Jpeg], Assert.Single(journal.ReadCommittedMoves()).GroupMembers!.Select(m => m.Source));
    }

    [Fact]
    public async Task ExecuteGroupAsync_OnlySidecarLeft_IsRefusedAsSourceMissing()
    {
        var (fs, journal) = CreateWorld();
        fs.Delete(Jpeg);
        fs.Delete(Raw);
        fs.AddFile(@"C:\photos\a.xmp", "xmp", Stamp);
        var bin = new CapacityBin(fs, long.MaxValue);

        var result = await CreateService(fs, journal, bin).ExecuteGroupAsync(new CaptureGroupActionRequest(
            new CaptureGroup(Jpeg, Raw, @"C:\photos\a.xmp"), FileOperationType.Recycle));

        Assert.False(result.Succeeded);
        Assert.Equal(0, bin.RecycleCalls);
        Assert.True(fs.FileExists(@"C:\photos\a.xmp"));
    }

    [Fact]
    public async Task ExecuteGroupAsync_CompensationThrowsUnexpectedExceptionType_DoesNotEscapeAndLeavesPreparedForReconcile()
    {
        var (fs, journal) = CreateWorld();
        fs.MoveHook = (source, _) =>
        {
            if (source == Raw) return new IOException("simulated second-member failure");
            // The rollback of the first (already moved) member blows up with a type the compensation does not expect.
            if (source == MovedJpeg) return new InvalidOperationException("simulated unexpected fault");
            return null;
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Move)); // must not throw

        Assert.False(result.Succeeded);
        Assert.False(result.Rejected);
        Assert.Equal(Tr.CoreGroupActionRollbackFailed("simulated second-member failure", 2), result.Error);
        Assert.All(result.Members, member => Assert.False(member.Completed));
        Assert.Contains(result.Members, member => member.Conflict); // state could not be proven: never reported as cleanly rolled back
        // No outcome line was written: the Prepared line stays, so startup reconcile judges the disk later.
        Assert.Equal(JournalState.Prepared, Assert.Single(journal.ReadPendingOperations()).State);
        Assert.Empty(journal.ReadFailedOperations());
        Assert.False(fs.FileExists(Jpeg)); // the JPEG really is still in the destination folder
        Assert.True(fs.FileExists(MovedJpeg));
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Stamp;
    }

    /// <summary>Fake Recycle Bin (never the real one): accepts at most <c>capacity</c> bytes per call and removes the file from the in-memory disk.</summary>
    private sealed class CapacityBin(InMemoryFileSystem fs, long capacity) : IRecycleBin
    {
        public int RecycleCalls { get; private set; }
        public void SendToRecycleBin(string path)
        {
            RecycleCalls++;
            fs.Delete(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
        public bool FitsInRecycleBin(string path, long fileSize) => fileSize <= capacity;
    }
}
