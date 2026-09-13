using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;

namespace PhotoReview.App;

public partial class MainWindow : Window
{
    private readonly ConcurrentDictionary<string, BitmapImage> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _files = [];
    private readonly Dictionary<string, int> _categories = new(StringComparer.OrdinalIgnoreCase);
    private int _index = -1;
    private long _generation;
    private readonly AppSettings _settings = AppSettings.Load();

    public MainWindow(string? initialPath = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(initialPath) && File.Exists(initialPath))
            _ = LoadFolderAsync(Path.GetDirectoryName(initialPath)!, initialPath);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Chọn folder ảnh để review" };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) _ = LoadFolderAsync(dialog.SelectedPath);
    }

    private async Task LoadFolderAsync(string folder, string? initialPath = null)
    {
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
        var files = await Task.Run(() => Directory.EnumerateFiles(folder).Where(p => supported.Contains(Path.GetExtension(p)))
            .OrderBy(p => NaturalKey(Path.GetFileName(p)), StringComparer.OrdinalIgnoreCase).ToList());
        _files.Clear(); _files.AddRange(files); _categories.Clear(); _index = -1; _cache.Clear();
        FolderText.Text = $"{folder}  ({_files.Count} ảnh)";
        if (_files.Count > 0) await ShowImageAsync(initialPath is null ? 0 : Math.Max(0, _files.IndexOf(Path.GetFullPath(initialPath))));
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
            var label = _categories.TryGetValue(path, out var c) ? $"Loại {c}" : "Chưa phân loại";
            StatusText.Text = $"{index + 1}/{_files.Count} | {label} | {Path.GetFileName(path)} | {image.PixelWidth}×{image.PixelHeight}";
            _ = PreloadAroundAsync(index, token);
        }
        catch (Exception ex) { StatusText.Text = $"Lỗi ảnh: {Path.GetFileName(path)} — {ex.Message}"; }
    }

    private async Task<BitmapImage> GetPreviewAsync(string path)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;
        return await Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 2200;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); _cache[path] = bitmap; return bitmap;
        });
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
        if (e.Key is Key.D1 or Key.NumPad1 or Key.D2 or Key.NumPad2 or Key.D3 or Key.NumPad3)
        {
            var category = e.Key is Key.D1 or Key.NumPad1 ? 1 : e.Key is Key.D2 or Key.NumPad2 ? 2 : 3;
            e.Handled = true; await ClassifyCurrentAsync(category); return;
        }
        if (e.Key == Key.Space) { e.Handled = true; await ShowImageAsync(Math.Min(_index + 1, _files.Count - 1)); return; }
        if (e.Key == Key.Right) { e.Handled = true; await ShowImageAsync(Math.Min(_index + 1, _files.Count - 1)); }
        if (e.Key == Key.Left) { e.Handled = true; await ShowImageAsync(Math.Max(_index - 1, 0)); }
    }

    private async Task ClassifyCurrentAsync(int category)
    {
        if (_index < 0 || _index >= _files.Count) return;
        var source = _files[_index];
        try
        {
            if (category == 3)
                FileSystem.DeleteFile(source, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else
            {
                var folderName = category == 1 ? _settings.Folder1Name : _settings.Folder2Name;
                var destinationFolder = Path.IsPathRooted(folderName) ? folderName : Path.Combine(Path.GetDirectoryName(source)!, folderName);
                Directory.CreateDirectory(destinationFolder);
                var destination = Path.Combine(destinationFolder, Path.GetFileName(source));
                if (File.Exists(destination)) throw new IOException($"Đích đã tồn tại: {destination}");
                File.Move(source, destination);
            }
            _files.RemoveAt(_index); _cache.TryRemove(source, out _);
            if (_files.Count > 0) await ShowImageAsync(Math.Min(_index, _files.Count - 1));
            else { MainImage.Source = null; StatusText.Text = "Đã xử lý hết ảnh trong folder."; }
        }
        catch (Exception ex) { StatusText.Text = $"Không xử lý được {Path.GetFileName(source)}: {ex.Message}"; }
    }

    private static string NaturalKey(string name) => System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "\\d+", m => m.Value.PadLeft(12, '0'));
}
