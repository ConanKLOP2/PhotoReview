using System;
using System.IO;
using System.Threading.Tasks;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// Điều khiển các thao tác file (Move, Copy, Recycle) và hoàn tác (Undo).
/// Là ranh giới lệnh duy nhất cho file actions, giữ các invariant INV-3/4/5/6.
/// </summary>
public sealed class FileActionController
{
    private readonly ReviewCatalog _catalog;
    private readonly GenerationClock _clock;
    private readonly FileActionService? _fileActionService;
    private readonly UndoService? _undoService;
    private readonly IDialogService? _dialogService;
    private readonly IPreloadController? _preloadController;
    private readonly INaturalComparer _naturalComparer;
    private readonly AppSettings _settings;
    private readonly IFileActionSink _sink;

    public FileActionController(
        ReviewCatalog catalog,
        GenerationClock clock,
        FileActionService? fileActionService,
        UndoService? undoService,
        IDialogService? dialogService,
        IPreloadController? preloadController,
        INaturalComparer naturalComparer,
        AppSettings settings,
        IFileActionSink sink)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _fileActionService = fileActionService;
        _undoService = undoService;
        _dialogService = dialogService;
        _preloadController = preloadController;
        _naturalComparer = naturalComparer ?? throw new ArgumentNullException(nameof(naturalComparer));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public async Task RunActionAsync(int index, string? compareSelectedPath, string? currentPath)
    {
        if (_catalog.Count == 0) return;
        var actions = _settings.Actions;
        if (index < 0 || index >= actions.Count) return;

        var action = actions[index];
        if (!Enum.IsDefined(action.Operation))
        {
            _sink.SetStatusText($"Không thực hiện được {action.Name}: Operation không hợp lệ.");
            return;
        }

        if (action.Confirm && _dialogService is not null)
        {
            var ok = _dialogService.ShowConfirmation("Xác nhận action", $"Thực hiện action '{action.Name}' trên ảnh hiện tại?");
            if (!ok) return;
        }

        if (action.Operation == FileOperationType.Recycle)
        {
            await ExecuteFileActionCoreAsync(action.Name, FileOperationType.Recycle, null, compareSelectedPath, currentPath);
            return;
        }

        var source = compareSelectedPath ?? currentPath;
        if (string.IsNullOrEmpty(source)) return;

        if (string.IsNullOrWhiteSpace(action.Destination))
        {
            _sink.SetStatusText($"Không thực hiện được {action.Name}: Action chưa có thư mục đích.");
            return;
        }

        await ExecuteFileActionCoreAsync(action.Name, action.Operation, action.Destination, compareSelectedPath, currentPath);
    }

    public async Task RecycleAsync(string? compareSelectedPath, string? currentPath)
    {
        await ExecuteFileActionCoreAsync("Recycle", FileOperationType.Recycle, null, compareSelectedPath, currentPath);
    }

