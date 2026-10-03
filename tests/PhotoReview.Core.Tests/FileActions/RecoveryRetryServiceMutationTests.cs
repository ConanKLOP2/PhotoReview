using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Mutation-testing gaps of <see cref="RecoveryRetryService"/> (group retry): the refusal rules that run BEFORE anything is
/// journaled, the re-checks that run after the Prepared line (races between the Recovery window's check and the execution),
/// the post-move verification and the JournalPersisted/JournalError reporting. Races are made deterministic with the
/// fake file system's stat hook (the Nth stat of a path mutates the world). Fakes only: no real Recycle Bin.
/// </summary>
public sealed class RecoveryRetryServiceMutationTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private const string Jpg = @"C:\photos\a.jpg";
    private const string Raw = @"C:\photos\a.cr2";
    private const string OutJpg = @"C:\photos\out\a.jpg";
    private const string OutRaw = @"C:\photos\out\a.cr2";

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class Bin(InMemoryFileSystem fs) : IRecycleBin
    {
        public List<string> Recycled { get; } = [];
        public List<string> PermanentlyDeleted { get; } = [];
        public List<string> RestoreCalls { get; } = [];
        public HashSet<string> NoBinPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Func<string, bool> RestoreResult { get; set; } = _ => true;
        public bool RestoreRecreatesFile { get; set; } = true;

        public void SendToRecycleBin(string path)
        {
            Recycled.Add(path);
            fs.Delete(path);
        }

        public bool CanRecycle(string path) => !NoBinPaths.Contains(path);

        public void DeletePermanently(string path)
        {
            PermanentlyDeleted.Add(path);
            fs.Delete(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
        {
            RestoreCalls.Add(originalPath);
            if (RestoreRecreatesFile) fs.AddFile(originalPath, new string('x', (int)expectedSize), expectedLastWriteUtc);
            return RestoreResult(originalPath);
        }
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly Bin _bin;
    private readonly OperationJournal _journal;
    private readonly RecoveryRetryService _service;

    public RecoveryRetryServiceMutationTests()
    {
        _bin = new Bin(_fs);
        _journal = new OperationJournal(Paths, _fs, new FixedClock(Stamp.AddMinutes(1)));
        _service = new RecoveryRetryService(_journal, _fs, new FixedClock(Stamp.AddMinutes(2)), _bin);
    }

    private static JournalGroupMember MoveMember(string source, string destination, long size, bool permanent = false) =>
        new(source, destination, size, Stamp, permanent);

    private static JournalEntry FailedMoveGroup() => new(
        "grp", FileOperationType.Move, JournalState.Failed, Jpg, OutJpg, 4, Stamp, Stamp,
        Error: "earlier failure", GroupId: "capture", GroupMembers: [MoveMember(Jpg, OutJpg, 4), MoveMember(Raw, OutRaw, 8)]);

    private static JournalEntry FailedRecycleGroup(bool? undo, params JournalGroupMember[] members) => new(
        "del", FileOperationType.Recycle, JournalState.Failed, members[0].Source, null, members[0].Size, Stamp, Stamp,
        Error: "x", ErrorCode: JournalErrors.SourceStillExistsAfterRecovery, Undo: undo,
        Permanent: members.Any(m => m.Permanent) ? true : null, GroupId: "capture", GroupMembers: members);

    private void AddMoveSources()
    {
        _fs.AddFile(Jpg, "jpeg", Stamp);
        _fs.AddFile(Raw, "raw data", Stamp);
    }

    /// <summary>Runs <paramref name="action"/> inside the <paramref name="nth"/> stat of <paramref name="path"/> (1 = the Recovery check).</summary>
    private void OnNthStat(string path, int nth, Action action)
    {
        var count = 0;
        _fs.StatHook = candidate =>
        {
            if (string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase) && ++count == nth) action();
            return null;
        };
    }

    private int JournalLineCount() => _fs.ReadLines(Paths.JournalFile).Count(line => line.Length > 0);

    private void AssertRefusedBeforeJournaling(RecoveryRetryResult result, string expectedMessage)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(expectedMessage, result.Message);
        Assert.Null(result.Entry);
        Assert.Equal(1, JournalLineCount()); // only the original Failed line: no Prepared was appended
        Assert.DoesNotContain(_fs.Events, e => e.Kind == "move");
        Assert.Empty(_bin.Recycled);
        Assert.Empty(_bin.PermanentlyDeleted);
        Assert.Empty(_bin.RestoreCalls);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Routing and the permanent-delete guard.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RetryMoveOrCopyAsync_MoveEntryWithAnEmptyMemberList_RetriesAsASingleMove()
    {
        _fs.AddFile(Jpg, "jpeg", Stamp);
        var failed = new JournalEntry("single", FileOperationType.Move, JournalState.Failed, Jpg, OutJpg, 4, Stamp, Stamp,
            Error: "x", GroupMembers: []);
        _journal.Append(failed);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(_fs.FileExists(OutJpg));
        Assert.False(_fs.FileExists(Jpg));
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_MoveGroupWithAMemberFlaggedPermanentAndSettingOff_StillRetries()
    {
        // `Permanent` is only meaningful for Delete: a Move member carrying it must not trip the permanent-delete refusal.
        AddMoveSources();
        var failed = FailedMoveGroup() with
        {
            GroupMembers = [MoveMember(Jpg, OutJpg, 4), MoveMember(Raw, OutRaw, 8, permanent: true)],
        };
        _journal.Append(failed);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(_fs.FileExists(OutJpg));
        Assert.True(_fs.FileExists(OutRaw));
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_UndoDeleteGroupWithAPermanentMissingMember_IsRefusedBeforeAnythingIsRestoredOrJournaled()
    {
        // JPEG restorable, RAW was deleted permanently: an undo can never bring it back, so nothing may be touched.
        var failed = FailedRecycleGroup(true, new JournalGroupMember(Jpg, null, 4, Stamp), new JournalGroupMember(Raw, null, 8, Stamp, true));
        _journal.Append(failed);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        AssertRefusedBeforeJournaling(result, Tr.CoreRecoverySourceChanged);
        Assert.False(_fs.FileExists(Jpg));
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_DeleteGroupWithPermanentMemberAndSettingOff_RefusesWithTheUnsupportedDriveMessageAndDeletesNothing()
    {
        _fs.AddFile(Jpg, "jpeg", Stamp);
        _fs.AddFile(Raw, "raw data", Stamp);
        _bin.NoBinPaths.Add(Raw);
        var failed = FailedRecycleGroup(null, new JournalGroupMember(Jpg, null, 4, Stamp), new JournalGroupMember(Raw, null, 8, Stamp, true));
        _journal.Append(failed);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        AssertRefusedBeforeJournaling(result, Tr.CoreRecycleUnsupportedDrive("a.cr2"));
        Assert.True(_fs.FileExists(Jpg));
        Assert.True(_fs.FileExists(Raw));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Delete group: the member changed between the Recovery check and the retry's own re-check (before Prepared).
    // ---------------------------------------------------------------------------------------------------------

    private JournalEntry PresentDeleteGroup(bool rawPermanent = false)
    {
        _fs.AddFile(Jpg, "jpeg", Stamp);
        _fs.AddFile(Raw, "raw data", Stamp);
        var failed = FailedRecycleGroup(null, new JournalGroupMember(Jpg, null, 4, Stamp), new JournalGroupMember(Raw, null, 8, Stamp, rawPermanent));
        _journal.Append(failed);
        return failed;
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_DeleteGroupMemberVanishesAfterTheCheck_IsRefusedAndNothingRecycled()
    {
        var failed = PresentDeleteGroup();
        OnNthStat(Jpg, 2, () => _fs.Delete(Jpg));

        var result = await _service.RetryMoveOrCopyAsync(failed);

        AssertRefusedBeforeJournaling(result, Tr.CoreRecoverySourceChanged);
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_DeleteGroupMemberResizedAfterTheCheck_IsRefusedAndNothingRecycled()
    {
        var failed = PresentDeleteGroup();
        OnNthStat(Jpg, 2, () => _fs.AddFile(Jpg, "jpeg!", Stamp));

        var result = await _service.RetryMoveOrCopyAsync(failed);

        AssertRefusedBeforeJournaling(result, Tr.CoreRecoverySourceChanged);
        Assert.True(_fs.FileExists(Raw));
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_DeleteGroupMemberTouchedAfterTheCheckWithSameSize_IsRefusedAndNothingRecycled()
    {
        var failed = PresentDeleteGroup();
        OnNthStat(Jpg, 2, () => _fs.AddFile(Jpg, "jpeg", Stamp.AddSeconds(30)));

        var result = await _service.RetryMoveOrCopyAsync(failed);

        AssertRefusedBeforeJournaling(result, Tr.CoreRecoverySourceChanged);
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_DeleteGroupMemberJournaledPermanentButDriveNowHasABin_IsRefusedAndNeverDeletedPermanently()
    {
        var failed = PresentDeleteGroup(rawPermanent: true);
        var service = new RecoveryRetryService(_journal, _fs, new FixedClock(Stamp.AddMinutes(2)), _bin, () => true);

        var result = await service.RetryMoveOrCopyAsync(failed);

        AssertRefusedBeforeJournaling(result, Tr.CoreRecoverySourceChanged);
        Assert.True(_fs.FileExists(Raw));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Move group: the same re-check before Prepared (call 2 of the source stat) ...
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RetryMoveOrCopyAsync_MoveGroupSourceVanishesAfterTheCheck_IsRefusedBeforeJournaling()
    {
        AddMoveSources();
        var failed = FailedMoveGroup();
        _journal.Append(failed);
        OnNthStat(Jpg, 2, () => _fs.Delete(Jpg));

        AssertRefusedBeforeJournaling(await _service.RetryMoveOrCopyAsync(failed), Tr.CoreRecoverySourceChanged);
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_MoveGroupSourceResizedAfterTheCheck_IsRefusedBeforeJournaling()
    {
        AddMoveSources();
        var failed = FailedMoveGroup();
        _journal.Append(failed);
        OnNthStat(Jpg, 2, () => _fs.AddFile(Jpg, "jpeg!", Stamp));

        AssertRefusedBeforeJournaling(await _service.RetryMoveOrCopyAsync(failed), Tr.CoreRecoverySourceChanged);
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_MoveGroupSourceTouchedAfterTheCheckWithSameSize_IsRefusedBeforeJournaling()
    {
        AddMoveSources();
        var failed = FailedMoveGroup();
        _journal.Append(failed);
        OnNthStat(Jpg, 2, () => _fs.AddFile(Jpg, "jpeg", Stamp.AddSeconds(30)));

        AssertRefusedBeforeJournaling(await _service.RetryMoveOrCopyAsync(failed), Tr.CoreRecoverySourceChanged);
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_MoveGroupDestinationAppearsAfterTheCheck_IsRefusedBeforeJournaling()
    {
        AddMoveSources();
        var failed = FailedMoveGroup();
        _journal.Append(failed);
        OnNthStat(Jpg, 2, () => _fs.AddFile(OutJpg, "someone else's file", Stamp));

        AssertRefusedBeforeJournaling(await _service.RetryMoveOrCopyAsync(failed), Tr.CoreRecoverySourceChanged);
        Assert.Equal("someone else's file".Length, _fs.GetFileStat(OutJpg)!.Length);
    }

    // ---------------------------------------------------------------------------------------------------------
    // ... and the re-check after Prepared (call 3): the failure is journaled, nothing is moved or overwritten.
    // ---------------------------------------------------------------------------------------------------------

    private async Task<RecoveryRetryResult> RetryMoveGroupWith(Action duringThirdSourceStat)
    {
        AddMoveSources();
        var failed = FailedMoveGroup();
        _journal.Append(failed);
        OnNthStat(Jpg, 3, duringThirdSourceStat);
        return await _service.RetryMoveOrCopyAsync(failed);
    }

    private void AssertFailedAfterPreparedWithoutMoving(RecoveryRetryResult result, string expectedMessage)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(expectedMessage, result.Message);
        Assert.NotNull(result.Entry);
        Assert.Equal(JournalState.Failed, result.Entry!.State);
        Assert.DoesNotContain(_fs.Events, e => e.Kind == "move");
        Assert.False(_fs.FileExists(OutRaw));
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_MoveGroupSourceVanishesAfterPrepared_FailsAsSourceChangedWithoutMoving()
    {
        var result = await RetryMoveGroupWith(() => _fs.Delete(Jpg));

        AssertFailedAfterPreparedWithoutMoving(result, Tr.CoreRecoverySourceChanged);
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_MoveGroupSourceResizedAfterPrepared_FailsAsSourceChangedWithoutMoving()
    {
        var result = await RetryMoveGroupWith(() => _fs.AddFile(Jpg, "jpeg!", Stamp));

        AssertFailedAfterPreparedWithoutMoving(result, Tr.CoreRecoverySourceChanged);
        Assert.True(_fs.FileExists(Jpg));
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_MoveGroupSourceTouchedAfterPreparedWithSameSize_FailsAsSourceChangedWithoutMoving()
    {
        var result = await RetryMoveGroupWith(() => _fs.AddFile(Jpg, "jpeg", Stamp.AddSeconds(30)));

        AssertFailedAfterPreparedWithoutMoving(result, Tr.CoreRecoverySourceChanged);
        Assert.True(_fs.FileExists(Jpg));
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_MoveGroupDestinationAppearsAfterPrepared_FailsAsDestinationExistsAndNeverOverwrites()
    {
        var result = await RetryMoveGroupWith(() => _fs.AddFile(OutJpg, "someone else's file", Stamp));

        AssertFailedAfterPreparedWithoutMoving(result, Tr.CoreRecoveryDestinationExists);
        Assert.True(_fs.FileExists(Jpg));
        Assert.Equal("someone else's file".Length, _fs.GetFileStat(OutJpg)!.Length);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Post-move verification.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RetryMoveOrCopyAsync_MoveGroupMoveLeavesTheSourceBehind_FailsWithRetryVerifyFailed()
    {
        AddMoveSources();
        var failed = FailedMoveGroup();
        _journal.Append(failed);
        _fs.MoveLeavesSource = true; // cross-volume move that copied but could not delete the source

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal(JournalErrors.RetryVerifyFailed, result.Entry!.ErrorCode);
        Assert.Empty(_journal.ReadPendingOperations());
        Assert.Equal(JournalErrors.RetryVerifyFailed, Assert.Single(_journal.ReadFailedOperations()).ErrorCode);
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_MoveGroupMovedFileHasAnotherSize_FailsWithRetryVerifyFailed()
    {
        AddMoveSources();
        var failed = FailedMoveGroup();
        _journal.Append(failed);
        // The file is replaced by a longer one right when it is moved (same write time, so the re-checks passed).
        _fs.MoveHook = (source, _) =>
        {
            if (source == Jpg) _fs.AddFile(Jpg, "jpeg plus more", Stamp);
            return null;
        };

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal(JournalErrors.RetryVerifyFailed, result.Entry!.ErrorCode);
        Assert.False(_fs.FileExists(OutRaw)); // the loop stopped at the first member
    }

    // ---------------------------------------------------------------------------------------------------------
    // Undo of a Delete: the bin said no / the file did not come back.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RetryMoveOrCopyAsync_UndoDeleteGroupBinReportsFailureEvenThoughAFileAppeared_FailsAsRestoreFailed()
    {
        var failed = FailedRecycleGroup(true, new JournalGroupMember(Jpg, null, 4, Stamp), new JournalGroupMember(Raw, null, 8, Stamp));
        _journal.Append(failed);
        _bin.RestoreResult = _ => false; // TryRestore says "not restored" (and still left a file behind)

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreUndoRecycleRestoreFailed("a.jpg"), result.Message);
        Assert.Equal([Jpg], _bin.RestoreCalls); // stopped at the first failure
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_UndoDeleteGroupBinReportsSuccessButNothingCameBack_FailsAsRestoreFailed()
    {
        var failed = FailedRecycleGroup(true, new JournalGroupMember(Jpg, null, 4, Stamp), new JournalGroupMember(Raw, null, 8, Stamp));
        _journal.Append(failed);
        _bin.RestoreRecreatesFile = false;

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreUndoRecycleRestoreFailed("a.jpg"), result.Message);
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_UndoDeleteGroupRestoresEveryMissingMember_Succeeds()
    {
        var failed = FailedRecycleGroup(true, new JournalGroupMember(Jpg, null, 4, Stamp), new JournalGroupMember(Raw, null, 8, Stamp));
        _journal.Append(failed);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal([Jpg, Raw], _bin.RestoreCalls);
        Assert.True(_fs.FileExists(Jpg));
        Assert.True(_fs.FileExists(Raw));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Journal outcome reporting (JournalPersisted / JournalError).
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RetryMoveOrCopyAsync_GroupMoveSucceedsAndJournals_ReportsPlainSuccessWithPersistedJournal()
    {
        AddMoveSources();
        var failed = FailedMoveGroup();
        _journal.Append(failed);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Tr.CoreRecoverySucceeded, result.Message);
        Assert.True(result.JournalPersisted);
        Assert.Null(result.JournalError);
        Assert.Equal(JournalState.Committed, result.Entry!.State);
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_GroupMoveSucceedsButCommitAppendFails_ReportsSuccessWithUnpersistedJournal()
    {
        AddMoveSources();
        var failed = FailedMoveGroup();
        _journal.Append(failed);
        var appends = 0;
        _fs.OpenAppendHook = _ => ++appends == 2 ? new IOException("disk full") : null; // 1 = Prepared, 2 = Committed

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.True(result.Succeeded);
        Assert.Equal(Tr.CoreRecoverySucceededJournalFailed, result.Message);
        Assert.False(result.JournalPersisted);
        Assert.Equal("disk full", result.JournalError);
        Assert.True(_fs.FileExists(OutJpg));
        Assert.True(_fs.FileExists(OutRaw));
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_GroupMoveFailsAndFailureIsJournaled_ReportsTheFailureWithPersistedJournal()
    {
        AddMoveSources();
        var failed = FailedMoveGroup();
        _journal.Append(failed);
        _fs.MoveHook = (_, _) => new IOException("boom");

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal("boom", result.Message);
        Assert.True(result.JournalPersisted);
        Assert.Null(result.JournalError);
        Assert.Equal(JournalState.Failed, result.Entry!.State);
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_GroupMoveFailsAndTheFailureAppendFailsToo_ReportsNotJournaled()
    {
        AddMoveSources();
        var failed = FailedMoveGroup();
        _journal.Append(failed);
        _fs.MoveHook = (_, _) => new IOException("boom");
        var appends = 0;
        _fs.OpenAppendHook = _ => ++appends == 2 ? new IOException("disk full") : null; // 1 = Prepared, 2 = Failed

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreRecoveryCompletedFailureNotJournaled, result.Message);
        Assert.False(result.JournalPersisted);
        Assert.Equal("disk full", result.JournalError);
        Assert.Null(result.Entry);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(NotSupportedException))]
    public async Task RetryMoveOrCopyAsync_MoveGroupRecheckThrowsAFileSystemError_FailsWithItsMessageBeforeJournaling(Type failure)
    {
        AddMoveSources();
        var failed = FailedMoveGroup();
        _journal.Append(failed);
        var count = 0;
        _fs.StatHook = path => path == Jpg && ++count == 2 ? (Exception)Activator.CreateInstance(failure, "share gone")! : null;

        AssertRefusedBeforeJournaling(await _service.RetryMoveOrCopyAsync(failed), "share gone");
    }
}