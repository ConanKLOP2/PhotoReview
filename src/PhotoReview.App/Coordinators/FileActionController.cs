using System;
using System.Collections.Generic;
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
    private readonly IFolderPicker? _folderPicker;
    private readonly Func<string, bool> _directoryExists;
    private readonly Action<FileOperationType, string>? _rememberFolder;
    private readonly IFileSystem? _fileSystem;

    /// <param name="folderPicker">"Move to… / Copy to…" folder picker; null disables those commands unless the last folder is reused.</param>
    /// <param name="fileSystem">Used to check that a picked or remembered folder exists (defaults to the real disk).</param>
    /// <param name="rememberFolder">Persists the folder a successful Move-to/Copy-to went to (settings LastMoveToFolder/LastCopyToFolder).</param>
    public FileActionController(
        ReviewCatalog catalog,
        GenerationClock clock,
        FileActionService? fileActionService,
        UndoService? undoService,
        IDialogService? dialogService,
        IPreloadController? preloadController,
        INaturalComparer naturalComparer,
        Func<AppSettings> getSettings,
        IFileActionSink sink,
        IFolderPicker? folderPicker = null,
        IFileSystem? fileSystem = null,
        Action<FileOperationType, string>? rememberFolder = null)
    {
        _folderPicker = folderPicker;
        _fileSystem = fileSystem;
        _directoryExists = fileSystem is not null ? fileSystem.DirectoryExists : Directory.Exists;
        _rememberFolder = rememberFolder;
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
        // Q-R44: general "confirm before delete", off by default. Skipped when the permanent-delete prompt
        // (Q-R8, WillAskPermanentDelete) will already ask -- same "one prompt, not two" rule as RunActionAsync.
        var source = compareSelectedPath ?? currentPath;
        var selectedGroup = source is null ? null : _catalog.Find(source)?.CaptureGroup;
        var permanentPrompt = selectedGroup is null && WillAskPermanentDelete(source);
        if (selectedGroup is null && _getSettings().ConfirmBeforeDelete && _dialogService is not null && !permanentPrompt)
        {
            var folderBeforeDialog = _clock.CurrentFolder;
            var ok = _dialogService.ShowConfirmation(Tr.DialogConfirmActionTitle, Tr.DialogConfirmActionMessage(Tr.ActionRecycleName));
            if (!ok) return;
            if (_clock.CurrentFolder != folderBeforeDialog || _fileActionService?.IsBusy == true
                || (source is not null && _catalog.IndexOf(source) < 0)) return;
        }

        await ExecuteFileActionCoreAsync(Tr.ActionRecycleName, FileOperationType.Recycle, null, compareSelectedPath, currentPath);
    }

    /// <summary>Q-R8: the setting is on and <paramref name="source"/> is on a drive without a Recycle Bin, so Recycle would delete permanently.</summary>
    private bool WillAskPermanentDelete(string? source) =>
        !string.IsNullOrEmpty(source)
        && _getSettings().AllowPermanentDeleteWithoutRecycleBin
        && _fileActionService is not null
        && _fileActionService.LacksRecycleBin(source);

    /// <returns>True when the file operation succeeded and the folder is still the current one.</returns>
    private async Task<bool> ExecuteFileActionCoreAsync(string actionName, FileOperationType operation, string? destination, string? compareSelectedPath, string? currentPath)
    {
        if (_catalog.Count == 0) return false;
        if (_fileActionService is null) return false;

        // INV-4: Gate bận
        if (_fileActionService.IsBusy) return false;

        var source = compareSelectedPath ?? currentPath;
        if (string.IsNullOrEmpty(source)) return false;
        var selectedGroup = _catalog.Find(source)?.CaptureGroup;
        if (selectedGroup is not null && _fileSystem is null) return false;

        // Q-R8: permanent delete only with the setting on AND an explicit "this is permanent" confirmation every time.
        // Without a dialog service nothing can be confirmed, so nothing is deleted. Setting off: the request stays
        // AllowPermanentDelete=false and the service refuses (fixed drives never reach this branch).
        var allowPermanent = false;
        if (operation == FileOperationType.Recycle && selectedGroup is null && WillAskPermanentDelete(source))
        {
            // The dialog runs a nested dispatcher loop: a forwarded open can switch the folder meanwhile.
            var folderBeforeDialog = _clock.CurrentFolder;
            if (_dialogService is null
                || !_dialogService.ShowConfirmation(Tr.DialogConfirmPermanentDeleteTitle, Tr.DialogConfirmPermanentDeleteMessage(Path.GetFileName(source))))
                return false;
            if (_clock.CurrentFolder != folderBeforeDialog || _fileActionService.IsBusy || _catalog.IndexOf(source) < 0) return false;
            allowPermanent = true;
        }

        // Same semantics as the single-file path (WillAskPermanentDelete): the permanent-delete prompt, and with it
        // allowPermanent, exist only when the setting is ON and a member really lacks a Recycle Bin. With the setting
        // OFF the request stays AllowPermanentDelete=false and the service refuses BEFORE anything is journaled.
        var permanentGroupPaths = operation == FileOperationType.Recycle && selectedGroup is not null
            && _getSettings().AllowPermanentDeleteWithoutRecycleBin
            ? selectedGroup.Paths.Where(_fileActionService.LacksRecycleBin).ToArray()
            : [];
        if (selectedGroup is not null && operation == FileOperationType.Recycle
            && (_getSettings().ConfirmBeforeDelete || permanentGroupPaths.Length > 0))
        {
            var folderBeforeGroupDialog = _clock.CurrentFolder;
            var prompt = permanentGroupPaths.Length > 0
                ? Tr.DialogConfirmGroupPermanentDeleteMessage(Path.GetFileName(source), permanentGroupPaths.Length, selectedGroup.Paths.Count)
                : Tr.DialogConfirmGroupRecycleMessage(Path.GetFileName(source), selectedGroup.Paths.Count);
            var title = permanentGroupPaths.Length > 0 ? Tr.DialogConfirmPermanentDeleteTitle : Tr.DialogConfirmActionTitle;
            if (_dialogService is null || !_dialogService.ShowConfirmation(title, prompt)) return false;
            if (_clock.CurrentFolder != folderBeforeGroupDialog || _fileActionService.IsBusy || _catalog.IndexOf(source) < 0) return false;
            allowPermanent = permanentGroupPaths.Length > 0;
        }

        // The confirmations above run nested dispatcher loops: the entry may have been degraded to a standalone survivor
        // (readability probe / RemoveOrDegrade) or regrouped meanwhile. Never act on files that are no longer shown together.
        var group = _catalog.Find(source)?.CaptureGroup;
        if (!Equals(group, selectedGroup))
        {
            _sink.SetStatusText(Tr.StatusGroupChangedDuringConfirm(Path.GetFileName(source)));
            return false;
        }

        var sourceIndex = _catalog.IndexOf(source);
        var isRemove = operation is FileOperationType.Move or FileOperationType.Recycle;
        // Only a Move/Recycle takes the file out of the catalog and re-presents the next photo (which also restarts
        // preload). A Copy changes nothing on screen and nothing would restart what StopForAction/Cancel stopped, so it
        // must not invalidate the in-flight present or the preload window (it also reads the source with sharing).
        if (isRemove)
        {
            _clock.StopForAction();
            _preloadController?.Cancel();
        }

        var folderGen = _clock.CurrentFolder;
        int nextIndex = -1;
        // The fire-and-forget presenter writes the status line when it finishes; a failure message must be set after it.
        Task? presentTask = null;
        // Neighbour that precedes the source in the review order: Undo of a Move puts the photo back after it.
        var previousPath = isRemove && sourceIndex > 0 ? _catalog.PathAt(sourceIndex - 1) : null;

        if (isRemove)
        {
            nextIndex = _catalog.Remove(source);
            _sink.OnCatalogChanged(source);

            // INV-3: Trình diễn ảnh tiếp theo TRƯỚC KHI thao tác file hoàn thành, không await
            if (nextIndex >= 0)
            {
                presentTask = _sink.PresentAsync(nextIndex);
            }
            else
            {
                _sink.SetStatusText(StatusFormatter.AllImagesProcessed());
            }
        }

        try
        {
            CaptureGroupActionResult? groupResult = null;
            FileActionResult? singleResult = null;
            if (group is null)
                singleResult = await _fileActionService.ExecuteAsync(new FileActionRequest(source, operation, destination, allowPermanent));
            else
                groupResult = await _fileActionService.ExecuteGroupAsync(new CaptureGroupActionRequest(group, operation, destination, allowPermanent));
            var succeeded = groupResult?.Succeeded ?? singleResult!.Succeeded;
            var sourceRemoved = groupResult is not null
                ? groupResult.Members.Any(member => member.Completed && (operation is FileOperationType.Move or FileOperationType.Recycle))
                : singleResult!.SourceRemoved;

            // Stale Folder Guard: the user switched folder while the I/O ran. The new folder's catalog, session path,
            // current image and gate state must not be touched (the file belonged to the previous folder).
            // APP-03 (Q-R25, option B): a SUCCESSFUL action is still real on disk, so it is registered for Undo and
            // the user is told (see ReportLateCompletion). On failure nothing changed on disk: no undo, and the
            // INV-5 restore into the (now different) catalog is skipped.
            if (!_clock.IsFolderCurrent(folderGen))
            {
                if (groupResult is not null)
                {
                    _undoService?.RegisterGroup(groupResult); // also the completed members of a part-way failed Delete
                    if (groupResult.Succeeded)
                    {
                        var name = Path.GetFileName(source);
                        var target = groupResult.Entry?.Destination;
                        if (operation == FileOperationType.Move && target is not null)
                            _sink.ShowLateActionStatus(Tr.StatusLateMoveUndoable(name, Path.GetDirectoryName(target) ?? string.Empty));
                        else if (operation == FileOperationType.Recycle)
                        {
                            // PermanentlyDeleted is true when ANY member was deleted for good: say exactly which ones, so a
                            // capture whose other members went to the Recycle Bin is not reported as fully permanent.
                            var permanentNames = groupResult.Members.Where(member => member.Completed && member.Member.Permanent)
                                .Select(member => Path.GetFileName(member.Member.Source)).ToArray();
                            var recycledAny = groupResult.Members.Any(member => member.Completed && !member.Member.Permanent);
                            if (permanentNames.Length > 0 && recycledAny)
                                _sink.ShowLateActionStatus(Tr.StatusLateDeletedPartlyPermanently(name, string.Join(", ", permanentNames)));
                            else if (permanentNames.Length > 0)
                                _sink.ShowLateActionStatus(Tr.StatusLateDeletedPermanently(name));
                            else
                                _sink.ShowLateActionStatus(Tr.StatusLateRecycleUndoable(name));
                        }
                    }
                }
                else if (singleResult!.Succeeded) ReportLateCompletion(singleResult);
                return false;
            }

            if (succeeded)
            {
                if (groupResult is not null) _undoService?.RegisterGroup(groupResult);
                else _undoService?.Register(singleResult!);
                // Move AND Recycle remember where the entry sat: a partly failed undo puts the members back there.
                if (isRemove && sourceIndex >= 0) RememberMovePosition(source, previousPath, group);
                _sink.UpdateSessionPath(_catalog.Current?.Path ?? source);

                if (groupResult?.SkippedMissing is { Count: > 0 } skipped)
                {
                    // A partner file vanished outside the app: the action covered the remaining files. Say so (after the
                    // presenter, which writes its own status when it finishes) -- but only while this folder is still the
                    // open one, and without replacing "All images processed" when nothing is left to review.
                    if (presentTask is not null) await presentTask;
                    if (_clock.IsFolderCurrent(folderGen))
                    {
                        var missing = Tr.StatusGroupPartnerMissing(Path.GetFileName(source), string.Join(", ", skipped.Select(Path.GetFileName)));
                        _sink.SetStatusText(isRemove && _catalog.Count == 0 ? StatusFormatter.AllImagesProcessed() + " " + missing : missing);
                    }
                }
                else if (_catalog.Count == 0)
                {
                    _sink.SetStatusText(isRemove ? StatusFormatter.AllImagesProcessed() : StatusFormatter.ActionCompleted(actionName));
                }
                else if (operation == FileOperationType.Copy)
                {
                    var dest = groupResult?.Entry?.Destination ?? singleResult!.DestinationPath;
                    _sink.SetStatusText(StatusFormatter.CopiedTo(Path.GetFileName(dest)));
                }
                return true;
            }

            // F3: a Move that failed verification (size differs) after the source was already removed. The journal says
            // Failed (Recovery window), but the source path no longer exists, so it must NOT go back into the catalog.
            // No Undo is registered: the destination no longer matches the fingerprint of the prepared source.
            if (operation == FileOperationType.Move && sourceRemoved && groupResult is null)
            {
                if (presentTask is not null) await presentTask;
                _sink.UpdateSessionPath(_catalog.Current?.Path ?? source);
                _sink.SetStatusText(Tr.StatusMoveUnverified(Path.GetFileName(source)));
                return false;
            }

            // A Delete that failed part-way: the members already in the Recycle Bin get a Ctrl+Z (RegisterGroup ignores the rest).
            if (groupResult is not null) _undoService?.RegisterGroup(groupResult);

            // INV-5: Thất bại thì khôi phục lại ảnh nguồn vào danh mục
            if (isRemove && sourceIndex >= 0)
            {
                if (groupResult is null) _catalog.Restore(source, sourceIndex);
                else
                {
                    // No member state at all (preflight refused it, or the gate was busy): nothing changed on disk, so the
                    // whole capture goes back as the one grouped entry it was. Otherwise exactly the image members still on
                    // disk return (re-forming the group when all of them are). RestoreMembers keeps CurrentIndex on the
                    // photo the presenter is showing, like Restore does for a single file.
                    var imagePaths = group!.ImagePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    IEnumerable<string> available = groupResult.Members.Count == 0
                        ? group.ImagePaths
                        : groupResult.Members.Where(member => imagePaths.Contains(member.Member.Source)
                                && member.StateKnown && member.SourceExists)
                            .Select(member => member.Member.Source);
                    _catalog.RestoreMembers(available, sourceIndex, group);
                    _sink.OnCatalogChanged(null);
                }
            }

            if (presentTask is not null) await presentTask;
            _sink.SetStatusText(StatusFormatter.ActionFailed(actionName, groupResult?.Error ?? singleResult!.Error));
            return false;
        }
        finally
        {
            _sink.NotifyNavigationStateChanged();
        }
    }

    /// <summary>Upper bound of remembered Move positions; older ones fall back to the name-ordered insert.</summary>
    private const int MaxRememberedMovePositions = 256;

    /// <summary>Where a moved photo sat: the review-order neighbour before it (null = it was first) and, for a capture, its group.</summary>
    private sealed record MovePosition(string? PreviousPath, CaptureGroup? Group);

    // Undo of a Move: remembered by source path. A capture is remembered under EVERY member path (the undo result reports the
    // first manifest member, which is not necessarily the catalog entry's representative).
    private readonly Dictionary<string, MovePosition> _movePositions = new(StringComparer.OrdinalIgnoreCase);

    private void RememberMovePosition(string source, string? previousPath, CaptureGroup? group)
    {
        if (_movePositions.Count >= MaxRememberedMovePositions) _movePositions.Clear();
        var position = new MovePosition(previousPath, group);
        _movePositions[source] = position;
        if (group is null) return;
        foreach (var path in group.Paths) _movePositions[path] = position;
    }

    /// <summary>
    /// Puts an undone Move back where it was in the review order (after its former predecessor) instead of by file
    /// name, which is only right when the catalog is sorted by ascending name (Explorer, size, folder order, Z-A differ).
    /// Falls back to the natural name order when the position is unknown or the predecessor is no longer listed.
    /// A capture (<paramref name="restoredPaths"/>) returns as one grouped entry at that same position; sidecars never
    /// become entries.
    /// </summary>
    private void InsertRestoredMove(string source, IReadOnlyList<string>? restoredPaths, bool keepPosition = false)
    {
        // A partly failed undo keeps the remembered position (and its group): the retry that restores the rest must still
        // find the group to fold the members back into one entry.
        var position = keepPosition ? PeekMovePosition(source) : TakeMovePosition(source);

        int index;
        if (position is not null && position.PreviousPath is null) index = 0;
        else if (position?.PreviousPath is { } previousPath && _catalog.IndexOf(previousPath) is var previousIndex and >= 0) index = previousIndex + 1;
        else index = SortedInsertIndex(source);

        if (restoredPaths is { Count: > 0 })
            _catalog.RestoreMembers(restoredPaths, index, position?.Group);
        else
            _catalog.Restore(source, index);
    }

    private MovePosition? PeekMovePosition(string source) => _movePositions.GetValueOrDefault(source);

    /// <summary>Returns and forgets the remembered position of <paramref name="source"/> (and of every member of its capture).</summary>
    private MovePosition? TakeMovePosition(string source)
    {
        if (!_movePositions.Remove(source, out var position)) return null;
        if (position.Group is { } forgetGroup)
            foreach (var path in forgetGroup.Paths) _movePositions.Remove(path);
        return position;
    }

    private int SortedInsertIndex(string path)
    {
        var index = 0;
        while (index < _catalog.Count
            && _naturalComparer.Compare(Path.GetFileName(_catalog.PathAt(index)), Path.GetFileName(path)) < 0)
            index++;
        return index;
    }

    /// <summary>
    /// APP-03: a Move/Recycle/Copy that finished after the folder changed. Move and Recycle are registered with the
    /// undo service (Ctrl+Z restores the file to its original path and reopens that folder, R7-2); Copy has no undo
    /// (UndoService ignores it) and stays silent as before. Only <see cref="IFileActionSink.ShowLateActionStatus"/>
    /// is used for the message, which the sink drops when the new folder's status line holds its own text (a load
    /// in progress, "no images", "open failed"): losing this hint is harmless, Ctrl+Z still works.
    /// </summary>
    private void ReportLateCompletion(FileActionResult result)
    {
        if (result.Rejected) return;
        _undoService?.Register(result);

        var fileName = Path.GetFileName(result.Source);
        switch (result.Operation)
        {
            case FileOperationType.Move when !string.IsNullOrEmpty(result.DestinationPath):
                _sink.ShowLateActionStatus(Tr.StatusLateMoveUndoable(fileName, Path.GetDirectoryName(result.DestinationPath) ?? string.Empty));
                break;
            case FileOperationType.Recycle when result.PermanentlyDeleted:
                _sink.ShowLateActionStatus(Tr.StatusLateDeletedPermanently(fileName));
                break;
            case FileOperationType.Recycle:
                _sink.ShowLateActionStatus(Tr.StatusLateRecycleUndoable(fileName));
                break;
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
            // A capture undo that failed half way still put some members back: show them now instead of at the next reload.
            if (_clock.IsFolderCurrent(folderGen) && result.RestoredPaths is { Count: > 0 } partial
                && !string.IsNullOrEmpty(result.Source) && IsInFolder(result.Source, currentFolder))
            {
                var inFolder = partial.Where(path => IsInFolder(path, currentFolder)).ToArray();
                if (inFolder.Length > 0)
                {
                    // Move and Recycle both go back to their remembered review position. When the catalog was empty (the
                    // undone action removed the last capture) nothing is on screen: present the restored entry, otherwise
                    // keep showing the current photo.
                    var wasEmpty = _catalog.Count == 0;
                    InsertRestoredMove(result.Source, inFolder, keepPosition: true);
                    _sink.OnCatalogChanged(null);
                    if (wasEmpty && _catalog.Count > 0)
                    {
                        await _sink.PresentAsync(Math.Max(_catalog.CurrentIndex, 0));
                        if (!_clock.IsFolderCurrent(folderGen)) return result;
                    }
                    _sink.UpdateSessionPath(_catalog.Current?.Path ?? result.Source);
                    _sink.NotifyNavigationStateChanged();
                }
            }

            _sink.SetStatusText(result.ErrorMessage ?? StatusFormatter.NothingToUndo());
            return result;
        }

        if (!_clock.IsFolderCurrent(folderGen)) return result;

        // R7-2: a Move made in another folder is restored there, not into this folder's catalog; the caller opens
        // that folder at the restored file, as for a Recycle undo (see RestoresOutsideFolder).
        if (RestoresOutsideFolder(result, currentFolder))
        {
            // The caller reloads the folder, which rebuilds the catalog from disk and presents the restored photo once:
            // restoring into the catalog and presenting here as well would decode the same image twice (a Recycle undo,
            // or a Move whose members are split across folders, so that part of them is in this one).
            // Only for the current folder: a restore made in another folder must not write its path into THIS folder's session.
            TakeMovePosition(result.Source);
            if (IsInFolder(result.Source, currentFolder))
            {
                _sink.OnCatalogChanged(null);
                _sink.UpdateSessionPath(result.Source);
            }
        }
        else if (result.Operation == FileOperationType.Move && !string.IsNullOrEmpty(result.Source)
            && IsInFolder(result.Source, currentFolder))
        {
            InsertRestoredMove(result.Source, result.RestoredPaths is { Count: > 0 } restored
                ? restored.Where(path => IsInFolder(path, currentFolder)).ToArray()
                : null);
            _sink.OnCatalogChanged(null);
            var idx = _catalog.IndexOf(result.Source);
            if (idx >= 0)
            {
                await _sink.PresentAsync(idx);
            }

            _sink.UpdateSessionPath(result.Source);
        }

        // A capture that could only be restored in part succeeds with a note naming what could not come back.
        if (!string.IsNullOrEmpty(result.ErrorMessage)) _sink.SetStatusText(result.ErrorMessage);
        _sink.NotifyNavigationStateChanged();
        return result;
    }

    /// <summary>
    /// "Move to… / Copy to…": asks for a destination folder (or reuses the last one when
    /// <see cref="AppSettings.MoveCopyReuseLastFolder"/> is on, the folder still exists and <paramref name="forcePicker"/> is
    /// false), validates it like an absolute action destination, then runs the same pipeline as an action profile
    /// (journal, undo for Move, catalog/preload updates, status and error texts). The caller holds the file-action gate.
    /// </summary>
    /// <param name="getSource">Reads the photo to act on (compare selection or current image) live, so it can be re-read
    /// after the modal picker returns.</param>
    public async Task MoveOrCopyToFolderAsync(FileOperationType operation, bool forcePicker, Func<(string? CompareSelectedPath, string? CurrentPath)> getSource)
    {
        if (operation is not (FileOperationType.Move or FileOperationType.Copy))
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Only Move and Copy have a destination folder.");
        ArgumentNullException.ThrowIfNull(getSource);
        if (_catalog.Count == 0 || _fileActionService is null || _fileActionService.IsBusy) return;

        var (compareSelectedPath, currentPath) = getSource();
        var source = compareSelectedPath ?? currentPath;
        if (string.IsNullOrEmpty(source)) return;
        var photoFolder = Path.GetDirectoryName(source);
        if (string.IsNullOrEmpty(photoFolder)) return;

        var isMove = operation == FileOperationType.Move;
        var actionName = isMove ? Tr.ActionMoveToFolderName : Tr.ActionCopyToFolderName;
        var settings = _getSettings();
        var lastFolder = isMove ? settings.LastMoveToFolder : settings.LastCopyToFolder;
        var lastUsable = ActionDestinationPolicy.ValidatePickedFolder(lastFolder, photoFolder, _directoryExists) == PickedFolderCheck.Ok;

        string? destination;
        if (settings.MoveCopyReuseLastFolder && !forcePicker && lastUsable)
        {
            destination = lastFolder;
        }
        else
        {
            if (_folderPicker is null) return;
            var initialFolder = lastFolder is not null && _directoryExists(lastFolder) ? lastFolder : Path.GetDirectoryName(photoFolder) ?? photoFolder;

            // The picker runs a nested dispatcher loop: a forwarded open can switch the folder, and anything that still
            // reaches the view model (not the gate holder) can change the photo or start a file operation meanwhile.
            var folderBeforeDialog = _clock.CurrentFolder;
            destination = _folderPicker.PickFolder(isMove ? Tr.DialogMoveToFolderTitle : Tr.DialogCopyToFolderTitle, initialFolder);
            if (destination is null) return; // cancelled: no-op

            var (compareAfter, currentAfter) = getSource();
            if (_clock.CurrentFolder != folderBeforeDialog
                || _fileActionService.IsBusy
                || !string.Equals(compareAfter ?? currentAfter, source, StringComparison.OrdinalIgnoreCase)
                || _catalog.IndexOf(source) < 0)
            {
                _sink.SetStatusText(Tr.StatusMoveCopyToStateChanged(actionName));
                return;
            }
        }

        switch (ActionDestinationPolicy.ValidatePickedFolder(destination, photoFolder, _directoryExists))
        {
            case PickedFolderCheck.Ok:
                break;
            case PickedFolderCheck.SameAsPhotoFolder:
                _sink.SetStatusText(Tr.StatusMoveCopyToSameFolder(actionName));
                return;
            case PickedFolderCheck.Missing:
                _sink.SetStatusText(Tr.StatusMoveCopyToFolderMissing(actionName, destination));
                return;
            default:
                _sink.SetStatusText(Tr.StatusMoveCopyToNotAbsolute(actionName));
                return;
        }

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination!));
        if (!await ExecuteFileActionCoreAsync(actionName, operation, folder, compareSelectedPath, currentPath)) return;

        if (!string.Equals(folder, lastFolder, StringComparison.OrdinalIgnoreCase)) _rememberFolder?.Invoke(operation, folder);
        if (_catalog.Count > 0)
        {
            var fileName = Path.GetFileName(source);
            _sink.SetStatusText(isMove ? Tr.StatusMovedToFolder(fileName, folder) : Tr.StatusCopiedToFolder(fileName, folder));
        }
    }

    /// <summary>
    /// R7-2: a successful undo whose restored file is not in <paramref name="currentFolder"/> -- a Recycle (always
    /// reloaded) or a Move made in a previous folder -- so the caller opens the file's folder at that file.
    /// </summary>
    public static bool RestoresOutsideFolder(UndoResult? result, string? currentFolder) =>
        result is { Succeeded: true } && !string.IsNullOrEmpty(result.Source)
        && (result.Operation == FileOperationType.Recycle
            || (result.Operation == FileOperationType.Move
                && (result.RestoredPaths is { Count: > 0 } paths
                    ? paths.Any(path => !IsInFolder(path, currentFolder))
                    : !IsInFolder(result.Source, currentFolder))));

    /// <summary>
    /// The file a reload after <paramref name="result"/> should open at. <c>Source</c> is the first manifest member, which
    /// a partly restored capture may not have got back (it can be the permanently deleted one, no longer on disk): prefer
    /// it when it was restored, otherwise the first restored image/RAW member (sidecars never become entries).
    /// </summary>
    public static string? ReloadPathAfterUndo(UndoResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.RestoredPaths is not { Count: > 0 } restored) return result.Source;
        if (!string.IsNullOrEmpty(result.Source) && restored.Contains(result.Source, StringComparer.OrdinalIgnoreCase)) return result.Source;
        return restored.FirstOrDefault(path => ImageFileTypes.IsSupported(path, rawEnabled: true)) ?? result.Source;
    }

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
