using PhotoReview.App.Menus;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Menus;

/// <summary>Phần thuần (không gọi OS) của host menu native: chữ hiển thị, cờ MF_*, và đổi command id về id mô hình.</summary>
internal static class NativeMenuMapping
{
    /// <summary>
    /// Chữ trên menu: '&amp;' nhân đôi (khỏi bị coi là phím gọi nhanh) và phím tắt đi sau một TAB (hiện căn phải như accelerator).
    /// </summary>
    internal static string DisplayText(MenuItemModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var text = item.Text.Replace("&", "&&", StringComparison.Ordinal);
        return string.IsNullOrEmpty(item.ShortcutText) ? text : text + "\t" + item.ShortcutText.Replace("&", "&&", StringComparison.Ordinal);
    }

    /// <summary>MF_STRING | MF_GRAYED khi tắt | MF_CHECKED khi được chọn (Toggle).</summary>
    internal static uint ItemFlags(MenuItemModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var flags = WindowMessages.MfString;
        if (!item.IsEnabled) flags |= WindowMessages.MfGrayed;
        if (item.IsChecked) flags |= WindowMessages.MfChecked;
        return flags;
    }

    /// <summary>Command id 1..N trả về từ TrackPopupMenuEx (0 = huỷ) -> id mô hình; null nếu 0 hoặc ngoài khoảng.</summary>
    internal static string? IdForCommand(IReadOnlyList<string> ids, int command)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return command >= 1 && command <= ids.Count ? ids[command - 1] : null;
    }
}
