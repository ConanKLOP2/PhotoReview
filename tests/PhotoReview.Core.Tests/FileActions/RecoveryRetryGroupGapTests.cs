using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>RV-T07 gap: group Recovery retry cancelled mid-loop and a Copy group retry with a destination already present.</summary>
public sealed class RecoveryRetryGroupGapTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private const string Jpg = @"C:\photos\a.jpg";
    private const string Raw = @"C:\photos\a.cr2";
    private const string OutJpg = @"C:\photos\out\a.jpg";
    private const string OutRaw = @"C:\photos\out\a.cr2";

    private sealed class Clock : IClock
    {
        public DateTime UtcNow => Stamp.AddMinutes(1);
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly OperationJournal _journal;
    private readonly RecoveryRetryService _service;

    public RecoveryRetryGroupGapTests()
    {
        _fs.AddFile(Jpg, "jpeg", Stamp);
        _fs.AddFile(Raw, "raw data", Stamp);
        _journal = new OperationJournal(Paths, _fs, new Clock());
        _service = new RecoveryRetryService(_journal, _fs, new Clock());
    }

    private static JournalEntry FailedGroup(FileOperationType type, string? errorCode = null) => new(
        "grp", type, JournalState.Failed, Jpg, OutJpg, 4, Stamp, Stamp,
        Error: "earlier failure", ErrorCode: errorCode, GroupId: "capture", GroupMembers:
        [
            new(Jpg, OutJpg, 4, Stamp),
            new(Raw, OutRaw, 8, Stamp),
        ]);

    [Fact]
    public async Task RetryMoveOrCopyAsync_GroupMoveCancelledAfterFirstMember_FailsCancelledAndKeepsTheCompletedMember()
    {
        var failed = FailedGroup(FileOperationType.Move);
        _journal.Append(failed);
        using var cts = new CancellationTokenSource();
        _fs.MoveHook = (source, _) =>
        {
            if (source == Jpg) cts.Cancel(); // the user cancels while the first member is being moved
            return null;
        };

        var result = await _service.RetryMoveOrCopyAsync(failed, ct: cts.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(JournalErrors.CancelledByUser, result.Entry!.ErrorCode);
        Assert.False(_fs.FileExists(Jpg));  // completed member stays moved: a retry does not undo
        Assert.True(_fs.FileExists(OutJpg));
        Assert.True(_fs.FileExists(Raw));   // the second member was never touched
        Assert.False(_fs.FileExists(OutRaw));
        var latest = Assert.Single(_journal.ReadFailedOperations());
        Assert.Equal(JournalErrors.CancelledByUser, latest.ErrorCode);
        Assert.Empty(_journal.ReadPendingOperations());
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_GroupCancelledMidLoopThenConfirmedAndRetried_FinishesOnlyTheRemainingMember()
    {
        var failed = FailedGroup(FileOperationType.Move);
        _journal.Append(failed);
        using var cts = new CancellationTokenSource();
        _fs.MoveHook = (source, _) =>
        {
            if (source == Jpg) cts.Cancel();
            return null;
        };
        var cancelled = await _service.RetryMoveOrCopyAsync(failed, ct: cts.Token);
        _fs.MoveHook = null;

        // Plain retry is refused (the entry says the user cancelled); the explicit confirmation finishes the rest.
        var refused = await _service.RetryMoveOrCopyAsync(cancelled.Entry!);
        Assert.Equal(Tr.CoreRecoveryCancelledNeedsConfirm, refused.Message);
        var finished = await _service.RetryMoveOrCopyAsync(cancelled.Entry!, confirmedFinishCancelled: true);

        Assert.True(finished.Succeeded, finished.Message);
        Assert.True(_fs.FileExists(OutJpg));
        Assert.True(_fs.FileExists(OutRaw));
        Assert.Empty(_journal.ReadFailedOperations());
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_GroupCopyWithOneDestinationAlreadyComplete_CopiesOnlyTheMissingMemberAndCommits()
    {
        var failed = FailedGroup(FileOperationType.Copy);
        _journal.Append(failed);
        _fs.AddFile(OutRaw, "raw data", Stamp); // the first run got as far as the second member before failing

        var result = await _service.RetryMoveOrCopyAsync(failed);

        // Pinned verdict: members whose destination is already in place are left alone, the missing one is copied.
        Assert.True(result.Succeeded, result.Message);
        Assert.True(_fs.FileExists(OutJpg));
        Assert.Equal(8, _fs.GetFileStat(OutRaw)!.Length);
        Assert.Empty(_journal.ReadFailedOperations());
    }
}
