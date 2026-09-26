using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// Điều phối quá trình quét, nạp danh mục và áp dụng thứ tự hiển thị Explorer tự nhiên cho một thư mục ảnh.
/// Đóng gói toàn bộ luồng bất đồng bộ của LoadFolderAsync độc lập với UI.
/// </summary>
public sealed class FolderLoadCoordinator : IDisposable
{
    private readonly ReviewCatalog _catalog;
    private readonly GenerationClock _clock;
    private readonly IExplorerOrderProvider _explorerOrder;
    private readonly IFileSystem _fileSystem;
    private readonly SessionStore _sessionStore;
    private readonly SessionWriter? _sessionWriter;
    private readonly SettingsStore _settingsStore;
    private readonly IFolderLoadSink _sink;

    private CancellationTokenSource? _loadCts;
    private TaskCompletionSource? _pendingOrder;
    private bool _disposed;

    public FolderLoadCoordinator(
        ReviewCatalog catalog,
        GenerationClock clock,
        IExplorerOrderProvider explorerOrder,
        IFileSystem fileSystem,
        SessionStore sessionStore,
        SettingsStore settingsStore,
        IFolderLoadSink sink,
        SessionWriter? sessionWriter = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _explorerOrder = explorerOrder ?? throw new ArgumentNullException(nameof(explorerOrder));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _sessionWriter = sessionWriter;
    }

    /// <summary>
    /// Thực thi tải thư mục ảnh không đồng bộ.
    /// </summary>
    /// <param name="folder">Đường dẫn thư mục ảnh cần mở.</param>
    /// <param name="initialPath">Đường dẫn tệp tin cụ thể được chọn mở trực tiếp (nếu có).</param>
    public async Task LoadAsync(string folder, string? initialPath = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var loadToken = _loadCts.Token;

        var loadGeneration = _clock.NextFolder();
        // A superseded load's gate must not strand navigation that is waiting on it.
        _pendingOrder?.TrySetResult();
        _pendingOrder = null;
        TaskCompletionSource? pendingOrder = null;
        // perf(startup): Folder(gen, phase, msSinceStart) -- restores the T0..T3 trace the D11
        // analyzer reads (lost in T46d). T2 (first image presented) comes from the first Presented
        // event after "start"; the extra phases here split T0->T2 into its IO/sort/explorer parts.
        var perf = new FolderPerf(loadGeneration);
        perf.Mark("start");

        try
        {
            // Trim after GetFullPath: TrimEnd first turned the root "C:\" into "C:" (the drive's current directory).
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!_fileSystem.DirectoryExists(folder))
            {
                throw new DirectoryNotFoundException(Tr.StatusFolderNotFound(folder));
            }

            // perf(startup): the Explorer query needs only the folder, so it starts before the scan
            // instead of after it -- it is the slowest part of a load (~1-2 s of cross-process COM
            // calls for a 1800-item Explorer view) and now runs in parallel with scan + sort.
            var explorerTask = _explorerOrder.TryGetSnapshotProgressiveAsync(
                folder,
                ExplorerQueryTimeout,
                null,
                16,
                loadToken);
            perf.TraceExplorer(explorerTask);

            var sortMode = _settingsStore.Current.ImageSortMode;
            // perf(startup): scan and sort in ONE background task. Two separate Task.Run hops made the
            // sort wait for the UI thread in between -- at startup that is the whole of Window.Show().
            // IO05 (ADR 0007 s3): unreadable files are skipped and counted, never dropped silently.
            var skipped = new List<SkippedEntry>();
            var (scannedFiles, entries) = await Task.Run(() =>
            {
                // The scan gets Length/LastWriteUtc from the same directory entry used to list the
                // file (see PhysicalFileSystem), so this needs no separate GetFileStat() syscall per
                // file. Files that cannot be opened for reading go to `skipped` (this delegate runs
                // on this one background task, so the list needs no lock).
                var scanned = _fileSystem.EnumerateReadableFilesWithStat(folder, ImageFileTypes.IsSupported, skipped.Add)
                    .Select(f => f.Stat is null
                        ? new CatalogEntry(f.Path)
                        : new CatalogEntry(f.Path) { Length = f.Stat.Length, LastWriteUtc = f.Stat.LastWriteUtc })
                    .ToList();
                perf.Mark("scanned", scanned.Count);
                loadToken.ThrowIfCancellationRequested();

                // Sort the scanned entries directly so stat metadata travels with each item.
                // The path map preserves the previous first-match behavior for unusual
                // case-variant duplicate paths while avoiding an O(n²) First lookup.
                var sorted = ImageSortService.SortEntries(scanned, sortMode);
                var firstByPath = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in scanned)
                {
                    firstByPath.TryAdd(entry.Path, entry);
                }

                var result = sorted.Select(entry => firstByPath[entry.Path]).ToList();
                perf.Mark("sorted");
                return (scanned.Select(e => e.Path).ToArray(), result);
            }, loadToken);
            perf.Mark("sortResumed");

