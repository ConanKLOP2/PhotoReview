using PhotoReview.Core.Abstractions;
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

    private readonly Func<bool>? _allowPermanentDelete;
    private readonly FileActionService? _fileActionGate;

    /// <param name="allowPermanentDelete">Current value of the permanent-delete-without-Recycle-Bin setting (Q-R8), read at retry
    /// time. Null = not allowed: a group Delete retry never permanently deletes a journaled-Permanent member unless the setting is on now.</param>
    /// <param name="fileActionGate">RV-C08: the app's INV-4 file-action gate. A retry takes it like any other file action and is
    /// refused (<see cref="Tr.CoreRecoveryBusy"/>, nothing touched) while a Move/Copy/Delete/Undo holds it. Null = no gate (tests).</param>
    public RecoveryRetryService(OperationJournal journal, IFileSystem fileSystem, IClock clock, IRecycleBin? recycleBin = null,
        Func<bool>? allowPermanentDelete = null, FileActionService? fileActionGate = null)
    {
        _allowPermanentDelete = allowPermanentDelete;
        _fileActionGate = fileActionGate;
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
    /// <param name="confirmedFinishCancelled">
    /// True only when the user explicitly confirmed "finish the operation I cancelled" (the Recovery window's dedicated
    /// confirmation). An entry journaled <see cref="JournalErrors.CancelledByUser"/> is otherwise refused: a retry would
    /// complete exactly what the user cancelled.
    /// </param>
    public async Task<RecoveryRetryResult> RetryMoveOrCopyAsync(JournalEntry failed, bool confirmedFinishCancelled = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(failed);
        // RV-C08: a retry is a file action like any other (INV-4). Refused before anything is read or journaled while a
        // Move/Copy/Delete/Undo holds the gate; the window's modality alone did not prevent the overlap.
        if (_fileActionGate is not null && !_fileActionGate.TryBegin())
            return new(false, Tr.CoreRecoveryBusy, null);
        try
        {
            return await RetryCoreAsync(failed, confirmedFinishCancelled, ct).ConfigureAwait(false);
        }
        finally
        {
            _fileActionGate?.End();
        }
    }

    private async Task<RecoveryRetryResult> RetryCoreAsync(JournalEntry failed, bool confirmedFinishCancelled, CancellationToken ct)
    {
        if (!confirmedFinishCancelled && string.Equals(failed.ErrorCode, JournalErrors.CancelledByUser, StringComparison.Ordinal))
            return new(false, Tr.CoreRecoveryCancelledNeedsConfirm, null);

        if (failed.GroupMembers is { Count: > 0 })
            return await RetryGroupAsync(failed, ct).ConfigureAwait(false);

        if (failed.Type is not (FileOperationType.Move or FileOperationType.Copy))
            return new(false, Tr.CoreRecoveryOnlyMoveCopy, null);
        if (string.IsNullOrWhiteSpace(failed.Destination))
            return new(false, Tr.CoreRecoveryNoDestination, null);
        FileStat sourceStat;
        try
        {
            // Same guard as the group path: a path the file system rejects (or an unreachable share) is a failed retry, not a crash.
            if (!_fileSystem.FileExists(failed.Source))
                return new(false, Tr.CoreRecoverySourceMissing, null);

            var stat = _fileSystem.GetFileStat(failed.Source);
            if (stat is null || stat.Length != failed.Size || stat.LastWriteUtc != failed.LastWriteUtc)
                return new(false, Tr.CoreRecoverySourceChanged, null);
            sourceStat = stat;
            if (_fileSystem.FileExists(failed.Destination))
                return new(false, Tr.CoreRecoveryDestinationExists, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(false, ex.Message, null);
        }

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
        {
            return check.Verdict switch
            {
                RecoveryVerdict.AlreadyDone => AlreadyHandled(),
                RecoveryVerdict.Conflict => new(false, Tr.CoreRecoveryConflict, null),
                RecoveryVerdict.DestinationChanged => new(false, Tr.CoreRecoveryDestinationChanged, null),
                _ => new(false, Tr.CoreRecoverySourceChanged, null),
            };
        }

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
        // Same rule as the first run (FileActionService): permanent deletion needs the setting; refuse before anything is mutated.
        if (failed.Type == FileOperationType.Recycle && failed.Undo != true && _allowPermanentDelete?.Invoke() != true
            && pending.FirstOrDefault(item => item.Member.Permanent) is { } permanentItem)
            return new(false, Tr.CoreRecycleUnsupportedDrive(Path.GetFileName(permanentItem.Member.Source)), null);
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
                            || !member.Permanent && !_recycleBin!.CanRecycle(member.Source))
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

        // Same per-volume cumulative rule as the first run: a bin that fits each file but not their sum would make the
        // shell delete the overflow permanently while the journal says Recycle.
        if (failed.Type == FileOperationType.Recycle && failed.Undo != true
            && RecycleBinCapacity.FirstOverflow(_recycleBin!, pending.Select(item => item.Member)) is { } overflow)
            return new(false, Tr.CoreRecycleBinCannotHold(Path.GetFileName(overflow.Source)), null);

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
                        // R15: this member was Missing at the pre-check (it is in the bin). Whatever sits at its path now is not the
                        // recycled file (a foreign file, or a racing manual restore the next check will recognise by identity):
                        // never count it as restored and never ask the shell to replace it. Fail closed, nothing is guessed.
                        if (_fileSystem.FileExists(member.Source))
                            throw new IOException(Tr.CoreUndoRecycleTargetExists(Path.GetFileName(member.Source)));
                        if (member.Permanent) throw new IOException(Tr.CoreRecoverySourceChanged);
                        if (!_recycleBin!.TryRestore(member.Source, member.Size, member.LastWriteUtc)
                            || !IsRestoredFile(member))
                            throw new IOException(Tr.CoreUndoRecycleRestoreFailed(Path.GetFileName(member.Source)));
                    }
                    else if (member.Permanent)
                    {
                        if (_recycleBin!.CanRecycle(member.Source)) throw new IOException(Tr.CoreRecoverySourceChanged);
                        _recycleBin.DeletePermanently(member.Source);
                    }
                    else _recycleBin!.SendToRecycleBin(member.Source);
                    if (failed.Undo != true && _fileSystem.FileExists(member.Source)) throw new JournalCodedException(JournalErrors.SourceStillExistsAfterRecovery);
                    if (failed.Undo == true && !IsRestoredFile(member))
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
                        CopyNewOrKeepLeftover(sourcePath, destinationPath);
                        var copied = _fileSystem.GetFileStat(destinationPath);
                        if (copied?.Length != member.Size) throw new JournalCodedException(JournalErrors.RetryVerifyFailed);
                    }
                    else
                    {
                        _fileSystem.Move(sourcePath, destinationPath);
                        if (_fileSystem.FileExists(sourcePath) || _fileSystem.GetFileStat(destinationPath)?.Length != member.Size)
                            throw new JournalCodedException(JournalErrors.RetryVerifyFailed);
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

    /// <summary>
    /// RV-C03: create-new copy for a retry. A destination that appeared after the pre-checks is never overwritten nor deleted
    /// (<see cref="Tr.CoreRecoveryDestinationExists"/>). When the copy itself throws, the destination is never deleted either
    /// (R01a): a throw gives no observation of the file this call created, so a size check cannot tell a partial copy from a foreign file
    /// that replaced it (the real file system raises no proof then, COPY-PARTIAL-01 option c); the leftover stays for Recovery to show
    /// as a Conflict and for the user to judge. The original failure propagates.
    /// </summary>
    private void CopyNewOrKeepLeftover(string source, string destination)
    {
        if (!_fileSystem.TryCopyNew(source, destination)) throw new IOException(Tr.CoreRecoveryDestinationExists);
    }

    /// <summary>R15/R18: the recycled file itself is at the member's original path (journaled size and write time, the proof Undo and Recovery use), not just some file.</summary>
    private bool IsRestoredFile(JournalGroupMember member) =>
        _fileSystem.GetFileStat(member.Source) is { } back && back.Length == member.Size && back.LastWriteUtc == member.LastWriteUtc;

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
            // R17: the pre-checks ran before Prepared was journaled; a different file may have taken the source path since. Same
            // identity re-proof the group retry does right before each mutation. The journaled fingerprint is the verified one
            // (`prepared` carries the stat the pre-check read), so a replacement is never moved/copied under the old operation.
            var sourceNow = _fileSystem.GetFileStat(failed.Source);
            if (sourceNow is null || sourceNow.Length != prepared.Size || sourceNow.LastWriteUtc != prepared.LastWriteUtc)
                throw new IOException(Tr.CoreRecoverySourceChanged);
            var destDir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(destDir))
            {
                _fileSystem.CreateDirectory(destDir);
            }

            if (failed.Type == FileOperationType.Copy)
            {
                CopyNewOrKeepLeftover(failed.Source, destination);
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
