using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

public sealed record RecoveryRetryResult(
    bool Succeeded,
    string Message,
    JournalEntry? Entry,
    bool JournalPersisted = true,
    string? JournalError = null);

public sealed class RecoveryRetryService
{
    private readonly OperationJournal _journal;
    private readonly IFileSystem _fileSystem;
    private readonly IClock _clock;

    public RecoveryRetryService()
        : this(new OperationJournal(), new PhysicalFileSystem(), new SystemClock())
    {
    }

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

        var prepared = new JournalEntry(
            failed.Id,
            failed.Type,
            JournalState.Prepared,
            failed.Source,
            failed.Destination,
            sourceStat.Length,
            sourceStat.LastWriteUtc,
            _clock.UtcNow);

        return await Task.Run(() => ExecuteRetry(failed, prepared), ct).ConfigureAwait(false);
    }

    private RecoveryRetryResult ExecuteRetry(JournalEntry failed, JournalEntry prepared)
    {
        var destination = failed.Destination!; // validated non-empty by the caller
        var tx = new JournalTransaction(_journal, _clock, prepared, failWithoutPrepared: true);
        try
        {
            tx.Begin();
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
            if (failError is null) return new(false, ex.Message, error);
            return new(tx.MutationCompleted, tx.MutationCompleted
                ? Tr.CoreRecoveryCompletedFailureNotJournaled
                : ex.Message, error, JournalPersisted: false, JournalError: failError);
        }
    }
}