            if (initialPath is not null)
            {
                var requested = Path.GetFullPath(initialPath);
                var requestedIndex = entries.FindIndex(e => string.Equals(e.Path, requested, StringComparison.OrdinalIgnoreCase));
                if (requestedIndex > 0)
                {
                    var selected = entries[requestedIndex];
                    entries.RemoveAt(requestedIndex);
                    entries.Insert(0, selected);
                }
            }

            if (loadToken.IsCancellationRequested || !_clock.IsFolderCurrent(loadGeneration))
            {
                return;
            }

            _sink.ResetCaches();
            _sessionWriter?.Flush(); // a pending write for this folder must be visible to Load
            var session = _sessionStore.Load(folder);
            _catalog.Reset(entries);
            _sink.OnCatalogReady(folder, _catalog.Count, session);
            if (skipped.Count > 0)
            {
                AppLog.Warn($"Folder scan skipped {skipped.Count.ToString(CultureInfo.InvariantCulture)} unreadable entr(y/ies) in '{folder}': "
                    + string.Join("; ", skipped.Take(20).Select(s => $"{s.Path} ({s.Reason})")));
                _sink.OnFilesSkipped(folder, skipped);
            }
            perf.Mark("catalogReady");

            var interactionGeneration = _clock.CurrentInteraction;
            var resumePath = initialPath ?? session.CurrentPath;

            // perf(startup): with the batched Explorer read and the startup prefetch the snapshot is
            // usually in before the catalog. Applying it before the first frame reaches the same end
            // state as the late path below (file open: the opened file, now at its Explorer index;
            // folder open: the first image of the Explorer order) without the transient fallback
            // frame, and preload starts around the right neighbours.
            var orderSettled = false;
            if (explorerTask.IsCompleted)
            {
                var earlySnapshot = await explorerTask; // already complete: continues synchronously
                perf.Mark("explorerAwaited");
                orderSettled = true;
                if (ExplorerSnapshotValidator.TryValidate(earlySnapshot, scannedFiles, out var earlyOrder, out _))
                {
                    if (_catalog.ReplaceOrder(earlyOrder))
                    {
                        _sink.OnOrderApplied(earlyOrder.Count, _catalog.CurrentIndex, currentKept: false);
                        perf.Mark("explorerApplied");
                        if (initialPath is null) resumePath = null;
                    }
                    else
                    {
                        perf.Mark("explorerIgnored");
                    }
                }
                else
                {
                    perf.Mark("explorerFallback");
                }
            }
            else if (initialPath is not null)
            {
                // INV-9 (file open): the requested file is presented right away instead of after the
                // snapshot, but navigation/file actions wait for this gate (see PendingOrder), so the
                // first step away from the opened file still follows the Explorer order.
                pendingOrder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingOrder = pendingOrder;
            }

            if (_catalog.Count > 0)
            {
                var resumeIndex = resumePath is null ? 0 : _catalog.IndexOf(resumePath);
                var targetIndex = resumeIndex >= 0 ? resumeIndex : 0;
                _catalog.SetCurrent(targetIndex);
                var presentationGen = _clock.CurrentNavigation;
                perf.Mark("presentStart");
                await _sink.PresentAsync(targetIndex, presentationGen);
                perf.Mark("presentDone");
            }
            else
            {
                _clock.NextNavigation();
                _sink.OnEmpty(folder, session);
            }

            if (orderSettled)
            {
                return;
            }

            var presentationGeneration = _clock.CurrentNavigation;
            var explorerSnapshot = await explorerTask;
            perf.Mark("explorerAwaited");

