using PhotoReview.App.Input;
using PhotoReview.App.Menus;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Menus;

/// <summary>
/// C-12 host (NE-4 = a): menu native <c>CreatePopupMenu</c> + <c>TrackPopupMenuEx</c> với <c>TPM_RETURNCMD</c>. Modal: bơm message
/// của chính hàm Win32 nên bàn phím, accessibility, submenu và đa màn hình dùng của hệ điều hành. Tối: <see cref="DarkModeMenus"/>
/// (rơi về menu sáng nếu thiếu API). Chạy trên thread giao diện của cửa sổ chủ.
/// </summary>
internal sealed class NativePopupMenuHost : IPopupMenuHost
{
    private const double BaseDpi = 96.0;

    private readonly Func<nint> _ownerHwnd;
    private readonly DarkModeMenus _darkMode;
    private bool _showing;

    /// <param name="ownerHwnd">Cửa sổ chủ của menu (cửa sổ xem ảnh); 0 thì <see cref="Show"/> trả null.</param>
    internal NativePopupMenuHost(Func<nint> ownerHwnd)
        : this(ownerHwnd, DarkModeMenus.CreateForCurrentSystem())
    {
    }

    internal NativePopupMenuHost(Func<nint> ownerHwnd, DarkModeMenus darkMode)
    {
        ArgumentNullException.ThrowIfNull(ownerHwnd);
        ArgumentNullException.ThrowIfNull(darkMode);
        _ownerHwnd = ownerHwnd;
        _darkMode = darkMode;
    }

    /// <summary>Hiện menu tại <paramref name="screenPointDip"/> (DIP của DPI cửa sổ chủ) và trả id mục chọn, hoặc null nếu bỏ.</summary>
    public string? Show(IReadOnlyList<MenuItemModel> items, PointD screenPointDip)
    {
        ArgumentNullException.ThrowIfNull(items);
        var owner = _ownerHwnd();
        if (owner == 0 || _showing || items.Count == 0) return null;

        _darkMode.TryEnable();
        _darkMode.TryAllowForWindow(owner);

        var ids = new List<string>();
        var root = User32.CreatePopupMenu();
        if (root == 0) return null;
        _showing = true;
        try
        {
            Populate(root, items, ids);
            var dpi = User32.GetDpiForWindow(owner);
            var scale = dpi == 0 ? 1.0 : dpi / BaseDpi;
            var x = (int)Math.Round(screenPointDip.X * scale);
            var y = (int)Math.Round(screenPointDip.Y * scale);
            var command = User32.TrackPopupMenuEx(root, WindowMessages.TpmReturnCmd | WindowMessages.TpmRightButton, x, y, owner, 0);
            return NativeMenuMapping.IdForCommand(ids, command);
        }
        finally
        {
            _showing = false;
            User32.DestroyMenu(root); // huỷ cả submenu con
        }
    }

    private static void Populate(nint menu, IReadOnlyList<MenuItemModel> items, List<string> ids)
    {
        foreach (var item in items)
        {
            if (item.Kind == MenuItemKind.Separator)
            {
                User32.AppendMenu(menu, WindowMessages.MfSeparator, 0, null);
                continue;
            }

            var flags = NativeMenuMapping.ItemFlags(item);
            var text = NativeMenuMapping.DisplayText(item);
            if (item.Kind == MenuItemKind.Submenu && item.Children.Count > 0)
            {
                var sub = User32.CreatePopupMenu();
                if (sub == 0) continue;
                Populate(sub, item.Children, ids);
                User32.AppendMenu(menu, flags | WindowMessages.MfPopup, (nuint)sub, text);
                continue;
            }

            ids.Add(item.Id);
            User32.AppendMenu(menu, flags, (nuint)ids.Count, text); // command id = vị trí + 1 trong ids (0 = huỷ)
        }
    }
}
