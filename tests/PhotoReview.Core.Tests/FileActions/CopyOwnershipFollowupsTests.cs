using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Audit follow-ups (wave 2, all P3): W2-FA-05 (group retry refusal texts per verdict) and W2-FA-10 (DuplicateFinder cancellation).
/// </summary>
public sealed class CopyOwnershipFollowupsTests
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

    private static JournalEntry FailedMoveGroup() => new(
        "grp", FileOperationType.Move, JournalState.Failed, Jpg, OutJpg, 4, Stamp, Stamp,
        Error: "earlier failure", GroupId: "capture", GroupMembers:
        [
            new(Jpg, OutJpg, 4, Stamp),
            new(Raw, OutRaw, 8, Stamp),
        ]);

    private async Task<(RecoveryRetryResult Result, RecoveryVerdict Verdict)> RetryAsync(JournalEntry failed)
    {
        var journal = new OperationJournal(Paths, _fs, new Clock());
        journal.Append(failed);
        var verdict = new RecoveryFileCheck(_fs).Check(failed).Verdict;
        var result = await new RecoveryRetryService(journal, _fs, new Clock()).RetryMoveOrCopyAsync(failed);
        return (result, verdict);
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_GroupAlreadyFullyMoved_ReportsAlreadyHandledAndSuperseded()
    {
        _fs.AddFile(OutJpg, "jpeg", Stamp);
        _fs.AddFile(OutRaw, "raw data", Stamp);

        var (result, verdict) = await RetryAsync(FailedMoveGroup());

        Assert.Equal(RecoveryVerdict.AlreadyDone, verdict);
        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreRecoveryAlreadyHandled, result.Message);
        Assert.True(result.Superseded);
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_GroupWithSourceAndDestinationBothPresent_ReportsConflictText()
    {
        _fs.AddFile(Jpg, "jpeg", Stamp);
        _fs.AddFile(OutJpg, "jpeg", Stamp);
        _fs.AddFile(Raw, "raw data", Stamp);

        var (result, verdict) = await RetryAsync(FailedMoveGroup());

        Assert.Equal(RecoveryVerdict.Conflict, verdict);
        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreRecoveryConflict, result.Message);
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_GroupWithDestinationNoLongerTheMovedFile_ReportsDestinationChangedText()
    {
        _fs.AddFile(OutJpg, "jpeg but edited afterwards", Stamp); // source gone, destination has a different size
        _fs.AddFile(Raw, "raw data", Stamp);

        var (result, verdict) = await RetryAsync(FailedMoveGroup());

        Assert.Equal(RecoveryVerdict.DestinationChanged, verdict);
        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreRecoveryDestinationChanged, result.Message);
    }

    [Fact]
    public async Task RetryMoveOrCopyAsync_GroupWithChangedSource_StillReportsSourceChangedText()
    {
        _fs.AddFile(Jpg, "jpeg edited, different size", Stamp);
        _fs.AddFile(Raw, "raw data", Stamp);

        var (result, verdict) = await RetryAsync(FailedMoveGroup());

        Assert.Equal(RecoveryVerdict.SourceChanged, verdict);
        Assert.Equal(Tr.CoreRecoverySourceChanged, result.Message);
    }

    [Fact]
    public async Task FindAsync_CancelledDuringSizeScan_StopsAtTheNextPath()
    {
        using var cts = new CancellationTokenSource();
        string[] files = [@"C:\p\a.jpg", @"C:\p\b.jpg", @"C:\p\c.jpg", @"C:\p\d.jpg"];
        for (var i = 0; i < files.Length; i++) _fs.WriteAllTextAtomic(files[i], new string('x', i + 1)); // distinct sizes: nothing to hash
        var statted = 0;
        _fs.StatHook = _ =>
        {
            if (Interlocked.Increment(ref statted) == 1) cts.Cancel(); // the user cancels while the first file is being measured
            return null;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DuplicateFinder.FindAsync(
            files, removeNumbered: true, hash: (_, _) => Task.FromResult("h"), fileSystem: _fs, cancellationToken: cts.Token));

        Assert.Equal(1, statted); // the scan must not keep measuring the remaining files after cancellation
    }
}
