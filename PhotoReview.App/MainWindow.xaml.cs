using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Text.Json;
using Forms = System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;

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
    private SessionState? _session;
    private double _zoom = 1;
    private readonly Stack<(string Source, string Destination)> _moveHistory = [];
    private const long MaxCacheBytes = 1024L * 1024 * 1024;

    public MainWindow(string? initialPath = null)
    {
        InitializeComponent();
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
        var files = await Task.Run(() => Directory.EnumerateFiles(folder).Where(p => supported.Contains(Path.GetExtension(p)))
            .OrderBy(p => NaturalKey(Path.GetFileName(p)), StringComparer.OrdinalIgnoreCase).ToList());
        _files.Clear(); _files.AddRange(files); _index = -1; _cache.Clear();
        _session = _sessionStore.Load(folder);
        FolderText.Text = $"{folder}  ({_files.Count} ảnh)";
        var resumePath = initialPath ?? _session.CurrentPath;
        if (_files.Count > 0) await ShowImageAsync(resumePath is null ? 0 : Math.Max(0, _files.IndexOf(Path.GetFullPath(resumePath))));
        else { MainImage.Source = null; StatusText.Text = "Không tìm thấy ảnh hỗ trợ."; }
    }

    private async Task ShowImageAsync(int index)
    {
        if (index < 0 || index >= _files.Count) return;
        _index = index; var path = _files[index]; var token = Interlocked.Increment(ref _generation);
        StatusText.Text = $"Đang tải {index + 1}/{_files.Count}: {Path.GetFileName(path)}";
        try
        {
            var image = await GetPreviewAsync(path);
            if (token != _generation) return;
            MainImage.Source = image;
            ApplyInitialViewMode();
            StatusText.Text = $"{index + 1}/{_files.Count} | Đang ở nguồn (chưa tác động) | {Path.GetFileName(path)} | {image.PixelWidth}×{image.PixelHeight}";
            if (_session is not null) { _session.CurrentPath = path; _session.UpdatedUtc = DateTime.UtcNow; _sessionStore.Save(_session); }
            _ = PreloadAroundAsync(index, token);
        }
        catch (Exception ex) { StatusText.Text = $"Lỗi ảnh: {Path.GetFileName(path)} — {ex.Message}"; }
    }

    private async Task<BitmapImage> GetPreviewAsync(string path)
    {
        if (_cache.TryGet(path, out var cached)) return cached;
        return await Task.Run(() =>
        {
            var bitmap = new BitmapImage();
            var cachePath = GetDiskCachePath(path);
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
                    bitmap = DecodeSource(path);
                }
            }
            else
            {
                bitmap = DecodeSource(path);
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
            return bitmap;
        });
    }

    private static BitmapImage DecodeSource(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 2200;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }

    private static string GetDiskCachePath(string path)
    {
        var info = new FileInfo(path);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|2200")));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview", "cache", key + ".png");
    }

    private async Task PreloadAroundAsync(int center, long token)
    {
        foreach (var offset in new[] { 1, -1, 2, -2, 3 })
        {
            if (token != _generation) return;
            var i = center + offset;
            if (i >= 0 && i < _files.Count) _ = GetPreviewAsync(_files[i]);
        }
        await Task.CompletedTask;
    }

    private async void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_index < 0) return;
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await UndoLastMoveAsync(); return; }
        if (Matches(e.Key, _settings.Shortcuts.MoveToFolder2)) { e.Handled = true; await ClassifyCurrentAsync(2); return; }
        if (Matches(e.Key, _settings.Shortcuts.SendToRecycleBin)) { e.Handled = true; await ClassifyCurrentAsync(3); return; }
        if (e.Key == Key.Space) { e.Handled = true; if (_session is not null) _session.Skipped.Add(_files[_index]); await ShowImageAsync(Math.Min(_index + 1, _files.Count - 1)); return; }
        if (e.Key == Key.Z) { e.Handled = true; SetZoom(_zoom == 1 ? 2 : 1); return; }
        if (e.Key is Key.Add or Key.OemPlus) { e.Handled = true; SetZoom(Math.Min(_zoom + .25, 4)); return; }
        if (e.Key is Key.Subtract or Key.OemMinus) { e.Handled = true; SetZoom(Math.Max(_zoom - .25, .25)); return; }
        if (Matches(e.Key, _settings.Shortcuts.Next) || e.Key == Key.Down) { e.Handled = true; await ShowImageAsync(Math.Min(_index + 1, _files.Count - 1)); }
        if (Matches(e.Key, _settings.Shortcuts.Previous) || e.Key == Key.Up) { e.Handled = true; await ShowImageAsync(Math.Max(_index - 1, 0)); }
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
            _files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(NaturalKey(Path.GetFileName(a)), NaturalKey(Path.GetFileName(b))));
            await ShowImageAsync(_files.FindIndex(p => string.Equals(p, move.Source, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) { StatusText.Text = $"Không thể Undo: {ex.Message}"; _moveHistory.Push(move); }
    }

    private static string NaturalKey(string name) => System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "\\d+", m => m.Value.PadLeft(12, '0'));
}
