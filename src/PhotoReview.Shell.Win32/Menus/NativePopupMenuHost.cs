using System.Runtime.InteropServices;
using PhotoReview.App.Input;
using PhotoReview.App.Menus;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Menus;

/// <summary>
/// C-12 host (NE-4 = a): menu native <c>CreatePopupMenu</c> + <c>TrackPopupMenuEx</c> với <c>TPM_RETURNCMD</c>. Modal: bơm message
/// của chính hàm Win32 nên bàn phím, accessibility, submenu và đa màn hình dùng của hệ điều hành. Giao diện tối do CHÍNH APP vẽ
/// (owner-draw, <see cref="OwnerDrawMenuSession"/>), không còn gọi uxtheme ordinal 135/136/133 (trên Windows 11 build 26300 chúng làm
/// chữ sáng trên nền trắng). Chạy trên thread giao diện của cửa sổ chủ.
/// </summary>
internal sealed partial class NativePopupMenuHost : IPopupMenuHost
{
    private const double BaseDpi = 96.0;
    private const uint MfOwnerDraw = 0x00000100;
    private const uint MimBackground = 0x00000002;
    private const uint MimApplyToSubmenus = 0x80000000;

    private readonly Func<nint> _ownerHwnd;
    private bool _showing;

    /// <param name="ownerHwnd">Cửa sổ chủ của menu (cửa sổ xem ảnh); 0 thì <see cref="Show"/> trả null.</param>
    internal NativePopupMenuHost(Func<nint> ownerHwnd)
    {
        ArgumentNullException.ThrowIfNull(ownerHwnd);
        _ownerHwnd = ownerHwnd;
    }

    /// <summary>Hiện menu tại <paramref name="screenPointDip"/> (DIP của DPI cửa sổ chủ) và trả id mục chọn, hoặc null nếu bỏ.</summary>
    public string? Show(IReadOnlyList<MenuItemModel> items, PointD screenPointDip)
    {
        ArgumentNullException.ThrowIfNull(items);
        var owner = _ownerHwnd();
        if (owner == 0 || _showing || items.Count == 0) return null;

        var dpi = User32.GetDpiForWindow(owner);
        var ids = new List<string>();
        using var session = new OwnerDrawMenuSession(owner, dpi);
        var ownerDraw = session.Attach();
        var root = User32.CreatePopupMenu();
        if (root == 0) return null;
        _showing = true;
        try
        {
            Populate(root, items, ids, MenuCreated, ownerDraw ? session : null);
            var (x, y) = ToScreenPixels(screenPointDip, dpi);
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

    private static unsafe void StyleMenu(nint menu, OwnerDrawMenuSession session)
    {
        var info = new MenuInfo
        {
            Size = (uint)sizeof(MenuInfo),
            Mask = MimBackground | MimApplyToSubmenus,
            BackgroundBrush = session.BackgroundBrush,
        };
        _ = SetMenuInfo(menu, ref info);
    }

    private static void Populate(nint menu, IReadOnlyList<MenuItemModel> items, List<string> ids, Action<nint>? created,
        OwnerDrawMenuSession? session)
    {
        created?.Invoke(menu);
        if (session is not null) StyleMenu(menu, session);
        foreach (var item in items)
        {
            if (item.Kind == MenuItemKind.Separator)
            {
                if (session is null)
                {
                    User32.AppendMenu(menu, WindowMessages.MfSeparator, 0, null);
                }
                else
                {
                    _ = AppendMenuData(menu, WindowMessages.MfSeparator | MfOwnerDraw, 0,
                        session.Register(new OwnerDrawItem(string.Empty, null, true, false)));
                }

                continue;
            }

            var flags = NativeMenuMapping.ItemFlags(item);
            if (item.Kind == MenuItemKind.Submenu && item.Children.Count > 0)
            {
                var sub = User32.CreatePopupMenu();
                if (sub == 0) continue;
                Populate(sub, item.Children, ids, created, session);
                AppendItem(menu, flags | WindowMessages.MfPopup, (nuint)sub, item, session);
                continue;
            }

            ids.Add(item.Id);
            AppendItem(menu, flags, (nuint)ids.Count, item, session); // command id = vị trí + 1 trong ids (0 = huỷ)
        }
    }

    private static void AppendItem(nint menu, uint flags, nuint idOrSubmenu, MenuItemModel item, OwnerDrawMenuSession? session)
    {
        if (session is null)
        {
            User32.AppendMenu(menu, flags, idOrSubmenu, NativeMenuMapping.DisplayText(item));
            return;
        }

        var data = session.Register(new OwnerDrawItem(item.Text, item.ShortcutText, false, (flags & WindowMessages.MfPopup) != 0));
        _ = AppendMenuData(menu, flags | MfOwnerDraw, idOrSubmenu, data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MenuInfo
    {
        public uint Size;
        public uint Mask;
        public uint Style;
        public uint MaxHeight;
        public nint BackgroundBrush;
        public uint ContextHelpId;
        public nuint MenuData;
    }

    /// <summary>AppendMenu với lpNewItem là giá trị của app (itemData của WM_MEASUREITEM/WM_DRAWITEM) khi có MF_OWNERDRAW.</summary>
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuData(nint menu, uint flags, nuint idOrSubmenu, nint itemData);

    [LibraryImport("user32.dll", EntryPoint = "SetMenuInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMenuInfo(nint menu, ref MenuInfo info);
}
