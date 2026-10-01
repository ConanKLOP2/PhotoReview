using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>Recovery verdict and retry for a capture-group Delete (Recycle group) that failed part-way. Fakes only: no real Recycle Bin.</summary>
public sealed class RecoveryRecycleGroupTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
    private const string Jpg = @"C:\photos\a.jpg";
    private const string Raw = @"C:\photos\a.cr2";

    private readonly InMemoryFileSystem _fs = new();
    private readonly FakeBin _bin;
    private readonly OperationJournal _journal;
    private readonly RecoveryRetryService _service;

    public RecoveryRecycleGroupTests()
    {
        _bin = new FakeBin(_fs);
        _journal = new OperationJournal(Paths, _fs, new FixedClock(Stamp.AddMinutes(1)));
        _service = new RecoveryRetryService(_journal, _fs, new FixedClock(Stamp.AddMinutes(2)), _bin);
    }

    private static JournalGroupMember Member(string path, long size = 10, bool permanent = false) =>
        new(path, null, size, Stamp, permanent);

    private static JournalEntry FailedGroup(params JournalGroupMember[] members) => new(
        "delete-group", FileOperationType.Recycle, JournalState.Failed, members[0].Source, null, members[0].Size, Stamp, Stamp,
        Error: "x", ErrorCode: JournalErrors.SourceStillExistsAfterRecovery, Permanent: members.Any(m => m.Permanent) ? true : null,
        GroupId: "capture-a", GroupMembers: members);

    private RecoveryCheckResult Check(JournalEntry entry) => new RecoveryFileCheck(_fs).Check(entry);

    [Fact]
    public void Check_RecycleGroupWithSomeMembersStillPresent_CanRetry()
    {
        _fs.AddFile(Raw, new string('r', 10), Stamp); // the JPEG already went to the bin, the RAW is still here

        var result = Check(FailedGroup(Member(Jpg), Member(Raw)));

        Assert.Equal(RecoveryVerdict.CanRetry, result.Verdict);
    }

    [Fact]
    public void Check_RecycleGroupWithNothingRecycledYet_CanRetry()
    {
        _fs.AddFile(Jpg, new string('j', 10), Stamp);
        _fs.AddFile(Raw, new string('r', 10), Stamp);

        Assert.Equal(RecoveryVerdict.CanRetry, Check(FailedGroup(Member(Jpg), Member(Raw))).Verdict);
    }

    [Fact]
    public void Check_RecycleGroupWherePresentMemberChanged_IsNotRecycledNotRetryable()
    {
        _fs.AddFile(Raw, new string('r', 11), Stamp); // size differs from the journal

        var result = Check(FailedGroup(Member(Jpg), Member(Raw)));

        Assert.Equal(RecoveryVerdict.NotRecycled, result.Verdict);
    }

    [Fact]
    public void Check_RecycleGroupAllMembersGone_IsRecycleUnverifiable()
    {
        Assert.Equal(RecoveryVerdict.RecycleUnverifiable, Check(FailedGroup(Member(Jpg), Member(Raw))).Verdict);
    }

    [Fact]
    public void Check_RecycleGroupAllMembersPermanentlyDeleted_IsPermanentlyDeleted()
    {
        var result = Check(FailedGroup(Member(Jpg, permanent: true), Member(Raw, permanent: true)));

        Assert.Equal(RecoveryVerdict.PermanentlyDeleted, result.Verdict);
    }

    [Fact]
    public void Check_RecycleGroupMixingPermanentAndRecycledMembers_IsPartiallyPermanentlyDeleted()
    {
        // JPEG on a drive without a bin (deleted for good), RAW recycled: the capture is NOT wholly unrecoverable.
        var result = Check(FailedGroup(Member(Jpg, permanent: true), Member(Raw)));

        Assert.Equal(RecoveryVerdict.PartiallyPermanentlyDeleted, result.Verdict);
        Assert.Equal("PartiallyPermanentlyDeleted", RecoveryFileCheck.Code(result.Verdict));
        Assert.Equal(RecoveryVerdict.PermanentlyDeleted, result.GroupMembers![0].Verdict);
        Assert.Equal(RecoveryVerdict.RecycleUnverifiable, result.GroupMembers![1].Verdict);
    }

    [Fact]
    public async Task Retry_RecycledMemberStillOnDisk_JournalsTheCodedErrorSoRecoveryCanLocalizeIt()
    {
        _fs.AddFile(Raw, new string('r', 10), Stamp);
        _bin.KeepFileOnRecycle = true; // the bin call "succeeds" but the file is still there
        var failed = FailedGroup(Member(Jpg), Member(Raw));
        _journal.Append(failed);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal(JournalErrors.SourceStillExistsAfterRecovery, result.Entry!.ErrorCode); // was null: a bare IOException carried only the identifier text
    }


    [Fact]
    public async Task Retry_PartlyRecycledGroup_RecyclesOnlyTheMembersStillPresentAndCommits()
    {
        _fs.AddFile(Raw, new string('r', 10), Stamp);
        var failed = FailedGroup(Member(Jpg), Member(Raw));
        _journal.Append(failed);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal([Raw], _bin.Recycled);
        Assert.Empty(_bin.PermanentlyDeleted);
        Assert.Equal(JournalState.Committed, result.Entry!.State);
        Assert.Empty(_journal.ReadFailedOperations());
        Assert.Empty(_journal.ReadPendingOperations());
    }

    [Fact]
    public void Retry_GroupRecycle_RunsTheBinCallsOffTheCallerThread()
    {
        _fs.AddFile(Jpg, new string('j', 10), Stamp);
        _fs.AddFile(Raw, new string('r', 10), Stamp);
        var failed = FailedGroup(Member(Jpg), Member(Raw));
        _journal.Append(failed);
        RecoveryRetryResult? result = null;
        var callerThread = 0;
        // A blocked dedicated thread (like an awaiting UI thread): work left on the caller thread would run here.
        var thread = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            result = _service.RetryMoveOrCopyAsync(failed).GetAwaiter().GetResult();
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));

        Assert.True(result!.Succeeded, result.Message);
        Assert.Equal(2, _bin.ThreadIds.Count);
        Assert.All(_bin.ThreadIds, id => Assert.NotEqual(callerThread, id));
    }

    [Fact]
    public async Task Retry_BinFitsEachMemberButNotTheirSum_IsRefusedAndNothingIsRecycled()
    {
        _fs.AddFile(Jpg, new string('j', 10), Stamp);
        _fs.AddFile(Raw, new string('r', 10), Stamp);
        _bin.Capacity = 15; // each 10-byte file fits, both together (20) do not
        var failed = FailedGroup(Member(Jpg), Member(Raw));
        _journal.Append(failed);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreRecycleBinCannotHold("a.jpg"), result.Message);
        Assert.Empty(_bin.Recycled);
        Assert.True(_fs.FileExists(Jpg));
        Assert.True(_fs.FileExists(Raw));
        Assert.Equal(JournalState.Failed, Assert.Single(_journal.ReadFailedOperations()).State); // still the same retryable item
    }

    [Fact]
    public async Task Retry_BinFitsTheMembersStillPendingEvenIfNotTheWholeGroup_Succeeds()
    {
        _fs.AddFile(Raw, new string('r', 10), Stamp); // the JPEG was already recycled by the first attempt
        _bin.Capacity = 15;
        var failed = FailedGroup(Member(Jpg), Member(Raw));
        _journal.Append(failed);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal([Raw], _bin.Recycled);
    }

    [Fact]
    public async Task Retry_PresentMemberChangedSinceJournal_IsRefusedAndNothingIsRecycled()
    {
        _fs.AddFile(Raw, new string('r', 11), Stamp);
        var failed = FailedGroup(Member(Jpg), Member(Raw));
        _journal.Append(failed);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Empty(_bin.Recycled);
        Assert.Empty(_bin.PermanentlyDeleted);
    }

    [Fact]
    public async Task Retry_MemberOnDriveWithoutRecycleBinNotJournaledPermanent_IsRefusedAndNeverDeleted()
    {
        _fs.AddFile(Jpg, new string('j', 10), Stamp);
        _fs.AddFile(Raw, new string('r', 10), Stamp);
        _bin.NoBinPaths.Add(Raw);
        var failed = FailedGroup(Member(Jpg), Member(Raw));
        _journal.Append(failed);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Empty(_bin.Recycled);
        Assert.Empty(_bin.PermanentlyDeleted);
        Assert.True(_fs.FileExists(Jpg));
        Assert.True(_fs.FileExists(Raw));
    }

    [Fact]
    public async Task Retry_MemberJournaledPermanentOnDriveWithoutBin_IsDeletedPermanentlyOnlyThatMember()
    {
        _fs.AddFile(Jpg, new string('j', 10), Stamp);
        _fs.AddFile(Raw, new string('r', 10), Stamp);
        _bin.NoBinPaths.Add(Raw);
        var failed = FailedGroup(Member(Jpg), Member(Raw, permanent: true));
        _journal.Append(failed);
        var service = new RecoveryRetryService(_journal, _fs, new FixedClock(Stamp.AddMinutes(2)), _bin, () => true);

        var result = await service.RetryMoveOrCopyAsync(failed);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal([Jpg], _bin.Recycled);
        Assert.Equal([Raw], _bin.PermanentlyDeleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Retry_MemberJournaledPermanentButSettingNowOff_IsRefusedBeforeAnyMutation(bool? setting)
    {
        _fs.AddFile(Jpg, new string('j', 10), Stamp);
        _fs.AddFile(Raw, new string('r', 10), Stamp);
        _bin.NoBinPaths.Add(Raw);
        var failed = FailedGroup(Member(Jpg), Member(Raw, permanent: true));
        _journal.Append(failed);
        var service = new RecoveryRetryService(_journal, _fs, new FixedClock(Stamp.AddMinutes(2)), _bin,
            setting is null ? null : () => setting.Value);

        var result = await service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreRecycleUnsupportedDrive("a.cr2"), result.Message);
        Assert.Empty(_bin.Recycled);
        Assert.Empty(_bin.PermanentlyDeleted);
        Assert.True(_fs.FileExists(Jpg));
        Assert.True(_fs.FileExists(Raw));
        Assert.Single(_journal.ReadFailedOperations()); // still the one Failed line, nothing new journaled
    }

    [Fact]
    public async Task Retry_WithoutRecycleBinService_IsRefusedAndNothingChanges()
    {
        _fs.AddFile(Raw, new string('r', 10), Stamp);
        var service = new RecoveryRetryService(_journal, _fs, new FixedClock(Stamp.AddMinutes(2)));
        var failed = FailedGroup(Member(Jpg), Member(Raw));
        _journal.Append(failed);

        var result = await service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.True(_fs.FileExists(Raw));
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class FakeBin(InMemoryFileSystem fs) : IRecycleBin
    {
        public List<string> Recycled { get; } = [];
        public List<string> PermanentlyDeleted { get; } = [];
        public HashSet<string> NoBinPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<int> ThreadIds { get; } = [];
        public bool KeepFileOnRecycle { get; set; }

        public void SendToRecycleBin(string path)
        {
            ThreadIds.Add(Environment.CurrentManagedThreadId);
            Recycled.Add(path);
            if (!KeepFileOnRecycle) fs.Delete(path);
        }

        public bool CanRecycle(string path) => !NoBinPaths.Contains(path);
        public long Capacity { get; set; } = long.MaxValue;
        public bool FitsInRecycleBin(string path, long fileSize) => fileSize <= Capacity;

        public void DeletePermanently(string path)
        {
            PermanentlyDeleted.Add(path);
            fs.Delete(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }
}
