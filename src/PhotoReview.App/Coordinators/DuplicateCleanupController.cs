using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// Điều khiển tìm kiếm và xóa ảnh trùng lặp, quản lý cache preview/thumbnail.
/// Kiểm tra generation folder chống race condition, xác nhận dialog qua IUiScheduler.
/// </summary>
public sealed class DuplicateCleanupController
{
    private readonly GenerationClock _clock;
    private readonly ReviewCatalog _catalog;
    private readonly FileActionService? _fileActionService;
    private readonly FileHashService? _hashService;
    private readonly IFileSystem _fileSystem;
    private readonly IDialogService? _dialogService;
    private readonly IUiScheduler _uiScheduler;
    private readonly IPreloadController? _preloadController;
    private readonly ThumbnailCache? _thumbnailCache;
    private readonly PreviewImageService? _previewService;
    private readonly IDuplicateCleanupSink _sink;

    public DuplicateCleanupController(
        GenerationClock clock,
        ReviewCatalog catalog,
        FileActionService? fileActionService,
        FileHashService? hashService,
        IFileSystem fileSystem,
        IDialogService? dialogService,
        IUiScheduler uiScheduler,
        IPreloadController? preloadController,
        ThumbnailCache? thumbnailCache,
        PreviewImageService? previewService,
        IDuplicateCleanupSink sink)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _fileActionService = fileActionService;
        _hashService = hashService;
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _dialogService = dialogService;
        _uiScheduler = uiScheduler ?? throw new ArgumentNullException(nameof(uiScheduler));
        _preloadController = preloadController;
        _thumbnailCache = thumbnailCache;
        _previewService = previewService;
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public async Task RemoveDuplicatesAsync(bool removeNumbered)
    {
        if (_fileActionService is null || _fileActionService.IsBusy) return;
        if (_catalog.Count == 0) return;

        var preHashGeneration = _clock.CurrentFolder;
        var candidates = _catalog.Paths.ToArray();
        if (candidates.Length == 0) return;

        List<string> remove;
        try
        {
            remove = await DuplicateFinder.FindAsync(
                candidates,
                removeNumbered,
                (path, ct) => _hashService?.GetAsync(path, ct) ?? Task.FromResult(string.Empty),
                _fileSystem,
                System.Threading.CancellationToken.None);
        }
        catch (Exception ex)
        {
            _sink.SetStatusText(StatusFormatter.DuplicateCheckFailed(ex.Message));
            return;
        }

        // Stale Folder Guard: Nếu folder đã bị đổi trong khi hash, hủy bỏ thao tác
        if (!_clock.IsFolderCurrent(preHashGeneration))
        {
            _sink.SetStatusText(StatusFormatter.DuplicateCheckCanceledFolderChanged());
            return;
        }

        if (remove.Count == 0)
        {
            _sink.SetStatusText(StatusFormatter.NoDuplicatesFound());
            return;
        }

        if (_dialogService is not null)
        {
            var confirmed = false;
            await _uiScheduler.InvokeAsync(() => confirmed = _dialogService.ShowBatchReview(remove));
            if (!confirmed)
            {
                _sink.SetStatusText(StatusFormatter.BatchCanceled());
                return;
            }
        }

        _clock.StopForAction();
        _preloadController?.Cancel();
        var actionFolderGeneration = _clock.CurrentFolder;

        var failures = new List<string>();
        var succeeded = 0;

        foreach (var path in remove)
        {
            var request = new FileActionRequest(path, FileOperationType.Recycle);
            // ADR 0005: ExecuteAsync stats + fsyncs its journal entry before its first await; run it
            // on the pool so a batch of N files does not do N synchronous fsyncs on the UI thread.
            // The continuation (counters, final status/dialog) still returns to the UI thread.
            var result = await Task.Run(() => _fileActionService.ExecuteAsync(request));
            if (result.Succeeded)
            {
                succeeded++;
            }
            else
            {
                failures.Add($"{Path.GetFileName(path)}: {result.Error}");
            }
        }

        if (!_clock.IsFolderCurrent(actionFolderGeneration))
        {
            return;
        }

        _sink.SetStatusText(StatusFormatter.BatchDone(succeeded, failures.Count));
        if (failures.Count > 0 && _dialogService is not null)
        {
            _dialogService.ShowError(Tr.DialogBatchErrorsTitle,string.Join(Environment.NewLine, failures));
        }

        if (succeeded > 0 && remove.Count > 0)
        {
            var folder = Path.GetDirectoryName(remove[0]);
            if (!string.IsNullOrEmpty(folder))
            {
                await _sink.OpenFolderAsync(folder);
            }
        }
    }

    public async Task ClearCacheAsync()
    {
        if (_dialogService is not null)
        {
            var confirmed = _dialogService.ShowConfirmation(Tr.DialogClearCacheTitle, Tr.DialogClearCacheMessage);
            if (!confirmed) return;
        }

        _preloadController?.Cancel();
        _thumbnailCache?.ClearDisk();
        _thumbnailCache?.ClearMemory();
        _previewService?.ClearCache();
        _previewService?.ClearDisk();
        _preloadController?.ClearPreloadedKeys();
        _sink.SetStatusText(StatusFormatter.CacheCleared());
        await Task.CompletedTask;
    }
}
