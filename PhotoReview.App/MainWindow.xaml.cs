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
    private readonly BoundedLruCache<string, BitmapImage> _cache = new(
        MaxCacheBytes, bitmap => Math.Max(1, bitmap.PixelWidth * (long)bitmap.PixelHeight * 4),
        StringComparer.OrdinalIgnoreCase);
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
    private CancellationTokenSource _preloadCts = new();
    private readonly Dictionary<string, Task<BitmapImage>> _previewLoads = new(StringComparer.OrdinalIgnoreCase);
    private long _totalSourceBytes;
    private readonly SemaphoreSlim _preloadSlots = new(2, 2);
    private const long MaxCacheBytes = 16L * 1024 * 1024 * 1024;
    private const long FullFolderRamThresholdBytes = 16L * 1024 * 1024 * 1024;
    private const double PreloadMemoryLoadLimit = 0.80;
    private string? _compareSelectedPath;
    private readonly FileHashService _hashService = new();
    private readonly ReviewMetrics _metrics = new();
    private readonly ExplorerOrderService _explorerOrder = new();
    private CancellationTokenSource _folderLoadCts = new();
    private long _folderGeneration;
    private ExplorerViewSnapshot? _lastExplorerSnapshot;
    private bool _placementRestored;
    private int _fileActionInProgress;
    private readonly Dictionary<string, (int Width, int Height)> _originalDimensions = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(string? initialPath = null)
    {
        InitializeComponent();
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
        if (dialog.ShowDialog() == true) _settings = AppSettings.Load();
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
            Title = $"Photo Review — {folder}";
            StatusText.Text = "Đang quét folder ảnh…";
            var files = await Task.Run(() => Directory.EnumerateFiles(folder, "*", System.IO.SearchOption.TopDirectoryOnly)
                .Where(ImageFileTypes.IsSupported).ToList(), loadToken);
            var sortMode = _settings.ImageSortMode;
            var scannedFiles = files.ToArray();
            var explorerTask = _explorerOrder.TryGetSnapshotAsync(folder, TimeSpan.FromSeconds(2), loadToken);
            files = await Task.Run(() => ImageSortService.Sort(files, sortMode), loadToken);
            if (loadToken.IsCancellationRequested || loadGeneration != _folderGeneration) return;
            AppLog.Info($"LoadFolder scan complete: {files.Count} files, initialSort={sortMode}");
            _preloadCts.Cancel();
            _totalSourceBytes = long.MaxValue;
            _files.Clear(); _files.AddRange(files); _index = -1; _cache.Clear(); _hashService.Clear(); _originalDimensions.Clear();
            _session = _sessionStore.Load(folder);
            FolderText.Text = $"{folder}  ({_files.Count} ảnh)";
            var resumePath = initialPath ?? _session.CurrentPath;
            if (_files.Count > 0)
            {
                var resumeIndex = resumePath is null ? 0 : _files.FindIndex(p => string.Equals(p, Path.GetFullPath(resumePath), StringComparison.OrdinalIgnoreCase));
                await ShowImageAsync(resumeIndex >= 0 ? resumeIndex : 0);
            }
            else { MainImage.Source = null; StatusText.Text = "Không tìm thấy ảnh hỗ trợ trong folder này."; }
            var presentationGeneration = _generation;

            var totalBytesTask = Task.Run(() => scannedFiles.Sum(path => { try { return new FileInfo(path).Length; } catch { return 0L; } }), loadToken);
            var explorerSnapshot = await explorerTask;
            if (loadToken.IsCancellationRequested || loadGeneration != _folderGeneration) return;
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
                _preloadCts.Cancel();
                _files.Clear(); _files.AddRange(explorerOrder);
                _index = currentPath is null ? -1 : _files.FindIndex(path => string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase));
                FolderText.Text = $"{folder}  ({_files.Count} ảnh) · Explorer";
                if (mayReplaceInitialFallback && _files.Count > 0) await ShowImageAsync(0);
                else if (_index >= 0) await ShowImageAsync(_index);
                AppLog.Info($"Explorer native order applied: {explorerOrder.Count} files");
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
            Title = $"Photo Review — {folder}";
            StatusText.Text = $"Không mở được folder: {ex.Message}";
        }
    }

    private async Task ShowImageAsync(int index)
    {
        if (index < 0 || index >= _files.Count) return;
        var presentStopwatch = Stopwatch.StartNew();
        _index = index; var path = _files[index]; var token = Interlocked.Increment(ref _generation);
        _compareSelectedPath = null;
        // The catalog can become stale while Explorer order is being applied or an
        // external move/delete completes. Do this check before touching FileInfo.Length
        // so a vanished item is removed and the viewer advances once without logging
        // a misleading ShowImage failure.
        if (!TryGetCurrentFileSize(path, out var initialSize))
        {
            await RemoveMissingCatalogItemAsync(path, index, token);
            return;
        }
        StatusText.Text = $"{index + 1}/{_files.Count} · {FormatFileSize(initialSize)} · Đang tải";
        try
        {
            if (string.Equals(_settings.LoadingMode, "Preview", StringComparison.OrdinalIgnoreCase)
                && !_cache.TryGet(path, out _) && !_previewLoads.ContainsKey(path))
            {
                var thumbnail = await _thumbnailCache.GetAsync(path);
                if (token != _generation) return;
                MainImage.Source = thumbnail;
                ApplyInitialViewMode();
                StatusText.Text = $"{index + 1}/{_files.Count} · {FormatFileSize(new FileInfo(path).Length)} · Đang tải bản rõ";
            }
            var image = await GetPreviewAsync(path);
            if (token != _generation) return;
            MainImage.Source = image;
            _ = PreloadAroundAsync(index, token);
            var pair = FindComparePair(path);
            ComparePanel.Visibility = pair is null ? Visibility.Collapsed : Visibility.Visible;
            if (pair is not null)
            {
                MainImage.Source = null;
                CompareLeftImage.Tag = pair.Value.Left;
                CompareRightImage.Tag = pair.Value.Right;
                CompareLeftImage.Source = await GetPreviewAsync(pair.Value.Left);
                if (token != _generation) return;
                CompareRightImage.Source = await GetPreviewAsync(pair.Value.Right);
                if (token != _generation) return;
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
                var original = await GetOriginalDimensionsAsync(path);
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
            await RemoveMissingCatalogItemAsync(path, index, token);
        }
        catch (Exception ex) when (token == _generation) { AppLog.Error($"ShowImage failed: {path}", ex); StatusText.Text = $"Lỗi ảnh: {Path.GetFileName(path)} — {ex.Message}"; }
    }

    private static bool TryGetCurrentFileSize(string path, out long size)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { size = 0; return false; }
            size = info.Length;
            return true;
        }
        catch (FileNotFoundException) { size = 0; return false; }
        catch (DirectoryNotFoundException) { size = 0; return false; }
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

    private async Task<BitmapImage> GetPreviewAsync(string path)
    {
        if (_cache.TryGet(path, out var cached)) { _metrics.RecordCacheHit(); return cached; }
        if (_previewLoads.TryGetValue(path, out var pending)) return await pending;
        _metrics.RecordCacheMiss();
        // Read WPF layout/DPI only on the UI thread. The decode below runs on a worker thread.
        var isOriginal = string.Equals(_settings.LoadingMode, "Original", StringComparison.OrdinalIgnoreCase);
        var targetWidth = isOriginal ? 0 : GetTargetDecodeWidth();
        var load = Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            var sourceRead = false;
            var bitmap = new BitmapImage();
            var cachePath = GetDiskCachePath(path, targetWidth);
            if (File.Exists(cachePath))
            {
                try
                {
                    using var cacheStream = File.OpenRead(cachePath);
                    bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = cacheStream; bitmap.EndInit(); bitmap.Freeze();
                }
                catch (Exception) when (File.Exists(cachePath))
                {
                    try { File.Delete(cachePath); } catch { }
                    sourceRead = true;
                    bitmap = DecodeWithFallback(path, targetWidth);
                }
            }
            else
            {
                sourceRead = true;
                bitmap = DecodeWithFallback(path, targetWidth);
                // Keep decoded previews in RAM; PNG encoding and durable writes delay review.
            }
            _cache.Set(path, bitmap);
            stopwatch.Stop();
            if (sourceRead) try { _metrics.RecordSourceRead(new FileInfo(path).Length, stopwatch.ElapsedMilliseconds); } catch { }
            return bitmap;
        });
        _previewLoads[path] = load;
        try { return await load; }
        finally { _previewLoads.Remove(path); }
    }

    private static BitmapImage DecodeSource(string path, int targetWidth)
    {
        // Allow an in-flight decode to coexist with Move/Delete. The action path
        // cancels future work and invalidates its result; Windows can still
        // complete the file operation without waiting for this read handle.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (targetWidth > 0) bitmap.DecodePixelWidth = targetWidth;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }

    private sealed class WindowHandle(Window window) : Forms.IWin32Window
    {
        public IntPtr Handle => new WindowInteropHelper(window).Handle;
    }

    private async Task<(int Width, int Height)> GetOriginalDimensionsAsync(string path)
    {
        if (_originalDimensions.TryGetValue(path, out var dimensions)) return dimensions;
        dimensions = await Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        });
        _originalDimensions[path] = dimensions;
        return dimensions;
    }

    private static BitmapImage DecodeWithFallback(string path, int targetWidth)
    {
        try { return DecodeSource(path, targetWidth); }
        catch when (targetWidth > 0) { return DecodeSource(path, 0); }
    }

    private int GetTargetDecodeWidth()
    {
        var viewport = ImageScroll.ActualWidth > 1 ? ImageScroll.ActualWidth : 2200;
        var dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        return AdaptivePreviewPolicy.CalculateTargetDecodeWidth(viewport, dpi, 1.15);
    }

    private static string GetDiskCachePath(string path, int targetWidth)
    {
        long length = 0;
        long lastWriteTicks = 0;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) { length = info.Length; lastWriteTicks = info.LastWriteTimeUtc.Ticks; }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{path}|{length}|{lastWriteTicks}|{targetWidth}")));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview", "cache", key + ".png");
    }

    private async Task PreloadAroundAsync(int center, long token)
    {
        _preloadCts.Cancel();
        _preloadCts.Dispose();
        _preloadCts = new CancellationTokenSource();
        var cancellationToken = _preloadCts.Token;
        var nearby = Enumerable.Range(1, 8).Concat([-1, -2]);
        var offsets = _totalSourceBytes < FullFolderRamThresholdBytes
            ? nearby.Concat(Enumerable.Range(center + 1, _files.Count - center - 1).Select(i => i - center))
                .Concat(Enumerable.Range(0, center).Reverse().Select(i => i - center)).Distinct()
            : nearby;
        var files = _files.ToArray();
        var batch = new List<Task>(2);
        foreach (var offset in offsets)
        {
            // Even RAM hits must let input/rendering run. Never enqueue the whole folder.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            if (token != _generation || cancellationToken.IsCancellationRequested) return;
            if (!HasPreloadHeadroom()) return;
            var i = center + offset;
            if (i < 0 || i >= files.Length || _cache.TryGet(files[i], out _)) continue;
            batch.Add(PreloadOneAsync(files[i], cancellationToken));
            if (batch.Count == 2)
            {
                await Task.WhenAll(batch);
                batch.Clear();
            }
        }
        await Task.WhenAll(batch);
    }

    private static bool HasPreloadHeadroom()
    {
        var memory = GC.GetGCMemoryInfo();
        return PhysicalMemory.HasHeadroom(PreloadMemoryLoadLimit) && (memory.TotalAvailableMemoryBytes <= 0 ||
            (double)memory.MemoryLoadBytes / memory.TotalAvailableMemoryBytes < PreloadMemoryLoadLimit);
    }

    private async Task PreloadOneAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await _preloadSlots.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!HasPreloadHeadroom()) return;
                await GetPreviewAsync(path);
            }
            finally { _preloadSlots.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (NotSupportedException) { }
        catch (Exception ex) { AppLog.Error($"Preload failed: {path}", ex); }
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
        // Keep navigation and other keyboard commands from racing an in-flight
        // file action. Otherwise a Next key can change _index before the action
        // removes its source and the viewer may skip an image.
        if (Volatile.Read(ref _fileActionInProgress) != 0)
        {
            e.Handled = true;
            return;
        }
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
        if (Matches(e.Key, _settings.Shortcuts.Skip)) { e.Handled = true; if (_session is not null) { _session.Skipped.Add(_files[_index]); _session.UpdatedUtc = DateTime.UtcNow; _sessionStore.Save(_session); } await ShowImageAsync(Math.Min(_index + 1, _files.Count - 1)); return; }
        if (Matches(e.Key, _settings.Shortcuts.ToggleFit)) { e.Handled = true; ApplyInitialViewMode(); return; }
        if (Matches(e.Key, _settings.Shortcuts.ZoomIn)) { e.Handled = true; SetZoom(Math.Min(_zoom + .25, 4)); return; }
        if (Matches(e.Key, _settings.Shortcuts.ZoomOut)) { e.Handled = true; SetZoom(Math.Max(_zoom - .25, .25)); return; }
        if (Matches(e.Key, _settings.Shortcuts.Next)) { e.Handled = true; await ShowImageAsync(Math.Min(_index + 1, _files.Count - 1)); }
        if (Matches(e.Key, _settings.Shortcuts.Previous)) { e.Handled = true; await ShowImageAsync(Math.Max(_index - 1, 0)); }
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

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(this, "Xóa toàn bộ cache preview? Ảnh nguồn không bị thay đổi.", "Xác nhận xóa cache", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        _thumbnailCache.ClearDisk();
        _thumbnailCache.ClearMemory();
        _cache.Clear();
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
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
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
        _preloadCts.Cancel();
        _preloadCts.Dispose();
        _folderLoadCts.Cancel();
        _folderLoadCts.Dispose();
        _thumbnailCache.Dispose();
        _hashService.Clear();
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
        var source = _compareSelectedPath ?? _files[_index];
        StopImageReadsForAction();
        try
        {
            var info = new FileInfo(source);
            if (category == 3)
            {
                var operationId = Guid.NewGuid().ToString("N");
                _journal.Append(new JournalEntry(operationId, "Recycle", "Prepared", source, null, info.Length, info.LastWriteTimeUtc, DateTime.UtcNow));
                FileSystem.DeleteFile(source, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
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
                File.Move(source, destination);
                var movedInfo = new FileInfo(destination);
                if (movedInfo.Length != info.Length) throw new IOException("Kiểm tra sau Move thất bại: kích thước thay đổi.");
                _journal.Append(new JournalEntry(operationId, "Move", "Committed", source, destination, info.Length, info.LastWriteTimeUtc, DateTime.UtcNow));
                _moveHistory.Push((source, destination));
                _lastUndoAction = new UndoAction("Move", source, destination, info.Length, info.LastWriteTimeUtc);
            }
            _files.Remove(source); _cache.Remove(source); _compareSelectedPath = null;
            if (_session is not null) { _session.CurrentPath = _files.Count == 0 ? null : _files[Math.Min(_index, _files.Count - 1)]; _session.UpdatedUtc = DateTime.UtcNow; _sessionStore.Save(_session); }
            if (_files.Count > 0) await ShowImageAsync(Math.Min(_index, _files.Count - 1));
            else { MainImage.Source = null; StatusText.Text = "Đã xử lý hết ảnh trong folder."; }
        }
        catch (Exception ex) { StatusText.Text = $"Không xử lý được {Path.GetFileName(source)}: {ex.Message}"; }
        finally { Volatile.Write(ref _fileActionInProgress, 0); }
    }

    private void StopImageReadsForAction()
    {
        // Do not await decode/preload tasks here: the file action must start now.
        // Generation invalidation prevents any late bitmap from being presented.
        Interlocked.Increment(ref _generation);
        // Invalidate an Explorer snapshot/load that started before the action.
        Interlocked.Increment(ref _folderGeneration);
        _preloadCts.Cancel();
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
        var source = _compareSelectedPath ?? _files[_index];
        var operation = action.Operation.Equals("Copy", StringComparison.OrdinalIgnoreCase) ? "Copy" : "Move";
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
            if (operation == "Copy") File.Copy(source, destinationPath);
            else File.Move(source, destinationPath);
            var destinationInfo = new FileInfo(destinationPath);
            if (!destinationInfo.Exists || destinationInfo.Length != sourceSize)
                throw new IOException("Kiểm tra sau thao tác thất bại: kích thước đích thay đổi.");
            _journal.Append(new JournalEntry(operationId, operation, "Committed", source, destinationPath, sourceSize, sourceLastWriteUtc, DateTime.UtcNow));
            if (operation == "Move") { _files.Remove(source); _cache.Remove(source); _compareSelectedPath = null; }
            if (_files.Count > 0) await ShowImageAsync(Math.Min(_index, _files.Count - 1));
            else { MainImage.Source = null; StatusText.Text = $"Đã thực hiện: {action.Name}"; }
        }
        catch (Exception ex)
        {
            if (prepared) _journal.Append(new JournalEntry(operationId, operation, "Failed", source, destinationPath, sourceSize, sourceLastWriteUtc, DateTime.UtcNow, ex.Message));
            StatusText.Text = $"Không thực hiện được {action.Name}: {ex.Message}";
        }
        finally { Volatile.Write(ref _fileActionInProgress, 0); }
    }

    private static bool IsSamePath(string first, string second)
    {
        var normalizedFirst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var normalizedSecond = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }

    private async Task UndoLastMoveAsync()
    {
        if (Volatile.Read(ref _fileActionInProgress) != 0) return;
        if (_moveHistory.Count == 0) { StatusText.Text = "Không có Move nào để hoàn tác."; return; }
        var move = _moveHistory.Pop();
        try
        {
            if (!File.Exists(move.Destination) || File.Exists(move.Source)) throw new IOException("Nguồn hoặc đích đã thay đổi.");
            var destinationInfo = new FileInfo(move.Destination);
            var committed = _journal.ReadCommittedMoves().LastOrDefault(x => x.Destination == move.Destination);
            if (committed is null || destinationInfo.Length != committed.Size || destinationInfo.LastWriteTimeUtc != committed.LastWriteUtc)
                throw new IOException("File đích đã thay đổi sau Move; không tự động Undo.");
            File.Move(move.Destination, move.Source);
            _lastUndoAction = null;
            if (!_files.Contains(move.Source, StringComparer.OrdinalIgnoreCase)) _files.Add(move.Source);
            _files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(ImageSortService.NaturalKey(Path.GetFileName(a)), ImageSortService.NaturalKey(Path.GetFileName(b))));
            await ShowImageAsync(_files.FindIndex(p => string.Equals(p, move.Source, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) { StatusText.Text = $"Không thể Undo: {ex.Message}"; _moveHistory.Push(move); }
    }

    private async Task UndoLastActionAsync()
    {
        if (Volatile.Read(ref _fileActionInProgress) != 0) return;
        if (_lastUndoAction is null) { StatusText.Text = "Không có Move/Delete vừa thực hiện để hoàn tác."; return; }
        var action = _lastUndoAction;
        if (action.Operation == "Move") { await UndoLastMoveAsync(); return; }
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

    private sealed record UndoAction(string Operation, string Source, string? Destination, long Size, DateTime LastWriteUtc);

}
