using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Win32.Menus;

/// <summary>Một mục menu vẽ tay: chữ, phím tắt và loại (dấu phân cách / submenu).</summary>
internal sealed record OwnerDrawItem(string Text, string? Shortcut, bool IsSeparator, bool IsSubmenu);

/// <summary>
/// Vẽ menu native bằng owner-draw trong lúc <c>TrackPopupMenuEx</c> chạy: tạm "subclass" cửa sổ chủ để nhận WM_MEASUREITEM/WM_DRAWITEM
/// (gửi tới chủ của menu), vẽ nền/chữ/hover/dấu chọn/mũi tên bằng GDI theo <see cref="MenuOwnerDraw"/>. Không dùng uxtheme ordinal.
/// Bàn phím, submenu, đa màn hình vẫn do chính menu Win32 lo. Tạo, <see cref="Attach"/> trước khi dựng menu, <see cref="Dispose"/> sau.
/// </summary>
internal sealed unsafe partial class OwnerDrawMenuSession : IDisposable
{
    private const uint WmDrawItem = 0x002B;
    private const uint WmMeasureItem = 0x002C;
    private const uint OdtMenu = 1;
    private const nuint SubclassId = 0x50524D4E; // "PRMN"
    private const int TransparentBk = 1;
    private const uint DtLeft = 0x0000, DtRight = 0x0002, DtVCenter = 0x0004, DtSingleLine = 0x0020, DtNoPrefix = 0x0800, DtEndEllipsis = 0x8000;

    private readonly List<OwnerDrawItem> _items = [];
    private readonly nint _owner;
    private readonly uint _dpi;
    private nint _font;
    private nint _backgroundBrush;
    private GCHandle _self;
    private bool _attached;

    internal OwnerDrawMenuSession(nint owner, uint dpi)
    {
        _owner = owner;
        _dpi = dpi;
        _font = CreateMenuFont(dpi);
        _backgroundBrush = CreateSolidBrush(MenuOwnerDraw.Background);
    }

    /// <summary>Brush nền (hbrBack của mọi menu: viền/đệm quanh mục cùng màu nền).</summary>
    internal nint BackgroundBrush => _backgroundBrush;

    /// <summary>Đã gắn vào cửa sổ chủ (nếu SetWindowSubclass thất bại thì menu rơi về dáng hệ thống).</summary>
    internal bool IsAttached => _attached;

    internal bool Attach()
    {
        _self = GCHandle.Alloc(this);
        _attached = SetWindowSubclass(_owner, &SubclassProc, SubclassId, (nuint)GCHandle.ToIntPtr(_self));
        return _attached;
    }

    /// <summary>Đăng ký một mục, trả "itemData" (chỉ số + 1) để đưa vào AppendMenu.</summary>
    internal nint Register(OwnerDrawItem item)
    {
        _items.Add(item);
        return _items.Count;
    }

    public void Dispose()
    {
        if (_attached)
        {
            RemoveWindowSubclass(_owner, &SubclassProc, SubclassId);
            _attached = false;
        }

        if (_self.IsAllocated) _self.Free();
        if (_font != 0) { _ = DeleteObject(_font); _font = 0; }
        if (_backgroundBrush != 0) { _ = DeleteObject(_backgroundBrush); _backgroundBrush = 0; }
    }