    private async Task ExecuteFileActionCoreAsync(string actionName, FileOperationType operation, string? destination, string? compareSelectedPath, string? currentPath)
    {
        if (_catalog.Count == 0) return;
        if (_fileActionService is null) return;

        // INV-4: Gate bận
        if (_fileActionService.IsBusy) return;

        var source = compareSelectedPath ?? currentPath;
        if (string.IsNullOrEmpty(source)) return;

        var sourceIndex = _catalog.IndexOf(source);
        _clock.StopForAction();
        _preloadController?.Cancel();
        var folderGen = _clock.CurrentFolder;

        var isRemove = operation is FileOperationType.Move or FileOperationType.Recycle;
        int nextIndex = -1;

        if (isRemove)
        {
            nextIndex = _catalog.Remove(source);
            _sink.OnCatalogChanged();

            // INV-3: Trình diễn ảnh tiếp theo TRƯỚC KHI thao tác file hoàn thành, không await
            if (nextIndex >= 0)
            {
                _ = _sink.PresentAsync(nextIndex);
            }
            else
            {
                _sink.SetStatusText("Đã xử lý hết ảnh trong folder.");
            }
        }

        try
        {
            var request = new FileActionRequest(source, operation, destination);
            var result = await _fileActionService.ExecuteAsync(request);

            // Stale Folder Guard: Nếu người dùng đã đổi thư mục trong khi I/O đang chạy, bỏ qua
            if (!_clock.IsFolderCurrent(folderGen))
            {
                return;
            }

            if (result.Succeeded)
            {
                _undoService?.Register(result);
                _sink.UpdateSessionPath(_catalog.Current?.Path ?? source);

                if (_catalog.Count == 0)
                {
                    _sink.SetStatusText(isRemove ? "Đã xử lý hết ảnh trong folder." : $"Đã thực hiện: {actionName}");
                }
                else if (operation == FileOperationType.Copy)
                {
                    _sink.SetStatusText($"Đã copy sang {Path.GetFileName(result.DestinationPath)}");
                }
            }
            else
            {
                // INV-5: Thất bại thì khôi phục lại ảnh nguồn vào danh mục
                if (isRemove && sourceIndex >= 0)
                {
                    _catalog.Restore(source, sourceIndex);
                }

                _sink.SetStatusText($"Không thực hiện được {actionName}: {result.Error}");
            }
        }
        finally
        {
            _sink.NotifyNavigationStateChanged();
        }
    }

    /// <summary>
    /// Deprecated: Use UndoLastAsync() instead. This method only handles Move operations.
    /// For unified undo semantics that handle both Move and Recycle, use UndoLastAsync().
    /// </summary>
    [Obsolete("Use UndoLastAsync() instead. This method is Move-only; use UndoLastAsync() for full undo semantics.")]
    public async Task UndoAsync(string? currentPath)
    {
        if (_undoService is null) return;

        var folderGen = _clock.CurrentFolder;
        var result = await _undoService.UndoMoveAsync();

        if (!result.Succeeded)
        {
            _sink.SetStatusText(result.ErrorMessage ?? "Không thể Undo.");
            return;
        }

        if (!_clock.IsFolderCurrent(folderGen)) return;

        if (!string.IsNullOrEmpty(result.Source))
        {
            _catalog.InsertSorted(result.Source, (a, b) => _naturalComparer.Compare(Path.GetFileName(a), Path.GetFileName(b)));
            _sink.OnCatalogChanged();
            var idx = _catalog.IndexOf(result.Source);
            if (idx >= 0)
            {
                await _sink.PresentAsync(idx);
            }

            _sink.UpdateSessionPath(result.Source);
        }

        _sink.NotifyNavigationStateChanged();
    }

    public async Task<UndoResult?> UndoLastAsync(string? currentPath)
    {
        if (_undoService is null) return null;

        var folderGen = _clock.CurrentFolder;
        var result = await _undoService.UndoLastAsync();

        if (!result.Succeeded)
        {
            _sink.SetStatusText(result.ErrorMessage ?? "Không có thao tác nào để hoàn tác.");
            return result;
        }

        if (!_clock.IsFolderCurrent(folderGen)) return result;

        if (result.Operation == FileOperationType.Move && !string.IsNullOrEmpty(result.Source))
        {
            _catalog.InsertSorted(result.Source, (a, b) => _naturalComparer.Compare(Path.GetFileName(a), Path.GetFileName(b)));
            _sink.OnCatalogChanged();
            var idx = _catalog.IndexOf(result.Source);
            if (idx >= 0)
            {
                await _sink.PresentAsync(idx);
            }

            _sink.UpdateSessionPath(result.Source);
        }
        else if (result.Operation == FileOperationType.Recycle && !string.IsNullOrEmpty(result.Source))
        {
            _sink.UpdateSessionPath(result.Source);
        }

        _sink.NotifyNavigationStateChanged();
        return result;
    }
}
