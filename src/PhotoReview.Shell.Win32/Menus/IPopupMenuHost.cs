using PhotoReview.App.Input;
using PhotoReview.App.Menus;

namespace PhotoReview.Shell.Win32.Menus;

/// <summary>C-12 host (NO-WPF-EXEC-PLAN mục 5): menu native, modal; trả Id được chọn hoặc null. Thực thi ở WP-18.</summary>
public interface IPopupMenuHost
{
    string? Show(IReadOnlyList<MenuItemModel> items, PointD screenPointDip);
}
