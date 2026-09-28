using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <param name="Superseded">
/// P02: the retried entry was already handled by another PhotoReview window (another process sharing the journal
/// appended a newer record for its Id, or is executing it right now). Nothing was mutated or journaled by this retry;
/// the caller's snapshot is stale and should be dropped/refreshed (like <see cref="DismissOutcome.Skipped"/>).
/// </param>
public sealed record RecoveryRetryResult(
    bool Succeeded,
    string Message,
    JournalEntry? Entry,
    bool JournalPersisted = true,
    string? JournalError = null,
    bool Superseded = false);

public sealed class RecoveryRetryService
{
    private readonly OperationJournal _journal;
    private readonly IFileSystem _fileSystem;
    private readonly IClock _clock;

    public RecoveryRetryService(OperationJournal journal, IFileSystem fileSystem, IClock clock)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Re-runs a failed Move/Copy. Cheap validation runs on the caller's thread; the journal append, the file
    /// mutation (possibly a large cross-drive copy), verification and the final journal append run on the thread
    /// pool so a UI caller stays responsive (CORE-06).
    /// </summary>
    public async Task<RecoveryRetryResult> RetryMoveOrCopyAsync(JournalEntry failed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(failed);

        if (failed.Type is not (FileOperationType.Move or FileOperationType.Copy))
            return new(false, Tr.CoreRecoveryOnlyMoveCopy, null);
        if (string.IsNullOrWhiteSpace(failed.Destination))
            return new(false, Tr.CoreRecoveryNoDestination, null);
        if (!_fileSystem.FileExists(failed.Source))
            return new(false, Tr.CoreRecoverySourceMissing, null);

        var sourceStat = _fileSystem.GetFileStat(failed.Source);
        if (sourceStat is null || sourceStat.Length != failed.Size || sourceStat.LastWriteUtc != failed.LastWriteUtc)
            return new(false, Tr.CoreRecoverySourceChanged, null);
        if (_fileSystem.FileExists(failed.Destination))
            return new(false, Tr.CoreRecoveryDestinationExists, null);

        // `with` keeps Undo / Permanent: dropping Undo turns a retried undo-Move into a redo on the next Ctrl+Z after a restart.
        var prepared = failed with
        {
            State = JournalState.Prepared,
            Error = null,
            ErrorCode = null,
            Size = sourceStat.Length,
            LastWriteUtc = sourceStat.LastWriteUtc,
            TimestampUtc = _clock.UtcNow,
        };

        return await Task.Run(() => ExecuteRetry(failed, prepared), ct).ConfigureAwait(false);
    }

    private static RecoveryRetryResult AlreadyHandled() => new(false, Tr.CoreRecoveryAlreadyHandled, null, Superseded: true);

    // P02: `failed` is the Recovery window's snapshot. Another process sharing the journal (InstanceMode.PerFolder) may be
    // retrying the same Id right now or may already have resolved it; the filesystem pre-checks alone cannot tell (both
    // processes pass them before either mutates). So: skip when the Id is live elsewhere (Q-R27 marker), append Prepared
    // only if the snapshot is still the latest entry, and append a failure only if nothing but our own Prepared followed
    // the snapshot -- the loser's Failed must never land after the winner's Committed.
    private RecoveryRetryResult ExecuteRetry(JournalEntry failed, JournalEntry prepared)
    {
        if (_journal.LiveOperations.IsLive(failed.Id)) return AlreadyHandled();
        var destination = failed.Destination!; // validated non-empty by the caller
        using var tx = new JournalTransaction(_journal, _clock, prepared, retryOf: failed);
        try
        {
            if (!tx.TryBegin()) return AlreadyHandled();
            var destDir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(destDir))
            {
                _fileSystem.CreateDirectory(destDir);
            }

            if (failed.Type == FileOperationType.Copy)
            {
                _fileSystem.Copy(failed.Source, destination);
                tx.VerifyDestination(_fileSystem, destination, JournalErrors.RetryVerifyFailed);
            }
            else
            {
                _fileSystem.Move(failed.Source, destination);
                tx.VerifyMoved(_fileSystem, failed.Source, destination, JournalErrors.RetryVerifyFailed);
            }

            var committed = tx.Commit(out var commitError);
            return commitError is null
                ? new(true, Tr.CoreRecoverySucceeded, committed)
                : new(true, Tr.CoreRecoverySucceededJournalFailed, committed,
                    JournalPersisted: false, JournalError: commitError);
        }
        catch (Exception ex)
        {
            var error = tx.Fail(ex, out var failError);
            if (tx.Superseded) return AlreadyHandled();
            if (failError is null) return new(false, ex.Message, error);
            return new(tx.MutationCompleted, tx.MutationCompleted
                ? Tr.CoreRecoveryCompletedFailureNotJournaled
                : ex.Message, error, JournalPersisted: false, JournalError: failError);
        }
    }
}
