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
    // Keep the committed fingerprint next to the in-memory history.  Undo must
    // not rescan the journal for every action; the bounded startup tail is only
    // a history bootstrap, while entries registered during this process carry
    // their complete identity here.
    private readonly Dictionary<string, (long Size, DateTime LastWriteUtc)> _moveFingerprints =
        new(StringComparer.OrdinalIgnoreCase);
    private UndoActionRecord? _lastUndoAction;
    private int _internalInProgress;

    private sealed record UndoActionRecord(FileOperationType Operation, string Source, string? Destination, long Size, DateTime LastWriteUtc, bool Permanent = false);

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
    /// Nạp lịch sử các thao tác Move đã commit từ nhật ký (OperationJournal) khi khởi động.
    /// Chỉ nạp những thao tác mà file đích còn tồn tại và file nguồn chưa tồn tại.
    /// </summary>
    public void LoadFromJournal()
    {
        _moveHistory.Clear();
        _moveFingerprints.Clear();
        SeedHistory(ReadStartupHistory());
    }

    /// <summary>
    /// Startup, any thread (no in-memory state is touched): the recent committed Moves whose file is still at the
    /// destination and not back at the source, oldest first. Pass the result to <see cref="SeedHistory"/> on the UI thread.
    /// </summary>
    public IReadOnlyList<JournalEntry> ReadStartupHistory()
    {
        var history = new List<JournalEntry>();
        foreach (var entry in _journal.ReadCommittedMoves())
        {
            if (string.IsNullOrEmpty(entry.Destination)) continue;
            // An undo is journaled as a reverse Move; loading it would turn Ctrl+Z after a restart into a redo.
            if (entry.Undo == true) continue;

            if (_fileSystem.FileExists(entry.Destination) && !_fileSystem.FileExists(entry.Source))
            {
                // Move, undo, move again leaves two Committed records for one destination: keep only the newest, or the
                // second Ctrl+Z would hit a stale entry (file no longer there) and block every older undo.
                history.RemoveAll(older => string.Equals(older.Destination, entry.Destination, StringComparison.OrdinalIgnoreCase));
                history.Add(entry);
            }
        }
        return history;
    }

    /// <summary>
    /// Adds journal history (oldest first, from <see cref="ReadStartupHistory"/>) BELOW the moves already registered
    /// in this session, which are newer; a Move registered meanwhile is not added twice. Same thread as
    /// <see cref="Register"/> (UI).
    /// </summary>
    public void SeedHistory(IReadOnlyList<JournalEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var sessionMoves = _moveHistory.ToArray(); // newest first
        var sessionDestinations = new HashSet<string>(sessionMoves.Select(move => move.Destination), StringComparer.OrdinalIgnoreCase);
        _moveHistory.Clear();
        foreach (var entry in history)
        {
            if (entry.Destination is null || sessionDestinations.Contains(entry.Destination)) continue;
            _moveHistory.Push((entry.Source, entry.Destination));
            _moveFingerprints[entry.Destination] = (entry.Size, entry.LastWriteUtc);
        }
        for (var i = sessionMoves.Length - 1; i >= 0; i--) _moveHistory.Push(sessionMoves[i]);
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
            try
            {
                if (!_fileSystem.FileExists(move.Destination) || _fileSystem.FileExists(move.Source))
                {
                    throw new IOException(Tr.CoreUndoSourceOrDestinationChanged);
                }

                var destinationStat = _fileSystem.GetFileStat(move.Destination);
                if (!_moveFingerprints.TryGetValue(move.Destination, out var fingerprint))
                {
                    // Compatibility fallback for callers that populated the
                    // public history stack directly. Normal Register/Load paths
                    // never need to scan the journal here.
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
                _lastUndoAction = null;
                return new UndoResult(true, FileOperationType.Move, move.Source, move.Destination, null);
            }
            catch (Exception ex)
            {
                _moveHistory.Push(move);
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
            return await UndoMoveAsync().ConfigureAwait(false);
        }

        if (action.Operation == FileOperationType.Recycle)
        {
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
}
