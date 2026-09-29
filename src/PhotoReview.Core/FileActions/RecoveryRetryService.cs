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
    private readonly IRecycleBin? _recycleBin;

    public RecoveryRetryService(OperationJournal journal, IFileSystem fileSystem, IClock clock, IRecycleBin? recycleBin = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _recycleBin = recycleBin;
    }

    /// <summary>
    /// Re-runs a failed Move/Copy. Cheap validation runs on the caller's thread; the journal append, the file
    /// mutation (possibly a large cross-drive copy), verification and the final journal append run on the thread
    /// pool so a UI caller stays responsive (CORE-06).
    /// </summary>
    public async Task<RecoveryRetryResult> RetryMoveOrCopyAsync(JournalEntry failed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(failed);

        if (failed.GroupMembers is { Count: > 0 })
            return await RetryGroupAsync(failed, ct).ConfigureAwait(false);

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

    private async Task<RecoveryRetryResult> RetryGroupAsync(JournalEntry failed, CancellationToken ct)
    {
        if (failed.Type is not (FileOperationType.Move or FileOperationType.Copy or FileOperationType.Recycle))
            return new(false, Tr.CoreRecoveryOnlyMoveCopy, null);
        if (_journal.LiveOperations.IsLive(failed.Id)) return AlreadyHandled();

        var check = new RecoveryFileCheck(_fileSystem).Check(failed);
        if (check.Verdict != RecoveryVerdict.CanRetry || check.GroupMembers is null)
            return new(false, Tr.CoreRecoverySourceChanged, null);

        // Move/Copy: members whose own verdict is CanRetry. Delete (Recycle) undo: members still missing (restore them).
        // Delete: members still on disk (NotRecycled); those already recycled or deleted permanently are left alone.
        var pending = check.GroupMembers.Where(member => failed.Type != FileOperationType.Recycle
            ? member.Check.Verdict == RecoveryVerdict.CanRetry
            : failed.Undo == true
                ? member.Check.Source.Status == RecoveryPathStatus.Missing
                : member.Check.Verdict == RecoveryVerdict.NotRecycled).ToArray();
        if (pending.Length == 0) return new(false, Tr.CoreRecoveryAlreadyHandled, null, Superseded: true);
        if (failed.Type == FileOperationType.Recycle && _recycleBin is null)
            return new(false, Tr.CoreRecoveryOnlyMoveCopy, null);
        try
        {
            foreach (var item in pending)
            {
                var member = item.Member;
                if (failed.Type == FileOperationType.Recycle)
                {
                    if (failed.Undo == true)
                    {
                        if (member.Permanent) return new(false, Tr.CoreRecoverySourceChanged, null);
                    }
                    else
                    {
                        var stat = _fileSystem.GetFileStat(member.Source);
                        if (stat is null || stat.Length != member.Size || stat.LastWriteUtc != member.LastWriteUtc
                            || member.Permanent && _recycleBin!.CanRecycle(member.Source)
                            || !member.Permanent && (!_recycleBin!.CanRecycle(member.Source) || !_recycleBin.FitsInRecycleBin(member.Source, member.Size)))
                            return new(false, Tr.CoreRecoverySourceChanged, null);
                    }
                }
                else
                {
                    var sourcePath = member.Source;
                    var destinationPath = member.Destination;
                    var stat = _fileSystem.GetFileStat(sourcePath);
                    if (stat is null || stat.Length != member.Size || stat.LastWriteUtc != member.LastWriteUtc
                        || string.IsNullOrWhiteSpace(destinationPath) || _fileSystem.FileExists(destinationPath))
                        return new(false, Tr.CoreRecoverySourceChanged, null);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(false, ex.Message, null);
        }

        var prepared = failed with { State = JournalState.Prepared, Error = null, ErrorCode = null, TimestampUtc = _clock.UtcNow };
        return await Task.Run(() => ExecuteGroupRetry(failed, prepared, pending, ct), ct).ConfigureAwait(false);
    }

    private RecoveryRetryResult ExecuteGroupRetry(JournalEntry failed, JournalEntry prepared,
        IReadOnlyList<RecoveryGroupMemberCheck> pending, CancellationToken ct)
    {
        using var tx = new JournalTransaction(_journal, _clock, prepared, retryOf: failed);
        try
        {
            if (!tx.TryBegin()) return AlreadyHandled();
            foreach (var item in pending)
            {
                ct.ThrowIfCancellationRequested();
                var member = item.Member;
                if (failed.Type == FileOperationType.Recycle)
                {
                    if (failed.Undo == true)
                    {
                        if (_fileSystem.FileExists(member.Source)) continue;
                        if (member.Permanent) throw new IOException(Tr.CoreRecoverySourceChanged);
                        if (!_recycleBin!.TryRestore(member.Source, member.Size, member.LastWriteUtc)
                            || !_fileSystem.FileExists(member.Source))
                            throw new IOException(Tr.CoreUndoRecycleRestoreFailed(Path.GetFileName(member.Source)));
                    }
                    else if (member.Permanent)
                    {
                        if (_recycleBin!.CanRecycle(member.Source)) throw new IOException(Tr.CoreRecoverySourceChanged);
                        _recycleBin.DeletePermanently(member.Source);
                    }
                    else _recycleBin!.SendToRecycleBin(member.Source);
                    if (failed.Undo != true && _fileSystem.FileExists(member.Source)) throw new IOException(JournalErrors.SourceStillExistsAfterRecovery);
                    if (failed.Undo == true && !_fileSystem.FileExists(member.Source))
                        throw new IOException(Tr.CoreUndoRecycleRestoreFailed(Path.GetFileName(member.Source)));
                }
                else
                {
                    var sourcePath = member.Source;
                    var destinationPath = member.Destination;
                    var sourceStat = _fileSystem.GetFileStat(sourcePath);
                    if (sourceStat is null || sourceStat.Length != member.Size || sourceStat.LastWriteUtc != member.LastWriteUtc)
                        throw new IOException(Tr.CoreRecoverySourceChanged);
                    if (string.IsNullOrWhiteSpace(destinationPath) || _fileSystem.FileExists(destinationPath))
                        throw new IOException(Tr.CoreRecoveryDestinationExists);
                    var folder = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrWhiteSpace(folder)) _fileSystem.CreateDirectory(folder);
                    if (failed.Type == FileOperationType.Copy)
                    {
                        _fileSystem.Copy(sourcePath, destinationPath);
                        var copied = _fileSystem.GetFileStat(destinationPath);
                        if (copied?.Length != member.Size) throw new IOException(JournalErrors.RetryVerifyFailed);
                    }
                    else
                    {
                        _fileSystem.Move(sourcePath, destinationPath);
                        if (_fileSystem.FileExists(sourcePath) || _fileSystem.GetFileStat(destinationPath)?.Length != member.Size)
                            throw new IOException(JournalErrors.RetryVerifyFailed);
                    }
                }
            }
            tx.MarkMutationCompleted();
            var committed = tx.Commit(out var commitError);
            return commitError is null
                ? new(true, Tr.CoreRecoverySucceeded, committed)
                : new(true, Tr.CoreRecoverySucceededJournalFailed, committed, JournalPersisted: false, JournalError: commitError);
        }
        catch (Exception ex)
        {
            var entry = tx.Fail(ex, out var failError);
            if (tx.Superseded) return AlreadyHandled();
            return failError is null
                ? new(tx.MutationCompleted, ex.Message, entry)
                : new(tx.MutationCompleted, Tr.CoreRecoveryCompletedFailureNotJournaled, entry, JournalPersisted: false, JournalError: failError);
        }
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
