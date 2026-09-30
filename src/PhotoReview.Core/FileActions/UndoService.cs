using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// Dịch vụ quản lý lịch sử hoàn tác các thao tác tệp tin (Move, Recycle),
/// đảm bảo kiểm tra cổng bận (INV-4), so khớp fingerprint an toàn và sửa lỗi P12.
/// </summary>
public sealed class UndoService
{
    private readonly OperationJournal _journal;
    private readonly IFileSystem _fileSystem;
    private readonly IRecycleBin _recycleBin;
    private readonly FileActionService? _fileActionService;
    private readonly Func<string, string, Task>? _moveOverride;
    private readonly IClock _clock;

    private readonly Stack<(string Source, string Destination)> _moveHistory = new();
    // Keep the committed fingerprint next to the in-memory history so Undo does not
    // rescan the journal for every action; entries registered during this process
    // (the only source of Undo history since P03 - Undo is session-only) carry
    // their complete identity here.
    private readonly Dictionary<string, (long Size, DateTime LastWriteUtc)> _moveFingerprints =
        new(StringComparer.OrdinalIgnoreCase);
    private UndoActionRecord? _lastUndoAction;
    private int _internalInProgress;

    // A group undo that restored some members (by its own mutations) and then failed stays as _lastUndoAction so Ctrl+Z can
    // retry it. A retry that finds nothing pending is then an idempotent success ("everything is back") and resolves the
    // earlier Failed Recovery records; without this memory it is the "already handled" case (the user put the files back).
    private PartialGroupUndo? _partialGroupUndo;

    private sealed record PartialGroupUndo(object Action, IReadOnlyList<JournalEntry> FailedEntries);

    private sealed record UndoActionRecord(FileOperationType Operation, string Source, string? Destination, long Size, DateTime LastWriteUtc, bool Permanent = false,
        IReadOnlyList<JournalGroupMember>? GroupMembers = null, JournalEntry? FailedEntry = null);

    public UndoService(
        OperationJournal journal,
        IFileSystem fileSystem,
        IRecycleBin recycleBin,
        FileActionService? fileActionService = null,
        Func<string, string, Task>? moveOverride = null,
        IClock? clock = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _recycleBin = recycleBin ?? throw new ArgumentNullException(nameof(recycleBin));
        _fileActionService = fileActionService;
        _moveOverride = moveOverride;
        _clock = clock ?? new SystemClock();
    }

    /// <summary>Số lượng thao tác Move hiện có trong lịch sử hoàn tác.</summary>
    public int MoveHistoryCount => _moveHistory.Count;

    /// <summary>Test seam (Q-R small-findings #2): number of cached move fingerprints, to assert this map
    /// does not grow unbounded across register+undo cycles.</summary>
    internal int MoveFingerprintCount => _moveFingerprints.Count;

    /// <summary>Truy cập ngăn xếp lịch sử thao tác Move.</summary>
    public Stack<(string Source, string Destination)> MoveHistory => _moveHistory;

    /// <summary>Thao tác vừa hoàn thành gần nhất.</summary>
    public object? LastUndoAction => _lastUndoAction;

    /// <summary>Cho biết có thao tác Move nào để hoàn tác hay không.</summary>
    public bool CanUndoMove => _moveHistory.Count > 0;

    /// <summary>Cho biết có thao tác gần nhất nào (Move hoặc Recycle) để hoàn tác hay không.</summary>
    public bool HasLastAction => _lastUndoAction is not null;

    /// <summary>Cho biết dịch vụ có đang bận xử lý thao tác hay không.</summary>
    public bool IsBusy => _fileActionService?.IsBusy ?? (Volatile.Read(ref _internalInProgress) != 0);

    /// <summary>
    /// Thử khóa cổng thao tác tệp tin (INV-4).
    /// </summary>
    public bool TryBegin()
    {
        if (_fileActionService is not null)
        {
            return _fileActionService.TryBegin();
        }
        return Interlocked.CompareExchange(ref _internalInProgress, 1, 0) == 0;
    }

