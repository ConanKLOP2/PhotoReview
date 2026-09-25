using System.Windows.Input;
using PhotoReview.Core.Settings;

namespace PhotoReview.App.Input;

/// <summary>
/// Định tuyến sự kiện phím thành các lệnh ReviewCommand độc lập với UI,
/// bảo toàn 100% thứ tự ưu tiên xử lý phím bấm và phân biệt phím hệ thống.
/// </summary>
public sealed class ShortcutRouter
{
    private Key? _fullscreenKey;
    private Key? _nextFolderKey;
    private Key? _prevFolderKey;
    private Key? _firstImageKey;
    private Key? _undoKey;
    private Key? _compareKey;
    private readonly List<(Key Key, int Index)> _actionKeys = [];
    private Key? _recycleKey;
    private Key? _skipKey;
    private Key? _toggleFitKey;
    private Key? _zoomInKey;
    private Key? _zoomOutKey;
    private Key? _nextKey;
    private Key? _prevKey;

    public ShortcutRouter(AppSettings? settings = null)
    {
        if (settings is not null)
        {
            Rebuild(settings);
        }
    }

    /// <summary>
    /// Xây dựng lại bảng tra phím tắt từ cấu hình ứng dụng.
    /// </summary>
    public void Rebuild(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _fullscreenKey = ParseKey(settings.Shortcuts.Fullscreen);
        _nextFolderKey = ParseKey(settings.Shortcuts.NextFolder);
        _prevFolderKey = ParseKey(settings.Shortcuts.PreviousFolder);
        _firstImageKey = ParseKey(settings.Shortcuts.FirstImage);
        _undoKey = ParseKey(settings.Shortcuts.Undo);
        _compareKey = ParseKey(settings.Shortcuts.Compare);
        _recycleKey = ParseKey(settings.Shortcuts.SendToRecycleBin);
        _skipKey = ParseKey(settings.Shortcuts.Skip);
        _toggleFitKey = ParseKey(settings.Shortcuts.ToggleFit);
        _zoomInKey = ParseKey(settings.Shortcuts.ZoomIn);
        _zoomOutKey = ParseKey(settings.Shortcuts.ZoomOut);
        _nextKey = ParseKey(settings.Shortcuts.Next);
        _prevKey = ParseKey(settings.Shortcuts.Previous);

        _actionKeys.Clear();
        for (var i = 0; i < settings.Actions.Count; i++)
        {
            var action = settings.Actions[i];
            if (ParseKey(action.Shortcut) is { } k)
            {
                _actionKeys.Add((k, i));
            }
        }
    }

    /// <summary>
    /// Thử định tuyến phím bấm thành lệnh ReviewCommand theo đúng thứ tự ưu tiên trong MainWindow.
    /// </summary>
    public ReviewCommand? TryResolve(
        Key key,
        Key systemKey,
        ModifierKeys modifiers,
        bool isFullscreen,
        bool hasImage,
        bool hasComparePair = true,
        bool isCompareVisible = false)
    {
        var pressedKey = key == Key.System ? systemKey : key;

        // 1. Fullscreen: sử dụng pressedKey (tính cả Key.System)
        if (_fullscreenKey.HasValue && pressedKey == _fullscreenKey.Value)
        {
            return ReviewCommand.Fullscreen;
        }

        // 2. Escape: thoát Fullscreen nếu đang bật, nếu không thì đóng cửa sổ
        if (key == Key.Escape && isFullscreen)
        {
            return ReviewCommand.ExitFullscreen;
        }
        if (key == Key.Escape)
        {
            return ReviewCommand.Close;
        }

        // 3. Điều hướng thư mục
        if (_nextFolderKey.HasValue && key == _nextFolderKey.Value)
        {
            return ReviewCommand.NextFolder;
        }
        if (_prevFolderKey.HasValue && key == _prevFolderKey.Value)
        {
            return ReviewCommand.PreviousFolder;
        }

        // 4. Về ảnh đầu tiên (chỉ tác dụng khi có ảnh)
        if (_firstImageKey.HasValue && key == _firstImageKey.Value)
        {
            return hasImage ? ReviewCommand.FirstImage : null;
        }

        // 5. Undo Move: yêu cầu phím khớp và giữ Ctrl
        if (_undoKey.HasValue && key == _undoKey.Value && (modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            return ReviewCommand.Undo;
        }

        // 6. Nhóm lệnh sau if (_index < 0) return: chỉ chạy khi có ảnh hợp lệ
        if (!hasImage)
        {
            return null;
        }

        // 7. So sánh ảnh (Compare)
        if (_compareKey.HasValue && key == _compareKey.Value)
        {
            if (!isCompareVisible && !hasComparePair)
            {
                return null;
            }
            return ReviewCommand.ToggleCompare;
        }

        // 8. Custom Actions
        foreach (var (actionKey, index) in _actionKeys)
        {
            if (key == actionKey)
            {
                return ReviewCommand.Action(index);
            }
        }

        // 9. Recycle
        if (_recycleKey.HasValue && key == _recycleKey.Value)
        {
            return ReviewCommand.Recycle;
        }

        // 10. Skip
        if (_skipKey.HasValue && key == _skipKey.Value)
        {
            return ReviewCommand.Skip;
        }

        // 11. Toggle Fit
        if (_toggleFitKey.HasValue && key == _toggleFitKey.Value)
        {
            return ReviewCommand.ToggleFit;
        }

        // 12. Zoom In / Out
        if (_zoomInKey.HasValue && key == _zoomInKey.Value)
        {
            return ReviewCommand.ZoomIn;
        }
        if (_zoomOutKey.HasValue && key == _zoomOutKey.Value)
        {
            return ReviewCommand.ZoomOut;
        }

        // 13. Next / Previous
        if (_nextKey.HasValue && key == _nextKey.Value)
        {
            return ReviewCommand.Next;
        }
        if (_prevKey.HasValue && key == _prevKey.Value)
        {
            return ReviewCommand.Previous;
        }

        return null;
    }

    private static Key? ParseKey(string? str)
    {
        if (string.IsNullOrWhiteSpace(str)) return null;
        return Services.ShortcutKeyName.TryParse(str, out var key) ? key : null;
    }
}