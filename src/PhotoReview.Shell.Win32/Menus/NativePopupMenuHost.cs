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
            Populate(root, items, ids, MenuCreated);
            var (x, y) = ToScreenPixels(screenPointDip, User32.GetDpiForWindow(owner));
            var command = User32.TrackPopupMenuEx(root, WindowMessages.TpmReturnCmd | WindowMessages.TpmRightButton, x, y, owner, 0);
            return NativeMenuMapping.IdForCommand(ids, command);
        }
        finally
        {
            _showing = false;
            User32.DestroyMenu(root); // huỷ cả submenu con
        }
    }

    /// <summary>Test: gọi với mỗi HMENU (gốc và submenu) vừa tạo, để kiểm mọi handle đều được huỷ sau khi menu đóng (WP-24).</summary>
    internal Action<nint>? MenuCreated { get; set; }

    /// <summary>DIP (theo DPI của cửa sổ chủ) -> pixel màn hình; dpi = 0 (cửa sổ không đọc được) coi như 96.</summary>
    internal static (int X, int Y) ToScreenPixels(PointD dip, uint dpi)
    {
        var scale = dpi == 0 ? 1.0 : dpi / BaseDpi;
        return ((int)Math.Round(dip.X * scale), (int)Math.Round(dip.Y * scale));
    }

    private static void Populate(nint menu, IReadOnlyList<MenuItemModel> items, List<string> ids, Action<nint>? created)
    {
        created?.Invoke(menu);
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
                Populate(sub, item.Children, ids, created);
                User32.AppendMenu(menu, flags | WindowMessages.MfPopup, (nuint)sub, text);
                continue;
            }

            ids.Add(item.Id);
            User32.AppendMenu(menu, flags, (nuint)ids.Count, text); // command id = vị trí + 1 trong ids (0 = huỷ)
        }
    }
}