    /// <summary>
    /// Mở khóa cổng thao tác tệp tin sau khi hoàn thành.
    /// </summary>
    public void End()
    {
        if (_fileActionService is not null)
        {
            _fileActionService.End();
        }
        else
        {
            Volatile.Write(ref _internalInProgress, 0);
        }
    }

    /// <summary>
    /// Đăng ký kết quả thao tác tệp tin thành công vào ngăn xếp hoàn tác.
    /// </summary>
    public void Register(FileActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Succeeded || result.Rejected) return;

        if (result.Operation == FileOperationType.Move && !string.IsNullOrEmpty(result.DestinationPath))
        {
            _moveHistory.Push((result.Source, result.DestinationPath));
            _moveFingerprints[result.DestinationPath] = (result.Size, result.LastWriteUtc);
            _lastUndoAction = new UndoActionRecord(FileOperationType.Move, result.Source, result.DestinationPath, result.Size, result.LastWriteUtc);
        }
        else if (result.Operation == FileOperationType.Recycle)
        {
            _lastUndoAction = new UndoActionRecord(FileOperationType.Recycle, result.Source, null, result.Size, result.LastWriteUtc, result.PermanentlyDeleted);
        }
    }

    public void RegisterGroup(CaptureGroupActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Rejected) return;
        IReadOnlyList<JournalGroupMember> members;
        if (result.Succeeded)
        {
            if (result.Entry?.GroupMembers is not { Count: > 0 } entryMembers) return;
            members = entryMembers;
        }
        else if (result.Operation == FileOperationType.Recycle)
        {
            // A group Delete that failed part-way (e.g. the JPEG reached the Recycle Bin, the RAW did not): the members that
            // already went there still deserve a Ctrl+Z. Only completed members; a failed one is still on disk.
            members = result.Members.Where(member => member.Completed).Select(member => member.Member).ToArray();
            if (members.Count == 0) return;
        }
        else return;
        if (result.Operation is FileOperationType.Move or FileOperationType.Recycle)
            // Permanence is per member: only a capture whose EVERY member was deleted permanently has nothing to restore.
            // One permanent member (e.g. an XMP) must not disable undo of the JPEG/RAW that reached the Recycle Bin.
            _lastUndoAction = new UndoActionRecord(result.Operation, result.Entry?.Source ?? members[0].Source, result.Entry?.Destination ?? members[0].Destination,
                result.Entry?.Size ?? members[0].Size, result.Entry?.LastWriteUtc ?? members[0].LastWriteUtc,
                result.Operation == FileOperationType.Recycle && members.All(member => member.Permanent), members,
                // The Failed Recovery line of a part-way group Delete: a successful undo must stop it offering to delete again.
                FailedEntry: !result.Succeeded && result.Entry is { State: JournalState.Failed, GroupMembers: { Count: > 0 } } failedEntry ? failedEntry : null);
    }

    /// <summary>
    /// Hoàn tác thao tác Move gần nhất trong ngăn xếp.
    /// </summary>
    public async Task<UndoResult> UndoMoveAsync()
    {
        if (!TryBegin())
        {
            return new UndoResult(false, FileOperationType.Move, string.Empty, null, Tr.CoreUndoBusy, Rejected: true);
        }

        try
        {
            if (_moveHistory.Count == 0)
            {
                return new UndoResult(false, FileOperationType.Move, string.Empty, null, Tr.CoreUndoNoMoveToUndo);
            }

            var move = _moveHistory.Pop();
            // The destination is gone (deleted/renamed outside the app): this entry can never be undone. Report it
            // once and drop it, otherwise it is pushed back and every later Ctrl+Z hits the same dead entry.
            var permanentlyBroken = false;
            try
            {
                if (!_fileSystem.FileExists(move.Destination))
                {
                    permanentlyBroken = true;
                    throw new IOException(Tr.CoreUndoSourceOrDestinationChanged);
                }

                if (_fileSystem.FileExists(move.Source))
                {
                    throw new IOException(Tr.CoreUndoSourceOrDestinationChanged);
                }

                var destinationStat = _fileSystem.GetFileStat(move.Destination);
                if (!_moveFingerprints.TryGetValue(move.Destination, out var fingerprint))
                {
                    // Compatibility fallback for callers that populated the
                    // public history stack directly. The normal Register path
                    // never needs to scan the journal here.
                    var committed = _journal.ReadCommittedMoves()
                        .LastOrDefault(x => string.Equals(x.Destination, move.Destination, StringComparison.OrdinalIgnoreCase));
                    if (committed is null)
                        throw new IOException(Tr.CoreUndoFingerprintMissing);
                    fingerprint = (committed.Size, committed.LastWriteUtc);
                    _moveFingerprints[move.Destination] = fingerprint;
                }

                if (destinationStat is null ||
                    destinationStat.Length != fingerprint.Size ||
                    destinationStat.LastWriteUtc != fingerprint.LastWriteUtc)
                {
                    throw new IOException(Tr.CoreUndoDestinationChangedAfterMove);
                }

                // Review r7 (INV-6): the undo is itself a Move (destination -> source), journaled Prepared -> Committed/
                // Failed like any other, so a crash mid-undo leaves a pending entry for startup reconcile / Recovery.
                using var tx = new JournalTransaction(_journal, _clock, new JournalEntry(
                    Guid.NewGuid().ToString("N"),
                    FileOperationType.Move,
                    JournalState.Prepared,
                    move.Destination,
                    move.Source,
                    fingerprint.Size,
                    fingerprint.LastWriteUtc,
                    _clock.UtcNow,
                    Undo: true));
                try
                {
                    await tx.BeginAsync().ConfigureAwait(false);
                    if (_moveOverride is not null)
                    {
                        await _moveOverride(move.Destination, move.Source).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Run(() => _fileSystem.Move(move.Destination, move.Source)).ConfigureAwait(false);
                    }
                    tx.VerifyMoved(_fileSystem, move.Destination, move.Source, JournalErrors.VerifySizeChanged);
                }
                catch (Exception undoFailure)
                {
                    _ = tx.Fail(undoFailure, out _);
                    throw;
                }
                // A failed Committed append does not undo the completed move (same contract as FileActionService).
                _ = tx.Commit(out _);
                // The move is undone; its fingerprint no longer describes anything at Destination (the file is
                // back at Source, or gone from Destination entirely). Drop it so the map does not grow unbounded
                // across register+undo cycles.
                _moveFingerprints.Remove(move.Destination);
                _lastUndoAction = null;
                return new UndoResult(true, FileOperationType.Move, move.Source, move.Destination, null);
            }
            catch (Exception ex)
            {
                if (permanentlyBroken)
                {
                    _moveFingerprints.Remove(move.Destination);
                    if (_lastUndoAction is { Operation: FileOperationType.Move } last
                        && string.Equals(last.Destination, move.Destination, StringComparison.OrdinalIgnoreCase))
                    {
                        _lastUndoAction = null;
                    }
                }
                else
                {
                    _moveHistory.Push(move);
                }

                return new UndoResult(false, FileOperationType.Move, move.Source, move.Destination, Tr.CoreUndoFailed(ex.Message));
            }
        }
        finally
        {
            End();
        }
    }

    /// <summary>
    /// Hoàn tác thao tác vừa thực hiện gần nhất (Move hoặc Recycle).
    /// </summary>
    public async Task<UndoResult> UndoLastAsync()
    {
        if (_lastUndoAction is null)
        {
            return new UndoResult(false, null, string.Empty, null, Tr.CoreUndoNothingToUndo);
        }

        var action = _lastUndoAction;
        if (action.Operation == FileOperationType.Move)
        {
            if (action.GroupMembers is { Count: > 0 } groupMembers)
                return await UndoGroupMoveAsync(action, groupMembers).ConfigureAwait(false);
            return await UndoMoveAsync().ConfigureAwait(false);
        }

        if (action.Operation == FileOperationType.Recycle)
        {
            if (action.GroupMembers is { Count: > 0 } recycleMembers)
                return await UndoGroupRecycleAsync(action, recycleMembers).ConfigureAwait(false);
            // Q-R8: deleted permanently on a drive without a Recycle Bin: there is nothing to restore. Say so instead of
            // searching the Recycle Bin (it could even match an unrelated item with the same path/size/time).
            if (action.Permanent)
            {
                _lastUndoAction = null;
                return new UndoResult(false, FileOperationType.Recycle, action.Source, null, Tr.CoreUndoPermanentlyDeleted(Path.GetFileName(action.Source)));
            }

            if (!TryBegin())
            {
                return new UndoResult(false, FileOperationType.Recycle, action.Source, null, Tr.CoreUndoBusy, Rejected: true);
            }

            try
            {
                // The shell Restore verb would replace (or prompt about) a newer file that now sits at the original path.
                if (_fileSystem.FileExists(action.Source))
                {
                    return new UndoResult(false, FileOperationType.Recycle, action.Source, null, Tr.CoreUndoRecycleTargetExists(Path.GetFileName(action.Source)));
                }

                var restored = await Task.Run(() => _recycleBin.TryRestore(action.Source, action.Size, action.LastWriteUtc)).ConfigureAwait(false);
                if (!restored)
                {
                    return new UndoResult(false, FileOperationType.Recycle, action.Source, null, Tr.CoreUndoRecycleRestoreFailed(Path.GetFileName(action.Source)));
                }

                _lastUndoAction = null;
                return new UndoResult(true, FileOperationType.Recycle, action.Source, null, null);
            }
            finally
            {
                End();
            }
        }

        return new UndoResult(false, action.Operation, action.Source, null, Tr.CoreUndoUnsupported);
    }

    private async Task<UndoResult> UndoGroupMoveAsync(UndoActionRecord action, IReadOnlyList<JournalGroupMember> members)
    {
        if (!TryBegin()) return new UndoResult(false, FileOperationType.Move, action.Source, action.Destination, Tr.CoreUndoBusy, Rejected: true);
        JournalTransaction? tx = null;
        // Original paths that are back in place (restored by this call or earlier), reported even when a later member fails
        // so the catalog can show them without a folder reload.
        var restored = new List<string>();
        var restoredByThisUndo = 0;
        try
        {
            var undoMembers = members.Select(member => new JournalGroupMember(
                member.Destination ?? throw new IOException(Tr.CoreUndoSourceOrDestinationChanged),
                member.Source, member.Size, member.LastWriteUtc)).ToArray();
            var pending = new List<JournalGroupMember>();
            foreach (var member in undoMembers)
            {
                var sourceStat = _fileSystem.GetFileStat(member.Source);
                var destinationStat = _fileSystem.GetFileStat(member.Destination!);
                if (sourceStat is not null && destinationStat is not null)
                    throw new IOException(Tr.CoreUndoSourceOrDestinationChanged);
                // Already back at the original path: only when it is the moved file itself (size AND write time), not any
                // file that happens to have the same length.
                if (sourceStat is null && destinationStat is not null
                    && destinationStat.Length == member.Size && destinationStat.LastWriteUtc == member.LastWriteUtc)
                {
                    restored.Add(member.Destination!);
                    continue;
                }
                if (sourceStat is null || destinationStat is not null
                    || sourceStat.Length != member.Size || sourceStat.LastWriteUtc != member.LastWriteUtc)
                    throw new IOException(Tr.CoreUndoDestinationChangedAfterMove);
                pending.Add(member);
            }
            if (pending.Count == 0)
            {
                // Everything is already back: this entry can never do anything, so drop it (like a broken single Move)
                // instead of failing the same way on every later Ctrl+Z.
                var resumed = ResolvePartialUndo(action);
                DropGroupUndo(members);
                if (resumed)
                    return new UndoResult(true, FileOperationType.Move, action.Source, action.Destination, null,
                        RestoredPaths: members.Select(member => member.Source).ToArray());
                throw new IOException(Tr.CoreRecoveryAlreadyHandled);
            }
            var prepared = new JournalEntry(Guid.NewGuid().ToString("N"), FileOperationType.Move, JournalState.Prepared,
                undoMembers[0].Source, undoMembers[0].Destination, undoMembers[0].Size, undoMembers[0].LastWriteUtc, _clock.UtcNow,
                Undo: true, GroupId: Guid.NewGuid().ToString("N"), GroupMembers: undoMembers);
            tx = new JournalTransaction(_journal, _clock, prepared);
            await tx.BeginAsync().ConfigureAwait(false);
            // Whole loop on the pool (directory creation, moves and verification stats), same order as before.
            await Task.Run(async () =>
            {
                foreach (var member in pending)
                {
                    var folder = Path.GetDirectoryName(member.Destination!);
                    if (!string.IsNullOrEmpty(folder)) _fileSystem.CreateDirectory(folder);
                    if (_moveOverride is not null) await _moveOverride(member.Source, member.Destination!).ConfigureAwait(false);
                    else _fileSystem.Move(member.Source, member.Destination!);
                    if (_fileSystem.FileExists(member.Source) || _fileSystem.GetFileStat(member.Destination!)?.Length != member.Size)
                        throw new IOException(JournalErrors.VerifySizeChanged);
                    restored.Add(member.Destination!);
                    restoredByThisUndo++;
                }
            }).ConfigureAwait(false);
            tx.MarkMutationCompleted();
            _ = tx.Commit(out _);
            // A retry that really restored the remaining members completes the earlier partial undo: close its Failed lines.
            _ = ResolvePartialUndo(action);
            DropGroupUndo(members);
            return new UndoResult(true, FileOperationType.Move, action.Source, action.Destination, null,
                RestoredPaths: members.Select(member => member.Source).ToArray());
        }
        catch (Exception ex)
        {
            RememberPartialUndo(action, restoredByThisUndo, tx?.Fail(ex, out _));
            return new UndoResult(false, FileOperationType.Move, action.Source, action.Destination, Tr.CoreUndoFailed(ex.Message),
                RestoredPaths: restored.Count > 0 ? restored.ToArray() : null);
        }
        finally
        {
            tx?.Dispose();
            End();
        }
    }

    private void RememberPartialUndo(UndoActionRecord action, int restoredByThisUndo, JournalEntry? failedEntry)
    {
        var earlier = _partialGroupUndo is { } partial && ReferenceEquals(partial.Action, action) ? partial : null;
        if (restoredByThisUndo == 0 && earlier is null) return;
        var entries = new List<JournalEntry>(earlier?.FailedEntries ?? []);
        if (failedEntry is not null) entries.Add(failedEntry);
        _partialGroupUndo = new PartialGroupUndo(action, entries);
    }

    /// <summary>
    /// True when <paramref name="action"/> is a retry of an undo that already restored members itself: its earlier Failed
    /// records are closed with Committed (best effort; skipped when another writer touched them) and the memory is dropped.
    /// </summary>
    private bool ResolvePartialUndo(UndoActionRecord action)
    {
        var partial = _partialGroupUndo;
        _partialGroupUndo = null;
        if (partial is null || !ReferenceEquals(partial.Action, action)) return false;
        foreach (var failed in partial.FailedEntries)
        {
            try
            {
                var committed = failed with { State = JournalState.Committed, TimestampUtc = _clock.UtcNow, Error = null, ErrorCode = null };
                _ = _journal.AppendIfUnchangedSince(failed, [], committed);
            }
            catch (Exception ex) when (IsNonCritical(ex))
            {
                // The undo itself is complete; a journal that cannot be written only leaves the old Recovery item to be dismissed.
            }
        }
        return true;
    }

    /// <summary>
    /// A group Delete that failed part-way left a Failed line that Recovery offers to retry (recycle the members still on
    /// disk). Once Ctrl+Z restored members, retrying would delete them again, so they leave that line: it is replaced (same
    /// Id, appended only while it is still the latest line) by a Failed line with the members that were NOT restored, or by
    /// a terminal Dismissed line when nothing is left. Best effort like <see cref="ResolvePartialUndo"/>.
    /// </summary>
    private void SettleFailedDeleteLine(UndoActionRecord action, IReadOnlyCollection<string> restored)
    {
        if (action.FailedEntry is not { GroupMembers: { Count: > 0 } original } failed) return;
        try
        {
            var remaining = original.Where(member => !restored.Contains(member.Source, StringComparer.OrdinalIgnoreCase)).ToArray();
            var replacement = remaining.Length == 0
                ? failed with { State = JournalState.Dismissed, TimestampUtc = _clock.UtcNow, Error = null, ErrorCode = null }
                : failed with
                {
                    TimestampUtc = _clock.UtcNow,
                    Source = remaining[0].Source,
                    Destination = remaining[0].Destination,
                    Size = remaining[0].Size,
                    LastWriteUtc = remaining[0].LastWriteUtc,
                    GroupMembers = remaining,
                };
            _ = _journal.AppendIfUnchangedSince(failed, [], replacement);
        }
        catch (Exception ex) when (IsNonCritical(ex))
        {
            // The undo itself is complete; a journal that cannot be written (or read) only leaves the old Recovery item to be
            // dismissed. Core has no logger, so the failure is deliberately swallowed here, never turned into a failed undo.
        }
    }

    /// <summary>Best-effort journal settling must never fail a finished undo: anything but out-of-memory/cancellation is swallowed.</summary>
    private static bool IsNonCritical(Exception ex) => ex is not (OutOfMemoryException or OperationCanceledException);

    private void DropGroupUndo(IReadOnlyList<JournalGroupMember> members)
    {
        foreach (var member in members)
        {
            if (member.Destination is not null) _moveFingerprints.Remove(member.Destination);
        }
        _lastUndoAction = null;
    }

    private async Task<UndoResult> UndoGroupRecycleAsync(UndoActionRecord action, IReadOnlyList<JournalGroupMember> members)
    {
        if (action.Permanent)
        {
            _lastUndoAction = null;
            return new UndoResult(false, FileOperationType.Recycle, action.Source, null,
                Tr.CoreUndoPermanentlyDeleted(Path.GetFileName(action.Source)));
        }
        if (!TryBegin()) return new UndoResult(false, FileOperationType.Recycle, action.Source, null, Tr.CoreUndoBusy, Rejected: true);
        JournalTransaction? tx = null;
        // Original paths that are back (restored by this call or earlier); reported even when a later member fails.
        var restored = new List<string>();
        var restoredByThisUndo = 0;
        try
        {
            var pending = new List<JournalGroupMember>();
            var unrecoverable = new List<JournalGroupMember>();
            foreach (var member in members)
            {
                // Per member: a member deleted permanently (e.g. the XMP on another kind of drive) cannot be restored, but
                // the members that did reach the Recycle Bin still can.
                if (member.Permanent)
                {
                    unrecoverable.Add(member);
                    continue;
                }
                var existing = _fileSystem.GetFileStat(member.Source);
                if (existing is null)
                {
                    pending.Add(member);
                    continue;
                }
                // Something is at the original path: the shell Restore would replace or prompt about it. It only counts as
                // "already restored" when it is the recycled file itself; an unrelated newer file must not hide that the
                // real one is still in the bin.
                if (existing.Length != member.Size || existing.LastWriteUtc != member.LastWriteUtc)
                    throw new IOException(Tr.CoreUndoRecycleTargetExists(Path.GetFileName(member.Source)));
                restored.Add(member.Source);
            }
            if (pending.Count == 0)
            {
                _lastUndoAction = null;
                if (ResolvePartialUndo(action))
                {
                    SettleFailedDeleteLine(action, restored);
                    return new UndoResult(true, FileOperationType.Recycle, action.Source, null, UnrecoverableNote(members, restored, unrecoverable),
                        RestoredPaths: RestoredInOrder(members, restored));
                }
                // Members the user put back by hand still must leave the Failed Delete line, or Retry would recycle them again.
                SettleFailedDeleteLine(action, restored);
                throw new IOException(Tr.CoreRecoveryAlreadyHandled);
            }
            var undoMembers = members.Where(member => !member.Permanent).Select(member => member with { }).ToArray();
            var prepared = new JournalEntry(Guid.NewGuid().ToString("N"), FileOperationType.Recycle, JournalState.Prepared,
                undoMembers[0].Source, null, undoMembers[0].Size, undoMembers[0].LastWriteUtc, _clock.UtcNow,
                Undo: true, GroupId: Guid.NewGuid().ToString("N"), GroupMembers: undoMembers);
            tx = new JournalTransaction(_journal, _clock, prepared);
            await tx.BeginAsync().ConfigureAwait(false);
            // On the pool: BeginAsync completes synchronously in Fast journal mode, so without this the (slow, bin-enumerating)
            // restore would run on the caller's (UI) thread. Awaited, so `restored` is safely read after it, also on failure.
            await Task.Run(() =>
            {
                foreach (var member in pending)
                {
                    if (!_recycleBin.TryRestore(member.Source, member.Size, member.LastWriteUtc))
                        throw new IOException(Tr.CoreUndoRecycleRestoreFailed(Path.GetFileName(member.Source)));
                    if (!_fileSystem.FileExists(member.Source)) throw new IOException(Tr.CoreUndoRecycleRestoreFailed(Path.GetFileName(member.Source)));
                    restored.Add(member.Source);
                    restoredByThisUndo++;
                }
            }).ConfigureAwait(false);
            tx.MarkMutationCompleted();
            _ = tx.Commit(out _);
            _ = ResolvePartialUndo(action);
            SettleFailedDeleteLine(action, restored);
            _lastUndoAction = null;
            // Partly restorable capture: success for what came back, plus a message naming what cannot (a warning, not an error).
            return new UndoResult(true, FileOperationType.Recycle, action.Source, null, UnrecoverableNote(members, restored, unrecoverable),
                RestoredPaths: RestoredInOrder(members, restored));
        }
        catch (Exception ex)
        {
            RememberPartialUndo(action, restoredByThisUndo, tx?.Fail(ex, out _));
            // Members that did come back (now or earlier) must not stay on the Failed Delete line a fresh Recovery would retry.
            if (restored.Count > 0) SettleFailedDeleteLine(action, restored);
            return new UndoResult(false, FileOperationType.Recycle, action.Source, null, Tr.CoreUndoFailed(ex.Message),
                RestoredPaths: restored.Count > 0 ? restored.ToArray() : null);
        }
        finally
        {
            tx?.Dispose();
            End();
        }
    }

    private static string[] RestoredInOrder(IReadOnlyList<JournalGroupMember> members, IReadOnlyCollection<string> restored) =>
        members.Where(member => restored.Contains(member.Source, StringComparer.OrdinalIgnoreCase)).Select(member => member.Source).ToArray();

    private static string? UnrecoverableNote(IReadOnlyList<JournalGroupMember> members, IReadOnlyCollection<string> restored,
        List<JournalGroupMember> unrecoverable) =>
        unrecoverable.Count > 0
            ? Tr.CoreUndoGroupPartiallyRestored(RestoredInOrder(members, restored).Length, members.Count,
                string.Join(", ", unrecoverable.Select(member => Path.GetFileName(member.Source))))
            : null;
}
