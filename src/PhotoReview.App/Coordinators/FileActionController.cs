using System;
using System.IO;
using System.Threading.Tasks;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
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
    private readonly Func<AppSettings> _getSettings;
    private readonly IFileActionSink _sink;

    public FileActionController(
        ReviewCatalog catalog,
        GenerationClock clock,
        FileActionService? fileActionService,
        UndoService? undoService,
        IDialogService? dialogService,
        IPreloadController? preloadController,
        INaturalComparer naturalComparer,
        Func<AppSettings> getSettings,
        IFileActionSink sink)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _fileActionService = fileActionService;
        _undoService = undoService;
        _dialogService = dialogService;
        _preloadController = preloadController;
        _naturalComparer = naturalComparer ?? throw new ArgumentNullException(nameof(naturalComparer));
        _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public async Task RunActionAsync(int index, string? compareSelectedPath, string? currentPath)
    {
        if (_catalog.Count == 0) return;
        // Read at use time: Settings > Save replaces the AppSettings instance (R2-F-03).
        var actions = _getSettings().Actions;
        if (index < 0 || index >= actions.Count) return;

        var action = actions[index];
        if (!Enum.IsDefined(action.Operation))
        {
            _sink.SetStatusText(StatusFormatter.ActionInvalidOperation(action.Name));
            return;
        }

        // Q-R8: a permanent delete gets its own explicit confirmation (in the core step), which replaces the generic one.
        var permanentPrompt = action.Operation == FileOperationType.Recycle && WillAskPermanentDelete(compareSelectedPath ?? currentPath);
        if (action.Confirm && _dialogService is not null && !permanentPrompt)
        {
            // R7-4: the dialog runs a nested dispatcher loop (a forwarded open can switch the folder meanwhile),
            // so re-check state afterwards like the permanent-delete prompt does.
            var folderBeforeDialog = _clock.CurrentFolder;
            var target = compareSelectedPath ?? currentPath;
            var ok = _dialogService.ShowConfirmation(Tr.DialogConfirmActionTitle, Tr.DialogConfirmActionMessage(action.Name));
            if (!ok) return;
            if (_clock.CurrentFolder != folderBeforeDialog || _fileActionService?.IsBusy == true
                || (target is not null && _catalog.IndexOf(target) < 0)) return;
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
            _sink.SetStatusText(StatusFormatter.ActionNoDestination(action.Name));
            return;
        }

        await ExecuteFileActionCoreAsync(action.Name, action.Operation, action.Destination, compareSelectedPath, currentPath);
    }

    public async Task RecycleAsync(string? compareSelectedPath, string? currentPath)
    {
        await ExecuteFileActionCoreAsync(Tr.ActionRecycleName, FileOperationType.Recycle, null, compareSelectedPath, currentPath);
    }

    /// <summary>Q-R8: the setting is on and <paramref name="source"/> is on a drive without a Recycle Bin, so Recycle would delete permanently.</summary>
    private bool WillAskPermanentDelete(string? source) =>
        !string.IsNullOrEmpty(source)
        && _getSettings().AllowPermanentDeleteWithoutRecycleBin
        && _fileActionService is not null
        && _fileActionService.LacksRecycleBin(source);

    private async Task ExecuteFileActionCoreAsync(string actionName, FileOperationType operation, string? destination, string? compareSelectedPath, string? currentPath)
    {
        if (_catalog.Count == 0) return;
        if (_fileActionService is null) return;

        // INV-4: Gate bận
        if (_fileActionService.IsBusy) return;

        var source = compareSelectedPath ?? currentPath;
        if (string.IsNullOrEmpty(source)) return;

        // Q-R8: permanent delete only with the setting on AND an explicit "this is permanent" confirmation every time.
        // Without a dialog service nothing can be confirmed, so nothing is deleted. Setting off: the request stays
        // AllowPermanentDelete=false and the service refuses (fixed drives never reach this branch).
        var allowPermanent = false;
        if (operation == FileOperationType.Recycle && WillAskPermanentDelete(source))
        {
            // The dialog runs a nested dispatcher loop: a forwarded open can switch the folder meanwhile.
            var folderBeforeDialog = _clock.CurrentFolder;
            if (_dialogService is null
                || !_dialogService.ShowConfirmation(Tr.DialogConfirmPermanentDeleteTitle, Tr.DialogConfirmPermanentDeleteMessage(Path.GetFileName(source))))
                return;
            if (_clock.CurrentFolder != folderBeforeDialog || _fileActionService.IsBusy || _catalog.IndexOf(source) < 0) return;
            allowPermanent = true;
        }

        var sourceIndex = _catalog.IndexOf(source);
        _clock.StopForAction();
        _preloadController?.Cancel();
        var folderGen = _clock.CurrentFolder;

        var isRemove = operation is FileOperationType.Move or FileOperationType.Recycle;
        int nextIndex = -1;

        if (isRemove)
        {
            nextIndex = _catalog.Remove(source);
            _sink.OnCatalogChanged(source);

            // INV-3: Trình diễn ảnh tiếp theo TRƯỚC KHI thao tác file hoàn thành, không await
            if (nextIndex >= 0)
            {
                _ = _sink.PresentAsync(nextIndex);
            }
            else
            {
                _sink.SetStatusText(StatusFormatter.AllImagesProcessed());
            }
        }

        try
        {
            var request = new FileActionRequest(source, operation, destination, allowPermanent);
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
                    _sink.SetStatusText(isRemove ? StatusFormatter.AllImagesProcessed() : StatusFormatter.ActionCompleted(actionName));
                }
                else if (operation == FileOperationType.Copy)
                {
                    _sink.SetStatusText(StatusFormatter.CopiedTo(Path.GetFileName(result.DestinationPath)));
                }
            }
            else
            {
                // INV-5: Thất bại thì khôi phục lại ảnh nguồn vào danh mục
                if (isRemove && sourceIndex >= 0)
                {
                    _catalog.Restore(source, sourceIndex);
                }

                _sink.SetStatusText(StatusFormatter.ActionFailed(actionName, result.Error));
            }
        }
        finally
        {
            _sink.NotifyNavigationStateChanged();
        }
    }

    /// <param name="currentFolder">The folder open when the undo started; a Move restored elsewhere is not inserted here.</param>
    public async Task<UndoResult?> UndoLastAsync(string? currentFolder)
    {
        if (_undoService is null) return null;

        var folderGen = _clock.CurrentFolder;
        var result = await _undoService.UndoLastAsync();

        if (!result.Succeeded)
        {
            _sink.SetStatusText(result.ErrorMessage ?? StatusFormatter.NothingToUndo());
            return result;
        }

        if (!_clock.IsFolderCurrent(folderGen)) return result;

        // R7-2: a Move made in another folder is restored there, not into this folder's catalog; the caller opens
        // that folder at the restored file, as for a Recycle undo (see RestoresOutsideFolder).
        if (result.Operation == FileOperationType.Move && !string.IsNullOrEmpty(result.Source)
            && IsInFolder(result.Source, currentFolder))
        {
            _catalog.InsertSorted(result.Source, (a, b) => _naturalComparer.Compare(Path.GetFileName(a), Path.GetFileName(b)));
            _sink.OnCatalogChanged(null);
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

    /// <summary>
    /// R7-2: a successful undo whose restored file is not in <paramref name="currentFolder"/> -- a Recycle (always
    /// reloaded) or a Move made in a previous folder -- so the caller opens the file's folder at that file.
    /// </summary>
    public static bool RestoresOutsideFolder(UndoResult? result, string? currentFolder) =>
        result is { Succeeded: true } && !string.IsNullOrEmpty(result.Source)
        && (result.Operation == FileOperationType.Recycle
            || (result.Operation == FileOperationType.Move && !IsInFolder(result.Source, currentFolder)));

    private static bool IsInFolder(string path, string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return false;
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent)) return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
