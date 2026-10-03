using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>An entry the user cancelled must not be finished by a plain Retry: it needs an explicit confirmation.</summary>
public sealed class RecoveryRetryCancelledTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
    private const string Jpg = @"C:\photos\a.jpg";
    private const string Raw = @"C:\photos\a.cr2";

    private readonly InMemoryFileSystem _fs = new();
    private readonly OperationJournal _journal;
    private readonly RecoveryRetryService _service;

    public RecoveryRetryCancelledTests()
    {
        _fs.AddFile(Jpg, "jpeg", Stamp);
        _fs.AddFile(Raw, "raw data", Stamp);
        _journal = new OperationJournal(Paths, _fs, new FixedClock(Stamp.AddMinutes(1)));
        _service = new RecoveryRetryService(_journal, _fs, new FixedClock(Stamp.AddMinutes(2)));
    }

    private static JournalEntry CancelledGroupCopy() => new(
        "copy-group", FileOperationType.Copy, JournalState.Failed, Jpg, @"C:\photos\out\a.jpg", 4, Stamp, Stamp,
        Error: JournalErrors.EnglishText(JournalErrors.CancelledByUser), ErrorCode: JournalErrors.CancelledByUser,
        GroupId: "capture", GroupMembers:
        [
            new(Jpg, @"C:\photos\out\a.jpg", 4, Stamp),
            new(Raw, @"C:\photos\out\a.cr2", 8, Stamp),
        ]);

    [Fact]
    public async Task RetryMoveOrCopyAsync_CancelledGroupCopyWithoutConfirmation_IsRefusedAndNothingIsCopied()
    {
        var failed = CancelledGroupCopy();
        _journal.Append(failed);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreRecoveryCancelledNeedsConfirm, result.Message);
        Assert.False(_fs.FileExists(@"C:\photos\out\a.jpg"));
        Assert.False(_fs.FileExists(@"C:\photos\out\a.cr2"));
        Assert.Equal(failed, Assert.Single(_journal.ReadFailedOperations())); // untouched: still the same Failed line
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_CancelledGroupCopyExplicitlyConfirmed_FinishesTheCopy()
    {
        var failed = CancelledGroupCopy();
        _journal.Append(failed);

        var result = await _service.RetryMoveOrCopyAsync(failed, confirmedFinishCancelled: true);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(_fs.FileExists(@"C:\photos\out\a.jpg"));
        Assert.True(_fs.FileExists(@"C:\photos\out\a.cr2"));
        Assert.Empty(_journal.ReadFailedOperations());
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_CancelledSingleMoveWithoutConfirmation_IsRefused()
    {
        var failed = new JournalEntry("move", FileOperationType.Move, JournalState.Failed, Jpg, @"C:\photos\out\a.jpg", 4, Stamp, Stamp,
            Error: "x", ErrorCode: JournalErrors.CancelledByUser);
        _journal.Append(failed);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.True(_fs.FileExists(Jpg));
        Assert.False(_fs.FileExists(@"C:\photos\out\a.jpg"));
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_NonCancelledFailure_DoesNotNeedConfirmation()
    {
        var failed = CancelledGroupCopy() with { ErrorCode = null, Error = "disk full" };
        _journal.Append(failed);

        var result = await _service.RetryMoveOrCopyAsync(failed);

        Assert.True(result.Succeeded, result.Message);
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
