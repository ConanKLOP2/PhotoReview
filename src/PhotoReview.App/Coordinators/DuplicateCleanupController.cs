using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
    private readonly IFileHasher? _hashService;
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
        IFileHasher? hashService,
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

    private readonly object _hashGate = new();
    private CancellationTokenSource? _hashCts;

    /// <summary>
    /// Q-R25 (option A): cancels a running duplicate-check HASHING phase. Returns true while a check is hashing (so the
    /// caller, e.g. Esc, consumes the key even on a repeated press) and false when nothing is running - in particular
    /// after hashing finished, so it can never touch the review dialog or the recycle batch (option B, later).
    /// </summary>
    public bool CancelDuplicateCheck()
    {
        lock (_hashGate)
        {
            if (_hashCts is null) return false;
            CancelQuietly(_hashCts);
            return true;
        }
    }

    private static void CancelQuietly(CancellationTokenSource cts)
    {
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { } // its owner already finished and disposed it
    }

    public async Task RemoveDuplicatesAsync(bool removeNumbered)
    {
        if (_fileActionService is null || _fileActionService.IsBusy) return;
        if (_catalog.Count == 0) return;

        var preHashGeneration = _clock.CurrentFolder;
        var candidates = _catalog.Paths.ToArray();
        if (candidates.Length == 0) return;

        List<string> remove;
        var cts = new CancellationTokenSource();
        lock (_hashGate)
        {
            // A fresh check supersedes a previous one that is somehow still hashing (the file-action gate normally prevents it).
            if (_hashCts is { } previous) CancelQuietly(previous);
            _hashCts = cts;
        }
        _sink.SetStatusText(StatusFormatter.DuplicateCheckRunning());
        try
        {
            remove = await DuplicateFinder.FindAsync(
                candidates,
                removeNumbered,
                (path, ct) =>
                {
                    // Minimize disk reads: once the folder changed the result is discarded anyway (guard below),
                    // so stop hashing the remaining same-size files instead of reading them all.
                    if (!_clock.IsFolderCurrent(preHashGeneration)) throw new OperationCanceledException();
                    return _hashService?.GetAsync(path, ct) ?? throw UserFacingError.Localized(
                        new InvalidOperationException("Duplicate detection needs a hash service; without one every same-size file would look identical."),
                        () => Tr.ErrIoHashServiceMissing);
                },
                _fileSystem,
                cts.Token);
            // Esc pressed just as the last hash finished: honor the user's intent, apply nothing.
            cts.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!_clock.IsFolderCurrent(preHashGeneration))
        {
            _sink.SetStatusText(StatusFormatter.DuplicateCheckCanceledFolderChanged());
            return;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // A run superseded by a fresh check stays silent: the newer run owns the status text.
            bool superseded;
            lock (_hashGate) superseded = !ReferenceEquals(_hashCts, cts);
            if (!superseded) _sink.SetStatusText(StatusFormatter.DuplicateCheckCanceled());
            return;
        }
        catch (Exception ex)
        {
            _sink.SetStatusText(StatusFormatter.DuplicateCheckFailed(UserFacingError.Describe(ex)));
            return;
        }
        finally
        {
            // Hashing is over (or aborted): from here Esc no longer targets this check, and the source is released.
            lock (_hashGate)
            {
                if (ReferenceEquals(_hashCts, cts)) _hashCts = null;
            }
            cts.Dispose();
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

        // Q-R14: batch cleanup never deletes permanently. Say so once, up front, instead of one refusal per file after the confirmation.
        var withoutBin = remove.FirstOrDefault(_fileActionService.LacksRecycleBin);
        if (withoutBin is not null)
        {
            _sink.SetStatusText(StatusFormatter.BatchCanceled());
            _dialogService?.ShowError(Tr.DialogBatchErrorsTitle, Tr.CoreRecycleUnsupportedDrive(Path.GetFileName(withoutBin)));
            return;
        }

        if (_dialogService is not null)
        {
            // R7-4: the review dialog runs a nested dispatcher loop; a forwarded open can switch the folder meanwhile.
            var dialogFolderGeneration = _clock.CurrentFolder;
            var confirmed = false;
            await _uiScheduler.InvokeAsync(() => confirmed = _dialogService.ShowBatchReview(remove));
            if (!confirmed)
            {
                _sink.SetStatusText(StatusFormatter.BatchCanceled());
                return;
            }
            if (!_clock.IsFolderCurrent(dialogFolderGeneration))
            {
                _sink.SetStatusText(StatusFormatter.DuplicateCheckCanceledFolderChanged());
                return;
            }
            if (_fileActionService.IsBusy) return;
            remove = remove.Where(path => _catalog.IndexOf(path) >= 0).ToList();
            if (remove.Count == 0)
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
            _dialogService.ShowError(Tr.DialogBatchErrorsTitle, string.Join(Environment.NewLine, failures));
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
        // RAM first (cheap, and it also bumps the epochs so in-flight writers stop persisting) ...
        _thumbnailCache?.ClearMemory();
        _previewService?.ClearCache();
        // R2-F-16: "Clear cache" also has to drop the source-bytes RAM cache (up to 16 GB when that option is on).
        _previewService?.ClearSourceBytesCache();
        _preloadController?.ClearPreloadedKeys();
        // ... then the directory deletes (up to the multi-GB disk quota, thousands of files) off the UI thread.
        await Task.Run(() =>
        {
            _thumbnailCache?.ClearDisk();
            _previewService?.ClearDisk();
        });
        _sink.SetStatusText(StatusFormatter.CacheCleared());
    }
}
