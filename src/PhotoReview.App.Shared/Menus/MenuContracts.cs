using PhotoReview.Core.Settings;

namespace PhotoReview.App.Menus;

// C-12 (NO-WPF-EXEC-PLAN mục 5): mô hình menu dùng chung; builder thực thi ở WP-18. Host native (IPopupMenuHost) ở
// PhotoReview.Shell.Win32.Menus.

public enum MenuItemKind
{
    Command = 0,
    Toggle = 1,
    Submenu = 2,
    Separator = 3,
}

/// <param name="Id">Tên ContextMenuItemId hoặc id con: "Zoom.Preset.150", "Zoom.Custom", "Zoom.AlsoSetClickLevel", ...</param>
/// <param name="Kind">Loại mục.</param>
/// <param name="Text">Chữ hiển thị (đã dịch qua Tr).</param>
/// <param name="ShortcutText">Chữ phím tắt bên phải, hoặc null.</param>
/// <param name="IsEnabled">Bật/tắt.</param>
/// <param name="IsChecked">Dấu chọn (Toggle).</param>
/// <param name="Children">Mục con (Submenu), rỗng nếu không có.</param>
/// <param name="AutomationName">Tên cho accessibility, hoặc null.</param>
public sealed record MenuItemModel(
    string Id,
    MenuItemKind Kind, string Text, string? ShortcutText, bool IsEnabled, bool IsChecked,
    IReadOnlyList<MenuItemModel> Children, string? AutomationName);

public sealed record ContextMenuBuildContext(AppSettings Settings, bool HasImage, bool CanUndo, bool IsFit, double? EffectiveZoom,
    bool CanOpenInExternalEditor, bool CanCopyPath, bool IsCompareVisible);

public static class ContextMenuModelBuilder
{
    /// <summary>
    /// Dùng ContextMenuItems.Compute (Core) - cùng luật separator/ẩn hiện với MainWindow.ApplyContextMenuLayout + BuildZoomMenu.
    /// WP-18 thực thi.
    /// </summary>
    public static IReadOnlyList<MenuItemModel> Build(ContextMenuBuildContext context) => throw new NotImplementedException();

    /// <summary>Recovery, Diagnostics, Benchmark, ClearCache, RemoveNumbered, RemoveOriginal. WP-18 thực thi.</summary>
    public static IReadOnlyList<MenuItemModel> BuildToolsMenu(bool hasImages) => throw new NotImplementedException();
}