    [UnmanagedCallersOnly]
    private static nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint refData)
    {
        if (message is WmMeasureItem or WmDrawItem)
        {
            try
            {
                if (GCHandle.FromIntPtr((nint)refData).Target is OwnerDrawMenuSession session
                    && (message == WmMeasureItem ? session.Measure(lParam) : session.Draw(lParam)))
                {
                    return 1;
                }
            }
            catch (Exception)
            {
                // Không được ném qua ranh giới unmanaged: để menu vẽ theo mặc định.
            }
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private bool Measure(nint lParam)
    {
        var m = (MeasureItemStruct*)lParam;
        if (m->CtlType != OdtMenu || !TryItem(m->ItemData, out var item)) return false;
        if (item.IsSeparator)
        {
            m->ItemWidth = (uint)MenuOwnerDraw.Scale(48, _dpi);
            m->ItemHeight = (uint)MenuOwnerDraw.SeparatorHeight(_dpi);
            return true;
        }

        var dc = GetDC(_owner);
        var old = SelectObject(dc, _font);
        var text = TextWidth(dc, item.Text, out var textHeight);
        var shortcut = string.IsNullOrEmpty(item.Shortcut) ? 0 : TextWidth(dc, item.Shortcut, out _);
        _ = SelectObject(dc, old);
        _ = ReleaseDC(_owner, dc);
        m->ItemWidth = (uint)MenuOwnerDraw.ItemWidth(text, shortcut, _dpi);
        m->ItemHeight = (uint)MenuOwnerDraw.ItemHeight(textHeight, _dpi);
        return true;
    }

    private bool Draw(nint lParam)
    {
        var d = (DrawItemStruct*)lParam;
        if (d->CtlType != OdtMenu || !TryItem(d->ItemData, out var item)) return false;
        var rc = d->Rc;
        var dc = d->Hdc;
        FillSolid(dc, rc, item.IsSeparator ? MenuOwnerDraw.Background : MenuOwnerDraw.ItemBackground(d->ItemState));
        if (item.IsSeparator)
        {
            var mid = rc.Top + ((rc.Bottom - rc.Top) / 2);
            FillSolid(dc, new Rect { Left = rc.Left + MenuOwnerDraw.Scale(8, _dpi), Right = rc.Right - MenuOwnerDraw.Scale(8, _dpi), Top = mid, Bottom = mid + 1 },
                MenuOwnerDraw.Separator);
            return true;
        }

        var enabled = (d->ItemState & (MenuOwnerDraw.OdsGrayed | MenuOwnerDraw.OdsDisabled)) == 0;
        var color = MenuOwnerDraw.ItemText(d->ItemState);
        var old = SelectObject(dc, _font);
        _ = SetBkMode(dc, TransparentBk);
        _ = SetTextColor(dc, color);

        var textRect = new Rect { Left = rc.Left + MenuOwnerDraw.LeftGutter(_dpi), Top = rc.Top, Right = rc.Right - MenuOwnerDraw.RightPad(_dpi), Bottom = rc.Bottom };
        var shortcutRight = textRect.Right - (item.IsSubmenu ? MenuOwnerDraw.Scale(16, _dpi) : 0);
        if (!string.IsNullOrEmpty(item.Shortcut))
        {
            var shortcutRect = textRect;
            shortcutRect.Right = shortcutRight;
            _ = SetTextColor(dc, enabled ? MenuOwnerDraw.ColorRef("Dark.TextSecondary") : color);
            _ = DrawText(dc, item.Shortcut, item.Shortcut.Length, ref shortcutRect, DtRight | DtVCenter | DtSingleLine | DtNoPrefix);
            _ = SetTextColor(dc, color);
        }

        _ = DrawText(dc, item.Text, item.Text.Length, ref textRect, DtLeft | DtVCenter | DtSingleLine | DtNoPrefix | DtEndEllipsis);
        _ = SelectObject(dc, old);

        var midY = rc.Top + ((rc.Bottom - rc.Top) / 2);
        if ((d->ItemState & MenuOwnerDraw.OdsChecked) != 0)
        {
            var cx = rc.Left + MenuOwnerDraw.Scale(14, _dpi);
            StrokeLines(dc, color, cx - MenuOwnerDraw.Scale(4, _dpi), midY, cx - MenuOwnerDraw.Scale(1, _dpi), midY + MenuOwnerDraw.Scale(3, _dpi),
                cx + MenuOwnerDraw.Scale(4, _dpi), midY - MenuOwnerDraw.Scale(4, _dpi));
        }

        if (item.IsSubmenu)
        {
            var ax = rc.Right - MenuOwnerDraw.Scale(12, _dpi);
            var h = MenuOwnerDraw.Scale(4, _dpi);
            StrokeLines(dc, color, ax - h, midY - h, ax, midY, ax - h, midY + h);
            // Lưu ý: hệ thống còn vẽ mũi tên đen của nó sau WM_DRAWITEM (không tắt được): đen trên nền tối nên gần như vô hình.
        }

        return true;
    }

    private bool TryItem(nuint itemData, out OwnerDrawItem item)
    {
        var index = (int)itemData - 1;
        if (index >= 0 && index < _items.Count)
        {
            item = _items[index];
            return true;
        }

        item = null!;
        return false;
    }

    private static void FillSolid(nint dc, Rect rect, uint colorRef)
    {
        var brush = CreateSolidBrush(colorRef);
        _ = FillRect(dc, ref rect, brush);
        _ = DeleteObject(brush);
    }

    /// <summary>Hai đoạn thẳng a-b-c (dấu chọn, mũi tên) bằng bút GDI dày theo DPI.</summary>
    private void StrokeLines(nint dc, uint colorRef, int ax, int ay, int bx, int by, int cx, int cy)
    {
        var pen = CreatePen(0, Math.Max(1, MenuOwnerDraw.Scale(1, _dpi)), colorRef);
        var old = SelectObject(dc, pen);
        _ = MoveToEx(dc, ax, ay, 0);
        _ = LineTo(dc, bx, by);
        _ = LineTo(dc, cx, cy);
        _ = SelectObject(dc, old);
        _ = DeleteObject(pen);
    }

    private static int TextWidth(nint dc, string text, out int height)
    {
        if (text.Length == 0)
        {
            height = 0;
            return 0;
        }

        _ = GetTextExtentPoint32(dc, text, text.Length, out var size);
        height = size.Cy;
        return size.Cx;
    }

    /// <summary>Font menu của hệ thống (NONCLIENTMETRICS.lfMenuFont theo DPI); lỗi thì font GUI mặc định.</summary>
    private static nint CreateMenuFont(uint dpi)
    {
        const int metricsSize = 504; // sizeof(NONCLIENTMETRICSW) từ Vista; lfMenuFont ở offset 224 (LOGFONTW = 92 byte)
        const int menuFontOffset = 224;
        var buffer = stackalloc byte[metricsSize];
        new Span<byte>(buffer, metricsSize).Clear();
        *(uint*)buffer = metricsSize;
        var ok = dpi != 0 ? SystemParametersInfoForDpi(0x0029, metricsSize, buffer, 0, dpi) : SystemParametersInfo(0x0029, metricsSize, buffer, 0);
        if (ok)
        {
            var font = CreateFontIndirect(buffer + menuFontOffset);
            if (font != 0) return font;
        }

        return GetStockObject(17); // DEFAULT_GUI_FONT (đối tượng hệ thống, DeleteObject là no-op)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeasureItemStruct
    {
        public uint CtlType;
        public uint CtlId;
        public uint ItemId;
        public uint ItemWidth;
        public uint ItemHeight;
        public nuint ItemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrawItemStruct
    {
        public uint CtlType;
        public uint CtlId;
        public uint ItemId;
        public uint ItemAction;
        public uint ItemState;
        public nint HwndItem;
        public nint Hdc;
        public Rect Rc;
        public nuint ItemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeNative
    {
        public int Cx;
        public int Cy;
    }

    [LibraryImport("comctl32.dll", EntryPoint = "SetWindowSubclass")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint refData);

    [LibraryImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id);

    [LibraryImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetDC")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("user32.dll", EntryPoint = "FillRect")]
    private static partial int FillRect(nint dc, ref Rect rect, nint brush);

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DrawText(nint dc, string text, int count, ref Rect rect, uint format);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoForDpi", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoForDpi(uint action, uint param, byte* data, uint winIni, uint dpi);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, byte* data, uint winIni);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateSolidBrush")]
    private static partial nint CreateSolidBrush(uint colorRef);

    [LibraryImport("gdi32.dll", EntryPoint = "CreatePen")]
    private static partial nint CreatePen(int style, int width, uint colorRef);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontIndirectW")]
    private static partial nint CreateFontIndirect(byte* logFont);

    [LibraryImport("gdi32.dll", EntryPoint = "GetStockObject")]
    private static partial nint GetStockObject(int index);

    [LibraryImport("gdi32.dll", EntryPoint = "SelectObject")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")]
    private static partial int DeleteObject(nint obj);

    [LibraryImport("gdi32.dll", EntryPoint = "SetBkMode")]
    private static partial int SetBkMode(nint dc, int mode);

    [LibraryImport("gdi32.dll", EntryPoint = "SetTextColor")]
    private static partial uint SetTextColor(nint dc, uint colorRef);

    [LibraryImport("gdi32.dll", EntryPoint = "MoveToEx")]
    private static partial int MoveToEx(nint dc, int x, int y, nint previous);

    [LibraryImport("gdi32.dll", EntryPoint = "LineTo")]
    private static partial int LineTo(nint dc, int x, int y);

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetTextExtentPoint32(nint dc, string text, int count, out SizeNative size);
}
