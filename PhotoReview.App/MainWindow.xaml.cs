using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Text.Json;
using Forms = System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Windows.Interop;
using System.ComponentModel;

namespace PhotoReview.App;

public partial class MainWindow : Window
{
    private readonly List<string> _files = [];
    private int _index = -1;
    private long _generation;
    private AppSettings _settings = AppSettings.Load();
    private readonly OperationJournal _journal = new();
    private readonly SessionStore _sessionStore = new();
    private readonly ThumbnailCache _thumbnailCache = new(persistNewThumbnails: false);
    private SessionState? _session;
    private double _zoom = 1;
    private readonly Stack<(string Source, string Destination)> _moveHistory = [];
    private UndoAction? _lastUndoAction;
    private long _totalSourceBytes;
    private const long FullFolderRamThresholdBytes = AppConstants.ImageCacheCapacityBytes;
    private const double PreloadMemoryLoadLimit = AppConstants.PreloadMemoryLoadLimit;
    private string? _compareSelectedPath;
    private readonly FileHashService _hashService = new();
    private readonly ReviewMetrics _metrics = new();
    private readonly PreviewImageService _previewService;
    private readonly PreloadScheduler _preloadScheduler;
    private readonly ExplorerOrderService _explorerOrder = new();
    private CancellationTokenSource _folderLoadCts = new();
    private long _folderGeneration;
    // Incremented by user navigation and file actions.  An Explorer snapshot
    // started during folder load must never reindex a catalog the user has
    // already interacted with.
    private long _catalogInteractionGeneration;
    private ExplorerViewSnapshot? _lastExplorerSnapshot;
    private bool _placementRestored;
    private int _fileActionInProgress;

    public MainWindow(string? initialPath = null)
    {
        InitializeComponent();
        DpiChanged += MainWindow_DpiChanged;
        _previewService = new PreviewImageService(_metrics, IsOriginalLoadingMode, GetTargetDecodeWidth, AppConstants.ImageCacheCapacityBytes);
        _preloadScheduler = new PreloadScheduler(_previewService, _metrics, () => _files.ToArray(), () => _totalSourceBytes,
            FullFolderRamThresholdBytes, PreloadMemoryLoadLimit);
        _journal.ReconcilePendingOperations();
        foreach (var move in _journal.ReadCommittedMoves())
            if (File.Exists(move.Destination) && !File.Exists(move.Source)) _moveHistory.Push((move.Source, move.Destination!));
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            if (File.Exists(initialPath)) _ = LoadFolderAsync(Path.GetDirectoryName(initialPath)!, initialPath);
            else if (Directory.Exists(initialPath)) _ = LoadFolderAsync(initialPath);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Info("Open folder button clicked");
        using var dialog = new Forms.FolderBrowserDialog { Description = "Chọn folder ảnh để review" };
        if (dialog.ShowDialog(new WindowHandle(this)) == Forms.DialogResult.OK) _ = LoadFolderAsync(dialog.SelectedPath);
    }

    private void Window_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        var paths = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
        var input = DragDropInputService.Parse(paths);
        if (!input.IsValid)
        {
            StatusText.Text = input.Warning ?? "Không có input hợp lệ.";
            e.Handled = true;
            return;
        }

