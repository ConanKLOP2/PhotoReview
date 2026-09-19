using System.IO;
using PhotoReview.Core.Abstractions;
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

    private readonly Stack<(string Source, string Destination)> _moveHistory = new();
    private UndoActionRecord? _lastUndoAction;
    private int _internalInProgress;

    private sealed record UndoActionRecord(FileOperationType Operation, string Source, string? Destination, long Size, DateTime LastWriteUtc);

    public UndoService(
        OperationJournal journal,
        IFileSystem fileSystem,
        IRecycleBin recycleBin,
        FileActionService? fileActionService = null,
        Func<string, string, Task>? moveOverride = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _recycleBin = recycleBin ?? throw new ArgumentNullException(nameof(recycleBin));
        _fileActionService = fileActionService;
        _moveOverride = moveOverride;
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
        var committedMoves = _journal.ReadCommittedMoves();
        foreach (var entry in committedMoves)
        {
            if (string.IsNullOrEmpty(entry.Destination)) continue;

            if (_fileSystem.FileExists(entry.Destination) && !_fileSystem.FileExists(entry.Source))
            {
                _moveHistory.Push((entry.Source, entry.Destination));
            }
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
            _lastUndoAction = new UndoActionRecord(FileOperationType.Move, result.Source, result.DestinationPath, result.Size, result.LastWriteUtc);
        }
        else if (result.Operation == FileOperationType.Recycle)
        {
            _lastUndoAction = new UndoActionRecord(FileOperationType.Recycle, result.Source, null, result.Size, result.LastWriteUtc);
        }
    }

    /// <summary>
    /// Hoàn tác thao tác Move gần nhất trong ngăn xếp.
    /// </summary>
    public async Task<UndoResult> UndoMoveAsync()
    {
        if (!TryBegin())
        {
            return new UndoResult(false, FileOperationType.Move, string.Empty, null, "Hệ thống đang bận thao tác khác.", Rejected: true);
        }

        try
        {
            if (_moveHistory.Count == 0)
            {
                return new UndoResult(false, FileOperationType.Move, string.Empty, null, "Không có Move nào để hoàn tác.");
            }

            var move = _moveHistory.Pop();
            try
            {
                if (!_fileSystem.FileExists(move.Destination) || _fileSystem.FileExists(move.Source))
                {
                    throw new IOException("Nguồn hoặc đích đã thay đổi.");
                }

                var destinationStat = _fileSystem.GetFileStat(move.Destination);
                var committed = _journal.ReadCommittedMoves()
                    .LastOrDefault(x => string.Equals(x.Destination, move.Destination, StringComparison.OrdinalIgnoreCase));

                if (committed is null ||
                    destinationStat is null ||
                    destinationStat.Length != committed.Size ||
                    destinationStat.LastWriteUtc != committed.LastWriteUtc)
                {
                    throw new IOException("File đích đã thay đổi sau Move; không tự động Undo.");
                }

                if (_moveOverride is not null)
                {
                    await _moveOverride(move.Destination, move.Source).ConfigureAwait(false);
                }
                else
                {
                    await Task.Run(() => _fileSystem.Move(move.Destination, move.Source));
                }
                _lastUndoAction = null;
                return new UndoResult(true, FileOperationType.Move, move.Source, move.Destination, null);
            }
            catch (Exception ex)
            {
                _moveHistory.Push(move);
                return new UndoResult(false, FileOperationType.Move, move.Source, move.Destination, $"Không thể Undo: {ex.Message}");
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
            return new UndoResult(false, null, string.Empty, null, "Không có Move/Delete vừa thực hiện để hoàn tác.");
        }

        var action = _lastUndoAction;
        if (action.Operation == FileOperationType.Move)
        {
            return await UndoMoveAsync();
        }

        if (action.Operation == FileOperationType.Recycle)
        {
            if (!TryBegin())
            {
                return new UndoResult(false, FileOperationType.Recycle, action.Source, null, "Hệ thống đang bận thao tác khác.", Rejected: true);
            }

            try
            {
                var restored = await Task.Run(() => _recycleBin.TryRestore(action.Source, action.Size, action.LastWriteUtc));
                if (!restored)
                {
                    return new UndoResult(false, FileOperationType.Recycle, action.Source, null, $"Không thể khôi phục Recycle Bin: {Path.GetFileName(action.Source)}");
                }

                _lastUndoAction = null;
                return new UndoResult(true, FileOperationType.Recycle, action.Source, null, null);
            }
            finally
            {
                End();
            }
        }

        return new UndoResult(false, action.Operation, action.Source, null, "Không hỗ trợ hoàn tác thao tác này.");
    }
}