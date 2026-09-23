using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
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
        // perf(startup): Folder(gen, phase, msSinceStart) -- restores the T0..T3 trace the D11
        // analyzer reads (lost in T46d). T2 (first image presented) comes from the first Presented
        // event after "start"; the extra phases here split T0->T2 into its IO/sort/explorer parts.
        var perf = new FolderPerf(loadGeneration);
        perf.Mark("start");

        try
        {
            folder = Path.GetFullPath(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!_fileSystem.DirectoryExists(folder))
            {
                throw new DirectoryNotFoundException($"Không tìm thấy folder: {folder}");
            }

            var entries = await Task.Run(() =>
            {
                // EnumerateFilesWithStat gets Length/LastWriteUtc from the same directory entry
                // used to list the file (see PhysicalFileSystem), so this needs no separate
                // GetFileStat() syscall per file the way EnumerateFiles + GetFileStat did.
                var scanned = _fileSystem.EnumerateFilesWithStat(folder, "*")
                    .Where(f => ImageFileTypes.IsSupported(f.Path))
                    .Select(f => f.Stat is null
                        ? new CatalogEntry(f.Path)
                        : new CatalogEntry(f.Path) { Length = f.Stat.Length, LastWriteUtc = f.Stat.LastWriteUtc })
                    .ToList();
                perf.Mark("scanned", scanned.Count);
                return scanned;
            }, loadToken);
            perf.Mark("scanResumed");

            var sortMode = _settingsStore.Current.ImageSortMode;
            var scannedFiles = entries.Select(e => e.Path).ToArray();
            var totalSourceBytes = entries.Sum(e => e.Length ?? 0L);

            var explorerTask = _explorerOrder.TryGetSnapshotProgressiveAsync(
                folder,
                TimeSpan.FromSeconds(2),
                null,
                16,
                loadToken);
            perf.TraceExplorer(explorerTask);

            entries = await Task.Run(() =>
            {
                // Sort the scanned entries directly so stat metadata travels with each item.
                // The path map preserves the previous first-match behavior for unusual
                // case-variant duplicate paths while avoiding an O(n²) First lookup.
                var sorted = ImageSortService.SortEntries(entries, sortMode);
                var firstByPath = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    firstByPath.TryAdd(entry.Path, entry);
                }

                var result = sorted.Select(entry => firstByPath[entry.Path]).ToList();
                perf.Mark("sorted");
                return result;
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
            _sink.OnCatalogReady(folder, _catalog.Count);
            perf.Mark("catalogReady");

            var interactionGeneration = _clock.CurrentInteraction;
            var resumePath = initialPath ?? session.CurrentPath;

            ExplorerViewSnapshot? explorerSnapshot = null;
            if (initialPath is not null)
            {
                explorerSnapshot = await explorerTask;
                perf.Mark("explorerAwaited");
                if (loadToken.IsCancellationRequested || !_clock.IsFolderCurrent(loadGeneration))
                {
                    return;
                }
            }

            if (loadToken.IsCancellationRequested || !_clock.IsFolderCurrent(loadGeneration))
            {
                return;
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
                _sink.OnEmpty(folder);
            }

            var presentationGeneration = _clock.CurrentNavigation;
            explorerSnapshot ??= await explorerTask;

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
                    _sink.OnOrderApplied(explorerOrder.Count, _catalog.CurrentIndex);
                    perf.Mark("explorerApplied");

                    var mayReplaceInitialFallback = initialPath is null && _clock.CurrentNavigation == presentationGeneration;
                    if (mayReplaceInitialFallback && _catalog.Count > 0)
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
        catch (Exception ex) when (_clock.IsFolderCurrent(loadGeneration))
        {
            _sink.OnFailed(folder, ex);
        }
    }

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
            _ = explorerTask.ContinueWith(
                t => self.Mark("explorerSnapshot",
                    t.Status == TaskStatus.RanToCompletion ? t.Result.OrderedPaths.Count : -1,
                    t.Status == TaskStatus.RanToCompletion ? t.Result.Status.ToString() : t.Status.ToString()),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        }
    }
}