        if (input.Warning is not null) StatusText.Text = input.Warning;
        AppLog.Info($"DragDrop open: kind={input.Kind}, ignored={input.IgnoredPathCount}");
        _ = LoadFolderAsync(input.FolderPath!, input.InitialImagePath);
        e.Handled = true;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Info("Settings button clicked");
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            var previousMode = _settings.LoadingMode;
            _settings = AppSettings.Load();
            UpdateFolderTitle();
            if (!string.Equals(previousMode, _settings.LoadingMode, StringComparison.OrdinalIgnoreCase))
            {
                _preloadScheduler.Cancel();
                if (_index >= 0 && _index < _files.Count)
                    _ = PreloadAroundAsync(_index, _generation);
            }
        }
    }

    private async Task LoadFolderAsync(string folder, string? initialPath = null)
    {
        _folderLoadCts.Cancel();
        _folderLoadCts.Dispose();
        _folderLoadCts = new CancellationTokenSource();
        var loadToken = _folderLoadCts.Token;
        var loadGeneration = Interlocked.Increment(ref _folderGeneration);
        AppLog.Info($"LoadFolder start: {folder}");
        try
        {
            folder = Path.GetFullPath(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Không tìm thấy folder: {folder}");
            UpdateFolderTitle(folder);
            StatusText.Text = "Đang quét folder ảnh…";
            var files = await Task.Run(() => Directory.EnumerateFiles(folder, "*", System.IO.SearchOption.TopDirectoryOnly)
                .Where(ImageFileTypes.IsSupported).ToList(), loadToken);
            var sortMode = _settings.ImageSortMode;
            var scannedFiles = files.ToArray();
            // Collect source sizes while the first Explorer query runs, so the
            // very first preload pass can decide whether to queue the folder.
            var totalBytesTask = Task.Run(() => scannedFiles.Sum(path => { try { return new FileInfo(path).Length; } catch { return 0L; } }), loadToken);
            var lastExplorerProgressLog = 0;
            var explorerProgress = new Progress<ExplorerOrderService.ExplorerQueryProgress>(p =>
            {
                // Do not enqueue one log record per COM item; that turns the
                // diagnostic path into another source of Explorer latency.
                if (p.ItemsRead == p.ItemCount || p.ItemsRead - lastExplorerProgressLog >= 16)
                {
                    lastExplorerProgressLog = p.ItemsRead;
                    AppLog.Info($"Explorer progressive progress items={p.ItemsRead}/{p.ItemCount} comCalls={p.ComCalls}");
                }
            });
            var explorerTask = _explorerOrder.TryGetSnapshotProgressiveAsync(folder, TimeSpan.FromSeconds(2), loadToken, explorerProgress, 16);
            ExplorerViewSnapshot? explorerSnapshot = null;
            files = await Task.Run(() => ImageSortService.Sort(files, sortMode), loadToken);
            if (initialPath is not null)
            {
                var requested = Path.GetFullPath(initialPath);
                var requestedIndex = files.FindIndex(path => string.Equals(path, requested, StringComparison.OrdinalIgnoreCase));
                if (requestedIndex > 0)
                {
                    // Opening a file is an explicit user selection. Keep it at
                    // position 1 in the provisional catalog so the first frame
                    // and counter are immediately consistent, even if Explorer
                    // order arrives later or is skipped after interaction.
                    var selected = files[requestedIndex];
                    files.RemoveAt(requestedIndex);
                    files.Insert(0, selected);
                }
            }
            if (loadToken.IsCancellationRequested || loadGeneration != _folderGeneration) return;
            AppLog.Info($"LoadFolder scan complete: {files.Count} files, initialSort={sortMode}");
            _preloadScheduler.Cancel();
            _totalSourceBytes = long.MaxValue;
            _files.Clear(); _files.AddRange(files); _index = -1;
            _previewService.ClearCache();
            _preloadScheduler.ClearPreloadedKeys();
            _hashService.Clear(); _previewService.ClearOriginalDimensions();
            _session = _sessionStore.Load(folder);
            FolderText.Text = $"{folder}  ({_files.Count} ảnh)";
            var interactionGeneration = Volatile.Read(ref _catalogInteractionGeneration);
            var resumePath = initialPath ?? _session.CurrentPath;
            if (initialPath is not null)
            {
                // A direct file open must wait for the complete Explorer snapshot
                // before presenting the first frame. This prevents a provisional
                // fallback index from flashing before native order is known.
                explorerSnapshot = await explorerTask;
                if (loadToken.IsCancellationRequested || loadGeneration != _folderGeneration) return;
            }
            _totalSourceBytes = await totalBytesTask;
            if (loadToken.IsCancellationRequested || loadGeneration != _folderGeneration) return;
            if (_files.Count > 0)
            {
                var resumeIndex = resumePath is null ? 0 : _files.FindIndex(p => string.Equals(p, Path.GetFullPath(resumePath), StringComparison.OrdinalIgnoreCase));
                await ShowImageAsync(resumeIndex >= 0 ? resumeIndex : 0);
            }
            else { MainImage.Source = null; StatusText.Text = "Không tìm thấy ảnh hỗ trợ trong folder này."; }
            // Captured after the provisional frame is presented (ShowImageAsync bumps
            // _generation as its very first step), so this reflects "no navigation has
            // happened since presenting" rather than always mismatching.
            var presentationGeneration = _generation;
            explorerSnapshot ??= await explorerTask;
            if (loadToken.IsCancellationRequested || loadGeneration != _folderGeneration) return;
            if (interactionGeneration != Volatile.Read(ref _catalogInteractionGeneration))
            {
                AppLog.Info($"Explorer native order ignored after catalog interaction: loadInteraction={interactionGeneration} currentInteraction={_catalogInteractionGeneration}");
                _totalSourceBytes = await totalBytesTask;
                return;
            }
            _lastExplorerSnapshot = explorerSnapshot;
            if (ExplorerSnapshotValidator.TryValidate(explorerSnapshot, scannedFiles, out var explorerOrder, out var fallbackReason))
            {
                var currentSet = new HashSet<string>(_files, StringComparer.OrdinalIgnoreCase);
                if (currentSet.Count != scannedFiles.Length || !currentSet.SetEquals(scannedFiles))
                {
                    AppLog.Info("Explorer native order ignored because the catalog changed while the snapshot was loading");
                    _totalSourceBytes = await totalBytesTask;
                    return;
                }
                var currentPath = _index >= 0 && _index < _files.Count ? _files[_index] : null;
                var mayReplaceInitialFallback = initialPath is null && _generation == presentationGeneration;
                _preloadScheduler.Cancel();
                _files.Clear(); _files.AddRange(explorerOrder);
                _index = currentPath is null ? -1 : _files.FindIndex(path => string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase));
                FolderText.Text = $"{folder}  ({_files.Count} ảnh) · Explorer";
                if (mayReplaceInitialFallback && _files.Count > 0) await ShowImageAsync(0);
                else if (_index >= 0)
                {
                    // Reindex the catalog without decoding/presenting the same path
                    // a second time. The counter is updated immediately.
                    StatusText.Text = $"{_index + 1}/{_files.Count}";
                    // The provisional catalog's preload was canceled above;
                    // resume against the complete Explorer order from this path.
                    _ = PreloadAroundAsync(_index, _generation);
                }
                AppLog.Info($"Explorer native order applied: {explorerOrder.Count} files, currentIndex={_index}, currentPath={currentPath}");
            }
            else AppLog.Info($"Explorer view fallback: status={explorerSnapshot.Status}, reason={fallbackReason}");
            _totalSourceBytes = await totalBytesTask;
        }
        catch (OperationCanceledException) when (loadToken.IsCancellationRequested) { }
        catch (Exception ex) when (loadGeneration == _folderGeneration)
        {
            AppLog.Error($"LoadFolder failed: {folder}", ex);
            MainImage.Source = null;
            FolderText.Text = folder;
            UpdateFolderTitle(folder);
            StatusText.Text = $"Không mở được folder: {ex.Message}";
        }
        // Superseded by a newer folder load: don't touch UI state for a stale
        // request, but still log — otherwise this exception is unobserved once
        // the discarded fire-and-forget task (`_ = LoadFolderAsync(...)`) is
        // garbage collected.
        catch (Exception ex) { AppLog.Error($"LoadFolder failed (stale generation={loadGeneration}, current={_folderGeneration}): {folder}", ex); }
    }

    private void UpdateFolderTitle(string? folder = null)
    {
        folder ??= FolderText.Text;
        if (string.IsNullOrWhiteSpace(folder)) return;
        // Keeps the native title format used by: Title = $"Photo Review — {folder}"
        Title = $"Photo Review — {folder}{(AppLog.Enabled ? " · LOG" : "")}";
    }

    private async Task ShowImageAsync(int index)
    {
        if (index < 0 || index >= _files.Count) return;
        var presentStopwatch = Stopwatch.StartNew();
        _index = index; var path = _files[index]; var token = Interlocked.Increment(ref _generation);
        _compareSelectedPath = null;
        if (AppLog.Enabled) AppLog.Info($"ShowImage start index={index} count={_files.Count} token={token} path={path}");
        // The catalog can become stale while Explorer order is being applied or an
        // external move/delete completes. Do this check before touching FileInfo.Length
        // so a vanished item is removed and the viewer advances once without logging
        // a misleading ShowImage failure.
        if (!TryGetCurrentFileInfo(path, out var initialInfo))
        {
            await RemoveMissingCatalogItemAsync(path, index, token);
            return;
        }
        var initialSize = initialInfo.Length;
        var currentKey = GetCurrentCacheKey(initialInfo);
        var ramReady = TryGetCachedPreview(currentKey, out var readyBitmap);
        if (ramReady)
        {
            if (_preloadScheduler.TryConsumePreloadedKey(currentKey)) _metrics.RecordPreloadHit();
        }
        StatusText.Text = ramReady
            ? $"{index + 1}/{_files.Count} · {FormatFileSize(initialSize)} · {Path.GetFileName(path)}"
            : $"{index + 1}/{_files.Count} · {FormatFileSize(initialSize)} · Đang tải";
        if (AppLog.Enabled) AppLog.Info($"ShowImage cache-state token={token} path={path} ramReady={ramReady} cacheBytes={_previewService.CacheBytes}");
        try
        {
            if (string.Equals(_settings.LoadingMode, "Preview", StringComparison.OrdinalIgnoreCase)
                && !ramReady && !HasInflightPreview(path))
            {
                var thumbnail = await _thumbnailCache.GetAsync(path);
                if (token != _generation) return;
                MainImage.Source = thumbnail;
                if (AppLog.Enabled) AppLog.Info($"ShowImage thumbnail-presented token={token} path={path}");
                ApplyInitialViewMode();
                StatusText.Text = $"{index + 1}/{_files.Count} · {FormatFileSize(initialSize)} · Đang tải bản rõ";
            }
            var image = ramReady ? readyBitmap : await GetPreviewAsync(path, currentKey);
            if (ramReady) _metrics.RecordCacheHit();
            if (token != _generation) return;
            var uiAssign = Stopwatch.StartNew();
            MainImage.Source = image;
            _metrics.RecordUiAssign(uiAssign.ElapsedMilliseconds);
            if (AppLog.Enabled) AppLog.Info($"ShowImage preview-presented token={token} path={path} mode={_settings.LoadingMode}");
            _ = PreloadAroundAsync(index, token);
            var pair = FindComparePair(path);
            ComparePanel.Visibility = pair is null ? Visibility.Collapsed : Visibility.Visible;
            if (pair is not null)
            {
                MainImage.Source = null;
                CompareLeftImage.Tag = pair.Value.Left;
                CompareRightImage.Tag = pair.Value.Right;
                var comparePreviews = await Task.WhenAll(GetPreviewAsync(pair.Value.Left), GetPreviewAsync(pair.Value.Right));
                if (token != _generation) return;
                CompareLeftImage.Source = comparePreviews[0];
                CompareRightImage.Source = comparePreviews[1];
                _compareSelectedPath = path;
                UpdateCompareSelection();
                var leftSize = "";
                var rightSize = "";
                if (_settings.CompareSizeEnabled)
                {
                    var leftInfo = new FileInfo(pair.Value.Left);
                    var rightInfo = new FileInfo(pair.Value.Right);
                    leftSize = $" ({leftInfo.Length:N0} B)";
                    rightSize = $" ({rightInfo.Length:N0} B)";
                }
                var hashText = " | hash tắt";
                if (_settings.CompareHashEnabled)
                {
                    var hashes = await Task.WhenAll(GetHashAsync(pair.Value.Left), GetHashAsync(pair.Value.Right));
                    if (token != _generation) return;
                    hashText = $" | hash {(hashes[0] == hashes[1] ? "TRÙNG" : "KHÁC")}";
                }
                StatusText.Text = $"{index + 1}/{_files.Count} | Compare | {Path.GetFileName(pair.Value.Left)}{leftSize} ↔ {Path.GetFileName(pair.Value.Right)}{rightSize}{hashText} | click để chọn";
            }
            if (pair is null)
            {
                ApplyInitialViewMode();
                var original = IsOriginalLoadingMode()
                    ? (Width: image.PixelWidth, Height: image.PixelHeight)
                    : await GetOriginalDimensionsAsync(path);
                if (token != _generation) return;
                var currentInfo = new FileInfo(path);
                if (!currentInfo.Exists) return;
                StatusText.Text = $"{index + 1}/{_files.Count} · {FormatFileSize(currentInfo.Length)} · {original.Width}×{original.Height} · {Path.GetFileName(path)}";
            }
            if (token != _generation) return;
            if (_session is not null) { _session.CurrentPath = path; _session.UpdatedUtc = DateTime.UtcNow; _sessionStore.Save(_session); }
            presentStopwatch.Stop();
            _metrics.RecordPresented(presentStopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (token == _generation && (ex is FileNotFoundException || ex is DirectoryNotFoundException))
        {
            if (AppLog.Enabled) AppLog.Info($"ShowImage stale-file token={token} path={path}");
            await RemoveMissingCatalogItemAsync(path, index, token);
        }
        catch (Exception ex) when (token == _generation) { AppLog.Error($"ShowImage failed token={token} index={index} path={path}", ex); StatusText.Text = $"Lỗi ảnh: {Path.GetFileName(path)} — {ex.Message}"; }
        // Superseded by a newer navigation: don't touch UI state for a stale request,
        // but still log — otherwise this exception is unobserved once the discarded
        // fire-and-forget task (`_ = ShowImageAsync(...)`) is garbage collected.
        catch (Exception ex) { AppLog.Error($"ShowImage failed (stale token={token}, current={_generation}) index={index} path={path}", ex); }
    }

    private void FitImage_Click(object sender, RoutedEventArgs e) => ResetFitView();

    private static bool TryGetCurrentFileInfo(string path, out FileInfo info)
    {
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) { info = null!; return false; }
            return true;
        }
        catch (FileNotFoundException) { info = null!; return false; }
        catch (DirectoryNotFoundException) { info = null!; return false; }
    }

    private async Task RemoveMissingCatalogItemAsync(string path, int index, long token)
    {
        if (token != _generation) return;
        var removedIndex = _files.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (removedIndex < 0) return;
        _files.RemoveAt(removedIndex);
        if (_files.Count == 0)
        {
            _index = -1;
            MainImage.Source = null;
            StatusText.Text = "Không còn ảnh trong thư mục";
            return;
        }
        var nextIndex = Math.Min(Math.Max(removedIndex, 0), _files.Count - 1);
        _index = nextIndex;
        await ShowImageAsync(nextIndex);
    }

    private bool IsOriginalLoadingMode() => string.Equals(_settings.LoadingMode, "Original", StringComparison.OrdinalIgnoreCase);

    private ImageCacheKey GetCurrentCacheKey(string path) => _previewService.GetCurrentCacheKey(path);

    private ImageCacheKey GetCurrentCacheKey(FileInfo info) => _previewService.GetCurrentCacheKey(info);

    private Task<BitmapImage> GetPreviewAsync(string path) => _previewService.GetPreviewAsync(path);

    private Task<BitmapImage> GetPreviewAsync(string path, ImageCacheKey key) => _previewService.GetPreviewAsync(path, key);

    private bool TryGetCachedPreview(string path, out BitmapImage bitmap) => _previewService.TryGetCachedPreview(path, out bitmap);

    private bool TryGetCachedPreview(ImageCacheKey key, out BitmapImage bitmap) => _previewService.TryGetCachedPreview(key, out bitmap);

    private bool HasInflightPreview(string path) => _previewService.HasInflightPreview(path);

    private void EvictCachedPath(string path)
        => _previewService.EvictCachedPath(path, normalized =>
            _preloadScheduler.RemovePreloadedKeysForPath(normalized));

    private sealed class WindowHandle(Window window) : Forms.IWin32Window
    {
        public IntPtr Handle => new WindowInteropHelper(window).Handle;
    }

    private Task<(int Width, int Height)> GetOriginalDimensionsAsync(string path) => _previewService.GetOriginalDimensionsAsync(path);

    private double? _cachedDpiScale;

    // DPI only changes when the window moves to a monitor with a different scale
    // factor; caching it avoids walking the visual tree on every navigation.
    private void MainWindow_DpiChanged(object sender, System.Windows.DpiChangedEventArgs e) => _cachedDpiScale = e.NewDpi.DpiScaleX;

    private int GetTargetDecodeWidth()
    {
        var viewport = ImageScroll.ActualWidth > 1 ? ImageScroll.ActualWidth : 2200;
        var dpi = _cachedDpiScale ??= PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        return AdaptivePreviewPolicy.CalculateTargetDecodeWidth(viewport, dpi, 1.15);
    }

    private Task PreloadAroundAsync(int center, long token)
    {
        // A navigation that has already been superseded must not re-prioritize preload.
        if (token != _generation) return Task.CompletedTask;
        return _preloadScheduler.PreloadAroundAsync(center);
    }

    private async void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var pressedKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Matches(pressedKey, _settings.Shortcuts.Fullscreen))
        {
            e.Handled = true; ToggleFullscreen(); return;
        }
        if (e.Key == Key.Escape && WindowStyle == WindowStyle.None)
        {
            e.Handled = true; ExitFullscreen(); return;
        }
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        if (Matches(e.Key, _settings.Shortcuts.NextFolder) || Matches(e.Key, _settings.Shortcuts.PreviousFolder))
        {
            e.Handled = true;
            var nextFolder = Matches(e.Key, _settings.Shortcuts.NextFolder);
            await NavigateSiblingFolderAsync(nextFolder ? 1 : -1);
            return;
        }
        if (Matches(e.Key, _settings.Shortcuts.FirstImage))
        {
            if (_files.Count == 0) return;
            e.Handled = true;
            Interlocked.Increment(ref _catalogInteractionGeneration);
            await ShowImageAsync(0);
            return;
        }
        if (Matches(e.Key, _settings.Shortcuts.Undo) && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await UndoLastMoveAsync();
            return;
        }
        if (_index < 0) return;
        if (Matches(e.Key, _settings.Shortcuts.Compare))
        {
            if (ComparePanel.Visibility != Visibility.Visible && FindComparePair(_files[_index]) is null) return;
            e.Handled = true;
            ComparePanel.Visibility = ComparePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        foreach (var action in _settings.Actions)
        {
            if (Matches(e.Key, action.Shortcut)) { e.Handled = true; await ExecuteActionAsync(action); return; }
        }
        if (Matches(e.Key, _settings.Shortcuts.SendToRecycleBin)) { e.Handled = true; await ClassifyCurrentAsync(3); return; }
        if (Matches(e.Key, _settings.Shortcuts.Skip)) { e.Handled = true; Interlocked.Increment(ref _catalogInteractionGeneration); if (_session is not null) { _session.Skipped.Add(_files[_index]); _session.UpdatedUtc = DateTime.UtcNow; _sessionStore.Save(_session); } await ShowImageAsync(Math.Min(_index + 1, _files.Count - 1)); return; }
        if (Matches(e.Key, _settings.Shortcuts.ToggleFit)) { e.Handled = true; ResetFitView(); return; }
        if (Matches(e.Key, _settings.Shortcuts.ZoomIn)) { e.Handled = true; SetZoom(Math.Min(_zoom + .25, 4)); return; }
        if (Matches(e.Key, _settings.Shortcuts.ZoomOut)) { e.Handled = true; SetZoom(Math.Max(_zoom - .25, .25)); return; }
        if (Matches(e.Key, _settings.Shortcuts.Next)) { e.Handled = true; Interlocked.Increment(ref _catalogInteractionGeneration); await ShowImageAsync(Math.Min(_index + 1, _files.Count - 1)); }
        if (Matches(e.Key, _settings.Shortcuts.Previous)) { e.Handled = true; Interlocked.Increment(ref _catalogInteractionGeneration); await ShowImageAsync(Math.Max(_index - 1, 0)); }
    }

    private void Recovery_Click(object sender, RoutedEventArgs e)
    {
        var entries = _journal.ReadPendingOperations().Concat(_journal.ReadFailedOperations()).ToList();
        var dialog = new RecoveryWindow(entries, entry => RecoveryRetryService.RetryMoveOrCopy(entry, _journal)) { Owner = this };
        dialog.ShowDialog();
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DiagnosticsWindow(_metrics.Snapshot(), _lastExplorerSnapshot) { Owner = this };
        dialog.ShowDialog();
    }

    private void Benchmark_Click(object sender, RoutedEventArgs e)
    {
        var folder = _files.Count > 0 ? Path.GetDirectoryName(_files[Math.Clamp(_index, 0, _files.Count - 1)]) : null;
        var dialog = new BenchmarkWindow(folder) { Owner = this };
        dialog.Show();
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(this, "Xóa toàn bộ cache preview? Ảnh nguồn không bị thay đổi.", "Xác nhận xóa cache", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        _preloadScheduler.Cancel();
        _thumbnailCache.ClearDisk();
        _thumbnailCache.ClearMemory();
        _previewService.ClearCache();
        _preloadScheduler.ClearPreloadedKeys();
        StatusText.Text = "Đã xóa cache preview.";
    }

    private (string Left, string Right)? FindComparePair(string path)
        => ComparePairService.Find(_files, path);

    private void UpdateCompareSelection()
    {
        CompareLeftBorder.BorderBrush = string.Equals(_compareSelectedPath, CompareLeftImage.Tag as string, StringComparison.OrdinalIgnoreCase) ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Gray;
        CompareRightBorder.BorderBrush = string.Equals(_compareSelectedPath, CompareRightImage.Tag as string, StringComparison.OrdinalIgnoreCase) ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Gray;
    }

    private void CompareLeft_Click(object sender, MouseButtonEventArgs e) { _compareSelectedPath = CompareLeftImage.Tag as string; UpdateCompareSelection(); e.Handled = true; }
    private void CompareRight_Click(object sender, MouseButtonEventArgs e) { _compareSelectedPath = CompareRightImage.Tag as string; UpdateCompareSelection(); e.Handled = true; }
    private void CompareLeft_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key is Key.Enter or Key.Space) { _compareSelectedPath = CompareLeftImage.Tag as string; UpdateCompareSelection(); e.Handled = true; } }
    private void CompareRight_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key is Key.Enter or Key.Space) { _compareSelectedPath = CompareRightImage.Tag as string; UpdateCompareSelection(); e.Handled = true; } }

    private async void RemoveNumberedDuplicates_Click(object sender, RoutedEventArgs e) => await RemoveDuplicatesAsync(true);
    private async void RemoveOriginalDuplicates_Click(object sender, RoutedEventArgs e) => await RemoveDuplicatesAsync(false);

    private async Task RemoveDuplicatesAsync(bool removeNumbered)
    {
        var remove = new List<string>();
        // Batch work may race with an in-flight viewer decode or another action.
        // Snapshot only files that still have readable metadata; a file can
        // disappear between enumeration and this pass.
        var candidates = _files.ToArray();
        var sizeGroups = candidates
            .Select(path => { try { return (Path: path, Size: new FileInfo(path).Length); } catch { return (Path: path, Size: -1L); } })
            .Where(item => item.Size >= 0)
            .GroupBy(item => item.Size)
            .Where(group => group.Count() > 1)
            .Select(group => group.Select(item => item.Path).ToList())
            .ToList();
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in sizeGroups.SelectMany(group => group))
        {
            try
            {
                var hash = await GetHashAsync(path);
                if (!groups.TryGetValue(hash, out var group)) groups[hash] = group = [];
                group.Add(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var group in groups.Values.Where(group => group.Count > 1))
            remove.AddRange(group.Where(path => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(path), " \\(\\d+\\)$") == removeNumbered));
        if (remove.Count == 0) { StatusText.Text = "Không có duplicate cùng hash phù hợp."; return; }
        var review = new BatchReviewWindow(remove) { Owner = this };
        if (review.ShowDialog() != true) { StatusText.Text = "Đã hủy xử lý hàng loạt."; return; }
        var failures = new List<string>();
        var succeeded = 0;
        StopImageReadsForAction();
        foreach (var path in remove)
        {
            if (!File.Exists(path)) { failures.Add($"Không còn tồn tại: {path}"); continue; }
            FileInfo info;
            try { info = new FileInfo(path); if (!info.Exists) { failures.Add($"Không còn tồn tại: {path}"); continue; } }
            catch (Exception ex) { failures.Add($"{Path.GetFileName(path)}: {ex.Message}"); continue; }
            var operationId = Guid.NewGuid().ToString("N");
            _journal.Append(new JournalEntry(operationId, "Recycle", "Prepared", path, null, info.Length, info.LastWriteTimeUtc, DateTime.UtcNow));
            try
            {
                await Task.Run(() => FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin));
                _journal.Append(new JournalEntry(operationId, "Recycle", "Committed", path, null, info.Length, info.LastWriteTimeUtc, DateTime.UtcNow));
                succeeded++;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
                _journal.Append(new JournalEntry(operationId, "Recycle", "Failed", path, null, info.Length, info.LastWriteTimeUtc, DateTime.UtcNow, ex.Message));
            }
        }
        StatusText.Text = $"Batch hoàn tất: {succeeded} thành công, {failures.Count} lỗi.";
        if (failures.Count > 0) System.Windows.MessageBox.Show(this, string.Join(Environment.NewLine, failures), "Báo cáo lỗi batch", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (succeeded > 0) await LoadFolderAsync(_session?.Folder ?? Path.GetDirectoryName(_files[0])!);
    }

    private Task<string> GetHashAsync(string path) => _hashService.GetAsync(path);

    private async Task NavigateSiblingFolderAsync(int direction)
    {
        if (_session is null) return;
        var currentFolder = Path.GetFullPath(_session.Folder);
        var targetFolder = await Task.Run(() => FindNextImageFolder(currentFolder, direction));
        if (targetFolder is null)
        {
            StatusText.Text = direction > 0 ? "Đã ở folder cuối cùng cùng cấp." : "Đã ở folder đầu tiên cùng cấp.";
            return;
        }

        await LoadFolderAsync(targetFolder);
    }

    private static string? FindNextImageFolder(string currentFolder, int direction)
    {
        var folders = SiblingFolderService.GetSorted(currentFolder);
        var index = folders.ToList().FindIndex(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentFolder), StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        for (var i = index + direction; i >= 0 && i < folders.Count; i += direction)
        {
            try
            {
                if (Directory.EnumerateFiles(folders[i], "*", System.IO.SearchOption.TopDirectoryOnly).Any(ImageFileTypes.IsSupported)) return folders[i];
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private void ToggleFullscreen()
    {
        if (WindowStyle == WindowStyle.None) ExitFullscreen();
        else
        {
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
    }

    private void ExitFullscreen()
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        WindowState = WindowState.Normal;
        WindowState = WindowState.Maximized;
    }

    private static bool Matches(Key key, string configured) => Enum.TryParse<Key>(configured, true, out var parsed) && key == parsed;

    private void ImageScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control) { SetZoom(Math.Clamp(_zoom + (e.Delta > 0 ? .25 : -.25), .25, 4)); e.Handled = true; }
    }

    private void ImageScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateFitSize();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_placementRestored)
        {
            _placementRestored = true;
            WindowPlacementService.Restore(this);
        }
        UpdateFitSize();
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private async void UndoLastAction_Click(object sender, RoutedEventArgs e) => await UndoLastActionAsync();
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFitSize();
    private void Window_Closing(object? sender, CancelEventArgs e) => WindowPlacementService.Save(this);
    private void Window_Closed(object? sender, EventArgs e)
    {
        _preloadScheduler.Dispose();
        _folderLoadCts.Cancel();
        _folderLoadCts.Dispose();
        _thumbnailCache.Dispose();
        _hashService.Clear();
        _explorerOrder.Dispose();
    }

    private void UpdateFitSize()
    {
        if (!string.Equals(_settings.InitialViewMode, "Fit", StringComparison.OrdinalIgnoreCase)) return;
        var width = ImageScroll.ActualWidth - ImageScroll.BorderThickness.Left - ImageScroll.BorderThickness.Right;
        var height = ImageScroll.ActualHeight - ImageScroll.BorderThickness.Top - ImageScroll.BorderThickness.Bottom;
        if (width > 1 && height > 1)
        {
            MainImage.Width = double.NaN;
            MainImage.Height = double.NaN;
            MainImage.MaxWidth = width;
            MainImage.MaxHeight = height;
        }
    }

    private void SetZoom(double value)
    {
        MainImage.Stretch = System.Windows.Media.Stretch.None;
        MainImage.Width = double.NaN; MainImage.Height = double.NaN;
        MainImage.MaxWidth = double.PositiveInfinity; MainImage.MaxHeight = double.PositiveInfinity;
        _zoom = value; ImageScale.ScaleX = value; ImageScale.ScaleY = value;
        if (_index >= 0) StatusText.Text = $"{_index + 1}/{_files.Count} · {FormatFileSize(new FileInfo(_files[_index]).Length)} · Zoom {_zoom:0.##}x";
    }

    private void ApplyInitialViewMode()
    {
        if (string.Equals(_settings.InitialViewMode, "Fit", StringComparison.OrdinalIgnoreCase))
        {
            _zoom = 1;
            ImageScale.ScaleX = 1; ImageScale.ScaleY = 1;
            MainImage.Stretch = System.Windows.Media.Stretch.Uniform;
            MainImage.Width = double.NaN; MainImage.Height = double.NaN;
        }
        else
        {
            SetZoom(_settings.InitialViewMode switch { "200%" => 2, "400%" => 4, _ => 1 });
        }
    }

    private async Task ClassifyCurrentAsync(int category)
    {
        if (_index < 0 || _index >= _files.Count) return;
        if (Interlocked.Exchange(ref _fileActionInProgress, 1) != 0) return;
        var sourcePath = _compareSelectedPath ?? _files[_index];
        var source = sourcePath;
        var sourceIndex = _files.FindIndex(p => string.Equals(p, sourcePath, StringComparison.OrdinalIgnoreCase));
        StopImageReadsForAction();
        // StopImageReadsForAction() just bumped _folderGeneration; capture it so every
        // catalog/session/status mutation below (after the Task.Run await yields the UI
        // thread) can check the user hasn't opened a different folder in the meantime.
        var folderGeneration = _folderGeneration;
        var nextPath = await AdvanceBeforeFileActionAsync(sourcePath, removeSource: true);
        AppLog.Info($"FileAction classify-start category={category} source={sourcePath} next={nextPath ?? "<none>"}");
        try
        {
            var info = new FileInfo(source);
            if (category == 3)
            {
                var operationId = Guid.NewGuid().ToString("N");
                _journal.Append(new JournalEntry(operationId, "Recycle", "Prepared", source, null, info.Length, info.LastWriteTimeUtc, DateTime.UtcNow));
                await Task.Run(() => FileSystem.DeleteFile(source, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin));
                AppLog.Info($"FileAction recycle-complete source={source}");
                _journal.Append(new JournalEntry(operationId, "Recycle", "Committed", source, null, info.Length, info.LastWriteTimeUtc, DateTime.UtcNow));
                _lastUndoAction = new UndoAction("RecycleBin", source, null, info.Length, info.LastWriteTimeUtc);
            }
            else
            {
                var folderName = _settings.Folder2Name;
                var destinationFolder = Path.IsPathRooted(folderName) ? folderName : Path.Combine(Path.GetDirectoryName(source)!, folderName);
                Directory.CreateDirectory(destinationFolder);
                var destination = Path.Combine(destinationFolder, Path.GetFileName(source));
                if (File.Exists(destination)) throw new IOException($"Đích đã tồn tại: {destination}");
                var operationId = Guid.NewGuid().ToString("N");
                _journal.Append(new JournalEntry(operationId, "Move", "Prepared", source, destination, info.Length, info.LastWriteTimeUtc, DateTime.UtcNow));
                await Task.Run(() => File.Move(source, destination));
                var movedInfo = new FileInfo(destination);
                if (movedInfo.Length != info.Length) throw new IOException("Kiểm tra sau Move thất bại: kích thước thay đổi.");
                _journal.Append(new JournalEntry(operationId, "Move", "Committed", source, destination, info.Length, info.LastWriteTimeUtc, DateTime.UtcNow));
                _moveHistory.Push((source, destination));
                _lastUndoAction = new UndoAction("Move", source, destination, info.Length, info.LastWriteTimeUtc);
            }
            if (folderGeneration != _folderGeneration)
                AppLog.Info($"FileAction completion ignored after folder switch: source={source}");
            else
            {
                if (_session is not null) { _session.CurrentPath = _files.Count == 0 ? null : _files[Math.Min(_index, _files.Count - 1)]; _session.UpdatedUtc = DateTime.UtcNow; _sessionStore.Save(_session); }
                if (_files.Count == 0) StatusText.Text = "Đã xử lý hết ảnh trong folder.";
            }
        }
        catch (Exception ex)
        {
            if (folderGeneration != _folderGeneration)
                AppLog.Info($"FileAction failure ignored after folder switch: source={source}, error={ex.Message}");
            else
            {
                // The catalog is advanced optimistically before the synchronous
                // filesystem call. Restore the source when the operation fails so
                // a failed Delete/Move does not silently lose the image from view.
                if (File.Exists(source) && !_files.Contains(source, StringComparer.OrdinalIgnoreCase))
                {
                    var restoreIndex = Math.Clamp(sourceIndex < 0 ? _files.Count : sourceIndex, 0, _files.Count);
                    _files.Insert(restoreIndex, source);
                }
                StatusText.Text = $"Không xử lý được {Path.GetFileName(source)}: {ex.Message}";
            }
        }
        finally { Volatile.Write(ref _fileActionInProgress, 0); }
    }

    private void ResetFitView()
    {
        _zoom = 1;
        ImageScale.ScaleX = 1; ImageScale.ScaleY = 1;
        MainImage.Stretch = System.Windows.Media.Stretch.Uniform;
        MainImage.Width = double.NaN; MainImage.Height = double.NaN;
        UpdateFitSize();
    }

    private void StopImageReadsForAction()
    {
        // Do not await decode/preload tasks here: the file action must start now.
        // Generation invalidation prevents any late bitmap from being presented.
        Interlocked.Increment(ref _generation);
        // Invalidate an Explorer snapshot/load that started before the action.
        Interlocked.Increment(ref _folderGeneration);
        Interlocked.Increment(ref _catalogInteractionGeneration);
        _preloadScheduler.Cancel();
        AppLog.Info($"FileAction stop-reads generation={_generation} folderGeneration={_folderGeneration} index={_index}");
        // Keep the current frame visible while Move/Delete runs. Clearing the
        // source here creates a black flash before the next image is ready.
    }

    private async Task ExecuteActionAsync(ReviewAction action)
    {
        if (action.Operation is not ("Move" or "Copy" or "Recycle" or "Delete"))
        {
            StatusText.Text = $"Không thực hiện được {action.Name}: Operation không hợp lệ.";
            return;
        }
        if (action.Confirm)
        {
            var answer = System.Windows.MessageBox.Show(this, $"Thực hiện action '{action.Name}' trên ảnh hiện tại?", "Xác nhận action", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;
        }
        if (action.Operation.Equals("Recycle", StringComparison.OrdinalIgnoreCase) || action.Operation.Equals("Delete", StringComparison.OrdinalIgnoreCase))
        {
            await ClassifyCurrentAsync(3);
            return;
        }
        if (_index < 0 || _index >= _files.Count) return;
        if (Interlocked.Exchange(ref _fileActionInProgress, 1) != 0) return;
        StopImageReadsForAction();
        // StopImageReadsForAction() just bumped _folderGeneration; capture it so every
        // catalog/status mutation below (after the Task.Run await yields the UI thread)
        // can check the user hasn't opened a different folder in the meantime.
        var folderGeneration = _folderGeneration;
        var sourcePath = _compareSelectedPath ?? _files[_index];
        var source = sourcePath;
        var operation = action.Operation.Equals("Copy", StringComparison.OrdinalIgnoreCase) ? "Copy" : "Move";
        var sourceIndex = _files.FindIndex(p => string.Equals(p, sourcePath, StringComparison.OrdinalIgnoreCase));
        var nextPath = await AdvanceBeforeFileActionAsync(sourcePath, removeSource: operation == "Move");
        AppLog.Info($"FileAction action-start operation={operation} source={sourcePath} next={nextPath ?? "<none>"}");
        var operationId = Guid.NewGuid().ToString("N");
        var prepared = false;
        string? destinationPath = null;
        long sourceSize = 0;
        var sourceLastWriteUtc = DateTime.MinValue;
        try
        {
            if (string.IsNullOrWhiteSpace(action.Destination)) throw new IOException("Action chưa có thư mục đích.");
            var destinationFolder = Path.IsPathRooted(action.Destination) ? action.Destination : Path.Combine(Path.GetDirectoryName(source)!, action.Destination);
            destinationFolder = Path.GetFullPath(destinationFolder);
            var sourceFolder = Path.GetFullPath(Path.GetDirectoryName(source)!);
            if (IsSamePath(destinationFolder, sourceFolder)) throw new IOException("Không thể Move/Copy vào chính folder nguồn.");
            Directory.CreateDirectory(destinationFolder);
            destinationPath = Path.Combine(destinationFolder, Path.GetFileName(source));
            if (File.Exists(destinationPath)) throw new IOException($"Đích đã tồn tại: {destinationPath}");
            var sourceInfo = new FileInfo(source);
            sourceSize = sourceInfo.Length;
            sourceLastWriteUtc = sourceInfo.LastWriteTimeUtc;
            _journal.Append(new JournalEntry(operationId, operation, "Prepared", source, destinationPath, sourceSize, sourceLastWriteUtc, DateTime.UtcNow));
            prepared = true;
            if (operation == "Copy") await Task.Run(() => File.Copy(source, destinationPath));
            else await Task.Run(() => File.Move(source, destinationPath));
            AppLog.Info($"FileAction filesystem-complete operation={operation} source={source} destination={destinationPath}");
            var destinationInfo = new FileInfo(destinationPath);
            if (!destinationInfo.Exists || destinationInfo.Length != sourceSize)
                throw new IOException("Kiểm tra sau thao tác thất bại: kích thước đích thay đổi.");
            _journal.Append(new JournalEntry(operationId, operation, "Committed", source, destinationPath, sourceSize, sourceLastWriteUtc, DateTime.UtcNow));
            if (folderGeneration != _folderGeneration)
                AppLog.Info($"FileAction completion ignored after folder switch: operation={operation} source={source}");
            else if (_files.Count == 0) StatusText.Text = $"Đã thực hiện: {action.Name}";
        }
        catch (Exception ex)
        {
            if (prepared) _journal.Append(new JournalEntry(operationId, operation, "Failed", source, destinationPath, sourceSize, sourceLastWriteUtc, DateTime.UtcNow, ex.Message));
            if (folderGeneration != _folderGeneration)
                AppLog.Info($"FileAction failure ignored after folder switch: operation={operation} source={source}, error={ex.Message}");
            else
            {
                if (operation == "Move" && !_files.Contains(source, StringComparer.OrdinalIgnoreCase) && File.Exists(source))
                {
                    var restoreIndex = Math.Min(sourceIndex, _files.Count);
                    _files.Insert(restoreIndex, source);
                }
                StatusText.Text = $"Không thực hiện được {action.Name}: {ex.Message}";
            }
            AppLog.Error($"FileAction failed operation={operation} source={source} destination={destinationPath}", ex);
        }
        finally { Volatile.Write(ref _fileActionInProgress, 0); }
    }

    // Advance exactly once, before any synchronous filesystem call begins.
    // Do not call ShowImageAsync after action; completion only updates journal/catalog.
    private async Task<string?> AdvanceBeforeFileActionAsync(string sourcePath, bool removeSource)
    {
        // source is normally selected by _compareSelectedPath ?? _files[_index];
        // _files.Remove(source) is represented by RemoveAt to avoid a second lookup.
        var sourceIndex = _files.FindIndex(p => string.Equals(p, sourcePath, StringComparison.OrdinalIgnoreCase));
        if (removeSource && sourceIndex >= 0) { _files.RemoveAt(sourceIndex); EvictCachedPath(sourcePath); _compareSelectedPath = null; }
        var nextIndex = removeSource ? Math.Min(Math.Max(sourceIndex, 0), _files.Count - 1) : Math.Min(Math.Max(sourceIndex + 1, 0), _files.Count - 1);
        if (_files.Count > 0)
        {
            // Start presenting the next item, but do not wait for decode here.
            // The file operation must begin immediately after the catalog change;
            // awaiting ShowImageAsync would wait for the next image's I/O and
            // defeat the advance-first behavior.
            var nextPath = _files[Math.Max(0, nextIndex)];
            _ = ShowImageAsync(Math.Max(0, nextIndex));
            return nextPath;
        }
        MainImage.Source = null;
        return null;
    }

    private static bool IsSamePath(string first, string second)
    {
        var normalizedFirst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var normalizedSecond = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }

    private async Task UndoLastMoveAsync()
    {
        if (Interlocked.Exchange(ref _fileActionInProgress, 1) != 0) return;
        try
        {
            if (_moveHistory.Count == 0) { StatusText.Text = "Không có Move nào để hoàn tác."; return; }
            var move = _moveHistory.Pop();
            // Captured before the Task.Run await yields the UI thread, so a folder switch
            // mid-undo is detected instead of adding move.Source to a different folder's catalog.
            var folderGeneration = _folderGeneration;
            try
            {
                if (!File.Exists(move.Destination) || File.Exists(move.Source)) throw new IOException("Nguồn hoặc đích đã thay đổi.");
                var destinationInfo = new FileInfo(move.Destination);
                var committed = _journal.ReadCommittedMoves().LastOrDefault(x => x.Destination == move.Destination);
                if (committed is null || destinationInfo.Length != committed.Size || destinationInfo.LastWriteTimeUtc != committed.LastWriteUtc)
                    throw new IOException("File đích đã thay đổi sau Move; không tự động Undo.");
                await Task.Run(() => File.Move(move.Destination, move.Source));
                _lastUndoAction = null;
                if (folderGeneration != _folderGeneration)
                    AppLog.Info($"Undo completion ignored after folder switch: source={move.Source}");
                else
                {
                    if (!_files.Contains(move.Source, StringComparer.OrdinalIgnoreCase)) _files.Add(move.Source);
                    _files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(ImageSortService.NaturalKey(Path.GetFileName(a)), ImageSortService.NaturalKey(Path.GetFileName(b))));
                    await ShowImageAsync(_files.FindIndex(p => string.Equals(p, move.Source, StringComparison.OrdinalIgnoreCase)));
                }
            }
            catch (Exception ex) { StatusText.Text = $"Không thể Undo: {ex.Message}"; _moveHistory.Push(move); }
        }
        finally { Volatile.Write(ref _fileActionInProgress, 0); }
    }

    private async Task UndoLastActionAsync()
    {
        if (_lastUndoAction is null) { StatusText.Text = "Không có Move/Delete vừa thực hiện để hoàn tác."; return; }
        var action = _lastUndoAction;
        if (action.Operation == "Move") { await UndoLastMoveAsync(); return; }
        if (Interlocked.Exchange(ref _fileActionInProgress, 1) != 0) return;
        try
        {
            var restored = await Task.Run(() => RecycleBinRestoreService.TryRestore(action.Source, action.Size, action.LastWriteUtc));
            if (!restored)
            {
                StatusText.Text = $"Không thể khôi phục Recycle Bin: {Path.GetFileName(action.Source)}";
                return;
            }
            _lastUndoAction = null;
            if (_session is not null) { _session.CurrentPath = action.Source; _session.UpdatedUtc = DateTime.UtcNow; _sessionStore.Save(_session); }
            await LoadFolderAsync(Path.GetDirectoryName(action.Source)!);
        }
        finally { Volatile.Write(ref _fileActionInProgress, 0); }
    }

    private sealed record UndoAction(string Operation, string Source, string? Destination, long Size, DateTime LastWriteUtc);

}


