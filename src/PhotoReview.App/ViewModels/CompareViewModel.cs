using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoReview.App.ViewModels;

/// <summary>
/// ViewModel điều phối so sánh hai ảnh song song (tên, kích thước, MD5 hash song song),
/// tuân thủ quy tắc K-2 (không phụ thuộc visual / media types của WPF).
/// </summary>
public sealed partial class CompareViewModel : ObservableObject
{
    private bool _isVisible;
    private string? _leftPath;
    private string? _rightPath;
    private object? _leftImage;
    private object? _rightImage;
    private string? _selectedPath;
    private string _leftSizeText = string.Empty;
    private string _rightSizeText = string.Empty;
    private string _hashText = " | hash tắt";
    private string _statusText = string.Empty;

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public string? LeftPath
    {
        get => _leftPath;
        set
        {
            if (SetProperty(ref _leftPath, value))
            {
                OnPropertyChanged(nameof(IsLeftSelected));
            }
        }
    }

    public string? RightPath
    {
        get => _rightPath;
        set
        {
            if (SetProperty(ref _rightPath, value))
            {
                OnPropertyChanged(nameof(IsRightSelected));
            }
        }
    }

    public object? LeftImage
    {
        get => _leftImage;
        set => SetProperty(ref _leftImage, value);
    }

    public object? RightImage
    {
        get => _rightImage;
        set => SetProperty(ref _rightImage, value);
    }

    public string? SelectedPath
    {
        get => _selectedPath;
        set
        {
            if (SetProperty(ref _selectedPath, value))
            {
                OnPropertyChanged(nameof(IsLeftSelected));
                OnPropertyChanged(nameof(IsRightSelected));
            }
        }
    }

    public bool IsLeftSelected =>
        !string.IsNullOrEmpty(LeftPath) &&
        string.Equals(SelectedPath, LeftPath, StringComparison.OrdinalIgnoreCase);

    public bool IsRightSelected =>
        !string.IsNullOrEmpty(RightPath) &&
        string.Equals(SelectedPath, RightPath, StringComparison.OrdinalIgnoreCase);

    public string LeftSizeText
    {
        get => _leftSizeText;
        set
        {
            if (SetProperty(ref _leftSizeText, value))
            {
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    public string RightSizeText
    {
        get => _rightSizeText;
        set
        {
            if (SetProperty(ref _rightSizeText, value))
            {
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    public string SizeText => $"{LeftSizeText} ↔ {RightSizeText}";

    public string HashText
    {
        get => _hashText;
        set => SetProperty(ref _hashText, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public void SelectLeft() => SelectedPath = LeftPath;

    public void SelectRight() => SelectedPath = RightPath;

    public void Select(string? path) => SelectedPath = path;

    public void Toggle() => IsVisible = !IsVisible;

    public void Clear()
    {
        _isVisible = false;
        _leftPath = null;
        _rightPath = null;
        _leftImage = null;
        _rightImage = null;
        _selectedPath = null;
        _leftSizeText = string.Empty;
        _rightSizeText = string.Empty;
        _hashText = " | hash tắt";
        _statusText = string.Empty;

        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(LeftPath));
        OnPropertyChanged(nameof(RightPath));
        OnPropertyChanged(nameof(LeftImage));
        OnPropertyChanged(nameof(RightImage));
        OnPropertyChanged(nameof(SelectedPath));
        OnPropertyChanged(nameof(IsLeftSelected));
        OnPropertyChanged(nameof(IsRightSelected));
        OnPropertyChanged(nameof(LeftSizeText));
        OnPropertyChanged(nameof(RightSizeText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(HashText));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>
    /// Nạp dữ liệu so sánh ảnh: ảnh xem trước và mã hash được xử lý song song,
    /// bảo đảm kiểm tra thế hệ token hủy sau mỗi lần await (bảo toàn INV-1).
    /// </summary>
    public async Task<bool> LoadAsync(
        (string Left, string Right) pair,
        long token,
        Func<long, bool> isTokenCurrent,
        Func<string, Task<object?>> loadImageAsync,
        Func<string, Task<string>>? getHashAsync = null,
        bool compareSizeEnabled = false,
        bool compareHashEnabled = false,
        int currentIndex = 0,
        int totalFiles = 0,
        Func<string, long?>? getFileSize = null,
        string? initialSelectedPath = null)
    {
        ArgumentNullException.ThrowIfNull(isTokenCurrent);
        ArgumentNullException.ThrowIfNull(loadImageAsync);

        if (!isTokenCurrent(token)) return false;

        LeftPath = pair.Left;
        RightPath = pair.Right;
        SelectedPath = initialSelectedPath ?? pair.Left;
        IsVisible = true;
        LeftImage = null;
        RightImage = null;

        // 1. Tải hai ảnh song song
        var leftImageTask = loadImageAsync(pair.Left);
        var rightImageTask = loadImageAsync(pair.Right);
        var previews = await Task.WhenAll(leftImageTask, rightImageTask).ConfigureAwait(false);

        // Kiểm tra token sau await
        if (!isTokenCurrent(token)) return false;

        LeftImage = previews[0];
        RightImage = previews[1];

        // 2. Định dạng kích thước tệp nếu được bật
        var leftSize = string.Empty;
        var rightSize = string.Empty;
        if (compareSizeEnabled)
        {
            var leftBytes = getFileSize != null ? getFileSize(pair.Left) : TryGetFileSize(pair.Left);
            var rightBytes = getFileSize != null ? getFileSize(pair.Right) : TryGetFileSize(pair.Right);

            if (leftBytes.HasValue) leftSize = $" ({leftBytes.Value:N0} B)";
            if (rightBytes.HasValue) rightSize = $" ({rightBytes.Value:N0} B)";
        }
        LeftSizeText = leftSize;
        RightSizeText = rightSize;

        // 3. Tính mã hash song song nếu được bật
        var hashResult = " | hash tắt";
        if (compareHashEnabled && getHashAsync != null)
        {
            var leftHashTask = getHashAsync(pair.Left);
            var rightHashTask = getHashAsync(pair.Right);
            var hashes = await Task.WhenAll(leftHashTask, rightHashTask).ConfigureAwait(false);

            // Kiểm tra token sau await
            if (!isTokenCurrent(token)) return false;

            var match = string.Equals(hashes[0], hashes[1], StringComparison.OrdinalIgnoreCase);
            hashResult = $" | hash {(match ? "TRÙNG" : "KHÁC")}";
        }
        HashText = hashResult;

        // 4. Cập nhật StatusText
        var leftName = Path.GetFileName(pair.Left);
        var rightName = Path.GetFileName(pair.Right);
        StatusText = StatusFormatter.Compare(currentIndex, totalFiles, leftName, leftSize, rightName, rightSize, HashText);

        return true;
    }

    private static long? TryGetFileSize(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : null;
        }
        catch
        {
            return null;
        }
    }
}