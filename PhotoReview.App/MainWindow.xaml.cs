using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Text.Json;
using Forms = System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;
using System.Security.Cryptography;
using System.Diagnostics;

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
    private readonly ThumbnailCache _thumbnailCache = new();
    private SessionState? _session;
    private double _zoom = 1;
    private readonly Stack<(string Source, string Destination)> _moveHistory = [];
    private CancellationTokenSource _preloadCts = new();
    private readonly SemaphoreSlim _preloadSlots = new(2, 2);
    private const long MaxCacheBytes = 16L * 1024 * 1024 * 1024;
    private const long FullFolderRamThresholdBytes = 16L * 1024 * 1024 * 1024;
    private const double PreloadMemoryLoadLimit = 0.80;
    private string? _compareSelectedPath;
    private sealed record HashCacheEntry(long Length, DateTime LastWriteUtc, string Hash);
    private readonly BoundedLruCache<string, HashCacheEntry> _hashCache = new(
        16L * 1024 * 1024, _ => 128, StringComparer.OrdinalIgnoreCase);
    private readonly ReviewMetrics _metrics = new();

    public MainWindow(string? initialPath = null)
    {
        InitializeComponent();
        _journal.ReconcilePendingOperations();
        foreach (var move in _journal.ReadCommittedMoves())
            if (File.Exists(move.Destination) && !File.Exists(move.Source)) _moveHistory.Push((move.Source, move.Destination!));
        if (!string.IsNullOrWhiteSpace(initialPath) && File.Exists(initialPath))
            _ = LoadFolderAsync(Path.GetDirectoryName(initialPath)!, initialPath);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Chọn folder ảnh để review" };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) _ = LoadFolderAsync(dialog.SelectedPath);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true) _settings = AppSettings.Load();
    }

    private async Task LoadFolderAsync(string folder, string? initialPath = null)
    {
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
        var files = await Task.Run(() => ImageSortService.Sort(Directory.EnumerateFiles(folder).Where(p => supported.Contains(Path.GetExtension(p))), _settings.ImageSortMode));
        _files.Clear(); _files.AddRange(files); _index = -1; _cache.Clear(); _hashCache.Clear();
        _session = _sessionStore.Load(folder);
        FolderText.Text = $"{folder}  ({_files.Count} ảnh)";
        var resumePath = initialPath ?? _session.CurrentPath;
        if (_files.Count > 0) await ShowImageAsync(resumePath is null ? 0 : Math.Max(0, _files.IndexOf(Path.GetFullPath(resumePath))));
        else { MainImage.Source = null; StatusText.Text = "Không tìm thấy ảnh hỗ trợ."; }
    }

    private async Task ShowImageAsync(int index)
    {
        if (index < 0 || index >= _files.Count) return;
        var presentStopwatch = Stopwatch.StartNew();
        _index = index; var path = _files[index]; var token = Interlocked.Increment(ref _generation);
        StatusText.Text = $"Đang tải {index + 1}/{_files.Count}: {Path.GetFileName(path)}";
        try
        {
            if (string.Equals(_settings.LoadingMode, "Preview", StringComparison.OrdinalIgnoreCase))
            {
                var thumbnail = await _thumbnailCache.GetAsync(path);
                if (token != _generation) return;
                MainImage.Source = thumbnail;
                ApplyInitialViewMode();
                StatusText.Text = $"{index + 1}/{_files.Count} | Đang tải ảnh rõ hơn: {Path.GetFileName(path)}";
            }
            var image = await GetPreviewAsync(path);
            if (token != _generation) return;
            MainImage.Source = image;
            var pair = FindComparePair(path);
            ComparePanel.Visibility = pair is null ? Visibility.Collapsed : Visibility.Visible;
            if (pair is not null)
            {
                MainImage.Source = null;
                CompareLeftImage.Tag = pair.Value.Left;
                CompareRightImage.Tag = pair.Value.Right;
                CompareLeftImage.Source = await GetPreviewAsync(pair.Value.Left);
                CompareRightImage.Source = await GetPreviewAsync(pair.Value.Right);
                _compareSelectedPath = path;
                UpdateCompareSelection();
                var leftHash = await GetHashAsync(pair.Value.Left);
                var rightHash = await GetHashAsync(pair.Value.Right);
                var leftInfo = new FileInfo(pair.Value.Left);
                var rightInfo = new FileInfo(pair.Value.Right);
                StatusText.Text = $"{index + 1}/{_files.Count} | Compare | {Path.GetFileName(pair.Value.Left)} ({leftInfo.Length:N0} B) ↔ {Path.GetFileName(pair.Value.Right)} ({rightInfo.Length:N0} B) | hash {(leftHash == rightHash ? "TRÙNG" : "KHÁC")} | click để chọn";
            }
            if (pair is null)
            {
                ApplyInitialViewMode();
                StatusText.Text = $"{index + 1}/{_files.Count} | Đang ở nguồn (chưa tác động) | {Path.GetFileName(path)} | {image.PixelWidth}×{image.PixelHeight}";
            }
            if (_session is not null) { _session.CurrentPath = path; _session.UpdatedUtc = DateTime.UtcNow; _sessionStore.Save(_session); }
            presentStopwatch.Stop();
            _metrics.RecordPresented(presentStopwatch.ElapsedMilliseconds);
            _ = PreloadAroundAsync(index, token);
        }
        catch (Exception ex) { StatusText.Text = $"Lỗi ảnh: {Path.GetFileName(path)} — {ex.Message}"; }
    }

    private async Task<BitmapImage> GetPreviewAsync(string path)
    {
        if (_cache.TryGet(path, out var cached)) { _metrics.RecordCacheHit(); return cached; }
        _metrics.RecordCacheMiss();
        // Read WPF layout/DPI only on the UI thread. The decode below runs on a worker thread.
        var isOriginal = string.Equals(_settings.LoadingMode, "Original", StringComparison.OrdinalIgnoreCase);
        var targetWidth = isOriginal ? 0 : GetTargetDecodeWidth();
        return await Task.Run(() =>
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
                    bitmap = DecodeSource(path, targetWidth);
                }
            }
            else
            {
                sourceRead = true;
                bitmap = DecodeSource(path, targetWidth);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    var tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            encoder.Save(output);
                            output.Flush(true);
                        }
                        File.Move(tempPath, cachePath, true);
                    }
                    finally { try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { } }
                }
                catch { }
            }
            _cache.Set(path, bitmap);
            stopwatch.Stop();
            if (sourceRead) try { _metrics.RecordSourceRead(new FileInfo(path).Length, stopwatch.ElapsedMilliseconds); } catch { }
            return bitmap;
        });
    }

    private static BitmapImage DecodeSource(string path, int targetWidth)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (targetWidth > 0) bitmap.DecodePixelWidth = targetWidth;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }

    private int GetTargetDecodeWidth()
    {
        var viewport = ImageScroll.ActualWidth > 1 ? ImageScroll.ActualWidth : 2200;
        var dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        return AdaptivePreviewPolicy.CalculateTargetDecodeWidth(viewport, dpi, 1.15);
    }

    private static string GetDiskCachePath(string path, int targetWidth)
    {
        var info = new FileInfo(path);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{targetWidth}")));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview", "cache", key + ".png");
    }

    private async Task PreloadAroundAsync(int center, long token)
    {
        _preloadCts.Cancel();
        _preloadCts.Dispose();
        _preloadCts = new CancellationTokenSource();
        var cancellationToken = _preloadCts.Token;
        var totalSourceBytes = _files.Sum(path => { try { return new FileInfo(path).Length; } catch { return 0L; } });
        var offsets = totalSourceBytes < FullFolderRamThresholdBytes
            ? Enumerable.Range(0, _files.Count).Where(i => i != center).Select(i => i - center)
            : Enumerable.Range(1, 8).Concat([-1, -2]);
        foreach (var offset in offsets)
        {
            if (token != _generation || cancellationToken.IsCancellationRequested) return;
            if (!HasPreloadHeadroom()) return;
            var i = center + offset;
            if (i >= 0 && i < _files.Count) _ = PreloadOneAsync(_files[i], cancellationToken);
        }
        await Task.CompletedTask;
    }

    private static bool HasPreloadHeadroom()
    {
        var memory = GC.GetGCMemoryInfo();
        return memory.TotalAvailableMemoryBytes <= 0 ||
            (double)memory.MemoryLoadBytes / memory.TotalAvailableMemoryBytes < PreloadMemoryLoadLimit;
    }

    private async Task PreloadOneAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await _preloadSlots.WaitAsync(cancellationToken);
            try { await GetPreviewAsync(path); }
            finally { _preloadSlots.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (NotSupportedException) { }
    }

    private async void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            e.Handled = true; ToggleFullscreen(); return;
        }
        if (e.Key == Key.Escape && WindowStyle == WindowStyle.None)
        {
            e.Handled = true; ExitFullscreen(); return;
        }
        if (_index < 0) return;
        if (Matches(e.Key, _settings.Shortcuts.NextFolder) || Matches(e.Key, _settings.Shortcuts.PreviousFolder) || e.Key is Key.PageUp or Key.PageDown)
        {
            e.Handled = true;
            var nextFolder = Matches(e.Key, _settings.Shortcuts.NextFolder) || e.Key == Key.PageDown;
            await NavigateSiblingFolderAsync(nextFolder ? 1 : -1);
            return;
        }
        if (Matches(e.Key, _settings.Shortcuts.FirstImage) || e.Key == Key.Home)
        {
            e.Handled = true;
            await ShowImageAsync(0);
            return;
        }
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await UndoLastMoveAsync(); return; }
        foreach (var action in _settings.Actions)
        {
            if (Matches(e.Key, action.Shortcut)) { e.Handled = true; await ExecuteActionAsync(action); return; }
        }
        if (Matches(e.Key, _settings.Shortcuts.SendToRecycleBin)) { e.Handled = true; await ClassifyCurrentAsync(3); return; }
        if (e.Key == Key.Space) { e.Handled = true; if (_session is not null) _session.Skipped.Add(_files[_index]); await ShowImageAsync(Math.Min(_index + 1, _files.Count - 1)); return; }
        if (e.Key == Key.Z) { e.Handled = true; SetZoom(_zoom == 1 ? 2 : 1); return; }
        if (Matches(e.Key, _settings.Shortcuts.ZoomIn) || e.Key is Key.Add or Key.OemPlus) { e.Handled = true; SetZoom(Math.Min(_zoom + .25, 4)); return; }
        if (Matches(e.Key, _settings.Shortcuts.ZoomOut) || e.Key is Key.Subtract or Key.OemMinus) { e.Handled = true; SetZoom(Math.Max(_zoom - .25, .25)); return; }
        if (Matches(e.Key, _settings.Shortcuts.Next) || e.Key == Key.Down) { e.Handled = true; await ShowImageAsync(Math.Min(_index + 1, _files.Count - 1)); }
        if (Matches(e.Key, _settings.Shortcuts.Previous) || e.Key == Key.Up) { e.Handled = true; await ShowImageAsync(Math.Max(_index - 1, 0)); }
    }

    private void Recovery_Click(object sender, RoutedEventArgs e)
    {
        var entries = _journal.ReadPendingOperations().Concat(_journal.ReadFailedOperations()).ToList();
        var dialog = new RecoveryWindow(entries) { Owner = this };
        dialog.ShowDialog();
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DiagnosticsWindow(_metrics.Snapshot()) { Owner = this };
        dialog.ShowDialog();
    }

    private (string Left, string Right)? FindComparePair(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var match = System.Text.RegularExpressions.Regex.Match(stem, "^(.*) \\(\\d+\\)$");
        var baseStem = match.Success ? match.Groups[1].Value : stem;
        var original = _files.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(baseStem, StringComparison.OrdinalIgnoreCase));
        var numbered = _files.FirstOrDefault(p => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(p), $"^{System.Text.RegularExpressions.Regex.Escape(baseStem)} \\(\\d+\\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        return original is not null && numbered is not null ? (original, numbered) : null;
    }

    private void UpdateCompareSelection()
    {
        CompareLeftBorder.BorderBrush = string.Equals(_compareSelectedPath, CompareLeftImage.Tag as string, StringComparison.OrdinalIgnoreCase) ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Gray;
        CompareRightBorder.BorderBrush = string.Equals(_compareSelectedPath, CompareRightImage.Tag as string, StringComparison.OrdinalIgnoreCase) ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Gray;
    }

    private void CompareLeft_Click(object sender, MouseButtonEventArgs e) { _compareSelectedPath = CompareLeftImage.Tag as string; UpdateCompareSelection(); e.Handled = true; }
    private void CompareRight_Click(object sender, MouseButtonEventArgs e) { _compareSelectedPath = CompareRightImage.Tag as string; UpdateCompareSelection(); e.Handled = true; }

    private async void RemoveNumberedDuplicates_Click(object sender, RoutedEventArgs e) => await RemoveDuplicatesAsync(true);
    private async void RemoveOriginalDuplicates_Click(object sender, RoutedEventArgs e) => await RemoveDuplicatesAsync(false);

    private async Task RemoveDuplicatesAsync(bool removeNumbered)
    {
        var remove = new List<string>();
        var sizeGroups = _files.GroupBy(path => new FileInfo(path).Length).Where(group => group.Count() > 1).ToList();
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in sizeGroups.SelectMany(group => group))
        {
            var hash = await GetHashAsync(path);
            if (!groups.TryGetValue(hash, out var group)) groups[hash] = group = [];
            group.Add(path);
        }
        foreach (var group in groups.Values.Where(group => group.Count > 1))
            remove.AddRange(group.Where(path => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(path), " \\(\\d+\\)$") == removeNumbered));
        if (remove.Count == 0) { StatusText.Text = "Không có duplicate cùng hash phù hợp."; return; }
        var review = new BatchReviewWindow(remove) { Owner = this };
        if (review.ShowDialog() != true) { StatusText.Text = "Đã hủy xử lý hàng loạt."; return; }
        var failures = new List<string>();
        var succeeded = 0;
        foreach (var path in remove)
        {
            if (!File.Exists(path)) { failures.Add($"Không còn tồn tại: {path}"); continue; }
            var info = new FileInfo(path);
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

    private async Task<string> GetHashAsync(string path)
    {
        var info = new FileInfo(path);
        if (_hashCache.TryGet(path, out var cached) && cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc) return cached.Hash;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        _hashCache.Set(path, new HashCacheEntry(info.Length, info.LastWriteTimeUtc, hash));
        return hash;
    }

    private async Task NavigateSiblingFolderAsync(int direction)
    {
        if (_session is null) return;
        var currentFolder = Path.GetFullPath(_session.Folder);
        var parent = Directory.GetParent(currentFolder);
        if (parent is null) return;

        var siblingFolders = await Task.Run(() => Directory.EnumerateDirectories(parent.FullName)
            .OrderBy(path => ImageSortService.NaturalKey(Path.GetFileName(path)), StringComparer.OrdinalIgnoreCase)
            .ToList());
        var currentIndex = siblingFolders.FindIndex(path => string.Equals(Path.GetFullPath(path), currentFolder, StringComparison.OrdinalIgnoreCase));
        var targetIndex = currentIndex + direction;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= siblingFolders.Count)
        {
            StatusText.Text = direction > 0 ? "Đã ở folder cuối cùng cùng cấp." : "Đã ở folder đầu tiên cùng cấp.";
            return;
        }

        await LoadFolderAsync(siblingFolders[targetIndex]);
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

    private void Window_Loaded(object sender, RoutedEventArgs e) => UpdateFitSize();
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFitSize();

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
        if (_index >= 0) StatusText.Text = $"{_index + 1}/{_files.Count} | Zoom {_zoom:0.##}x | {Path.GetFileName(_files[_index])}";
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
        var source = _files[_index];
        try
        {
            var info = new FileInfo(source);
            if (category == 3)
            {
                var operationId = Guid.NewGuid().ToString("N");
                _journal.Append(new JournalEntry(operationId, "RecycleBin", "Prepared", source, null, info.Length, info.LastWriteTimeUtc, DateTime.UtcNow));
                FileSystem.DeleteFile(source, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                _journal.Append(new JournalEntry(operationId, "RecycleBin", "Committed", source, null, info.Length, info.LastWriteTimeUtc, DateTime.UtcNow));
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
            }
            _files.RemoveAt(_index); _cache.Remove(source);
            if (_session is not null) { _session.CurrentPath = _files.Count == 0 ? null : _files[Math.Min(_index, _files.Count - 1)]; _session.UpdatedUtc = DateTime.UtcNow; _sessionStore.Save(_session); }
            if (_files.Count > 0) await ShowImageAsync(Math.Min(_index, _files.Count - 1));
            else { MainImage.Source = null; StatusText.Text = "Đã xử lý hết ảnh trong folder."; }
        }
        catch (Exception ex) { StatusText.Text = $"Không xử lý được {Path.GetFileName(source)}: {ex.Message}"; }
    }

    private async Task ExecuteActionAsync(ReviewAction action)
    {
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
        var source = _files[_index];
        try
        {
            if (string.IsNullOrWhiteSpace(action.Destination)) throw new IOException("Action chưa có thư mục đích.");
            var destinationFolder = Path.IsPathRooted(action.Destination) ? action.Destination : Path.Combine(Path.GetDirectoryName(source)!, action.Destination);
            destinationFolder = Path.GetFullPath(destinationFolder);
            var sourceFolder = Path.GetFullPath(Path.GetDirectoryName(source)!);
            if (IsSamePath(destinationFolder, sourceFolder)) throw new IOException("Không thể Move/Copy vào chính folder nguồn.");
            Directory.CreateDirectory(destinationFolder);
            var destination = Path.Combine(destinationFolder, Path.GetFileName(source));
            if (File.Exists(destination)) throw new IOException($"Đích đã tồn tại: {destination}");
            if (action.Operation.Equals("Copy", StringComparison.OrdinalIgnoreCase)) File.Copy(source, destination);
            else File.Move(source, destination);
            if (!action.Operation.Equals("Copy", StringComparison.OrdinalIgnoreCase)) { _files.RemoveAt(_index); _cache.Remove(source); }
            if (_files.Count > 0) await ShowImageAsync(Math.Min(_index, _files.Count - 1));
            else { MainImage.Source = null; StatusText.Text = $"Đã thực hiện: {action.Name}"; }
        }
        catch (Exception ex) { StatusText.Text = $"Không thực hiện được {action.Name}: {ex.Message}"; }
    }

    private static bool IsSamePath(string first, string second)
    {
        var normalizedFirst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var normalizedSecond = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }

    private async Task UndoLastMoveAsync()
    {
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
            if (!_files.Contains(move.Source, StringComparer.OrdinalIgnoreCase)) _files.Add(move.Source);
            _files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(ImageSortService.NaturalKey(Path.GetFileName(a)), ImageSortService.NaturalKey(Path.GetFileName(b))));
            await ShowImageAsync(_files.FindIndex(p => string.Equals(p, move.Source, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) { StatusText.Text = $"Không thể Undo: {ex.Message}"; _moveHistory.Push(move); }
    }

}