            if (loadToken.IsCancellationRequested || !_clock.IsFolderCurrent(loadGeneration))
            {
                return;
            }

            if (!_clock.IsInteractionCurrent(interactionGeneration))
            {
                perf.Mark("explorerIgnored");
                return;
            }

            if (ExplorerSnapshotValidator.TryValidate(explorerSnapshot, scannedFiles, out var explorerOrder, out _))
            {
                if (_catalog.ReplaceOrder(explorerOrder))
                {
                    var mayReplaceInitialFallback = initialPath is null && _clock.CurrentNavigation == presentationGeneration
                        && _catalog.Count > 0;
                    _sink.OnOrderApplied(explorerOrder.Count, _catalog.CurrentIndex, currentKept: !mayReplaceInitialFallback);
                    perf.Mark("explorerApplied");

                    if (mayReplaceInitialFallback)
                    {
                        _catalog.SetCurrent(0);
                        await _sink.PresentAsync(0, _clock.CurrentNavigation);
                    }
                }
                else
                {
                    perf.Mark("explorerIgnored");
                }
            }
            else
            {
                perf.Mark("explorerFallback");
            }
        }
        catch (OperationCanceledException) when (loadToken.IsCancellationRequested)
        {
            // Bỏ qua khi loadToken bị hủy
        }
        catch (Exception ex) when (!loadToken.IsCancellationRequested && _clock.IsFolderCurrent(loadGeneration))
        {
            _sink.OnFailed(folder, ex);
        }
        finally
        {
            // Applied, ignored, timed out, failed or superseded: in every case the order is settled
            // for this load, so gated navigation may proceed (it re-reads the catalog afterwards).
            pendingOrder?.TrySetResult();
        }
    }

    /// <summary>
    /// Completes once the Explorer order of the current file-open load has been applied or given up
    /// on; already completed when no such load is pending (folder open, or the snapshot was already
    /// in when the catalog became ready). INV-9: the opened file is presented before the snapshot
    /// arrives, so navigation and file actions await this before they bump the interaction
    /// generation -- otherwise the first key press would make INV-7 discard the Explorer order and
    /// the user would silently review the folder in fallback order.
    /// </summary>
    public Task PendingOrder => _pendingOrder?.Task ?? Task.CompletedTask;

    /// <summary>How long a load waits for Explorer's view order before falling back.</summary>
    internal static readonly TimeSpan ExplorerQueryTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// perf(startup): per-load Folder/FolderInfo tracer. Every call is a no-op (no Stopwatch read,
    /// no allocation beyond this struct) when no PhotoReview-Perf listener is attached.
    /// </summary>
    private readonly struct FolderPerf
    {
        private readonly long _generation;
        private readonly long _start;

        public FolderPerf(long generation)
        {
            _generation = generation;
            _start = PhotoReviewPerf.Log.IsEnabled() ? Stopwatch.GetTimestamp() : 0;
        }

        private bool Enabled => _start != 0 && PhotoReviewPerf.Log.IsEnabled();

        public void Mark(string phase)
        {
            if (Enabled) PhotoReviewPerf.Log.Folder(_generation, phase, PhotoReviewPerf.Ms(_start));
        }

        public void Mark(string phase, long value, string detail = "")
        {
            if (!Enabled) return;
            PhotoReviewPerf.Log.Folder(_generation, phase, PhotoReviewPerf.Ms(_start));
            PhotoReviewPerf.Log.FolderInfo(_generation, phase, value, detail);
        }

        /// <summary>Marks "explorerSnapshot" (+status/count) when the query completes, whoever awaits it.</summary>
        public void TraceExplorer(Task<ExplorerViewSnapshot> explorerTask)
        {
            if (!Enabled) return;
            var self = this;
            // Awaited from a thread-pool context (no SynchronizationContext), so the mark is taken
            // when the query completes, not when the UI thread next gets around to it.
            _ = Task.Run(async () =>
            {
                try
                {
                    var snapshot = await explorerTask;
                    self.Mark("explorerSnapshot", snapshot.OrderedPaths.Count, snapshot.Status.ToString());
                }
                catch (Exception ex)
                {
                    self.Mark("explorerSnapshot", -1, ex.GetType().Name);
                }
            });
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _pendingOrder?.TrySetResult();
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        }
    }
}
