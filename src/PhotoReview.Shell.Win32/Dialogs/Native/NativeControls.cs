using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

// WP-35 GĐ2: các điều khiển bổ sung của khung NativeDialog (combobox, listbox đa chọn owner-draw có huy hiệu, listview report,
// progress, trackbar, checkbox/radio). Mỗi lớp tĩnh chỉ bọc thông điệp của MỘT lớp cửa sổ chuẩn: dialog manager vẫn lo Tab/phím tắt/
// IME/UIA; màu tối do <see cref="DarkDialogChrome"/> (chủ đề uxtheme) và các hàm ApplyColors ở đây.

/// <summary>Cấp một lần mỗi tiến trình các lớp common-control (listview, progress, trackbar, tooltip) trước khi tạo template.</summary>
internal static unsafe class CommonControlsInit
{
    private static int s_done;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref s_done, 1) != 0)
        {
            return;
        }

        uint* init = stackalloc uint[2];
        init[0] = 8; // sizeof(INITCOMMONCONTROLSEX)
        init[1] = ControlConstants.IccListViewClasses | ControlConstants.IccBarClasses | ControlConstants.IccProgressClass
            | ControlConstants.IccWin95Classes | ControlConstants.IccStandardClasses;
        CommonControls.InitCommonControlsEx(init);
    }
}

/// <summary>CBS_DROPDOWNLIST: chọn một mục trong danh sách thả.</summary>
internal static unsafe class ComboBoxControl
{
    public const uint DefaultStyle = ControlConstants.CbsDropDownList | ControlConstants.CbsHasStrings | DialogConstants.WsTabStop
        | DialogConstants.WsVScroll;

    /// <summary>HWND của cửa sổ danh sách thả (một listbox riêng, cũng cần chủ đề tối); 0 khi không lấy được.</summary>
    public static nint ListWindow(nint combo)
    {
        if (combo == 0)
        {
            return 0;
        }

        byte* info = stackalloc byte[ControlConstants.ComboBoxInfoSize];
        new Span<byte>(info, ControlConstants.ComboBoxInfoSize).Clear();
        *(uint*)info = ControlConstants.ComboBoxInfoSize;
        return User32.GetComboBoxInfo(combo, info) ? *(nint*)(info + ControlConstants.ComboBoxInfoListOffset) : 0;
    }

    public static void Add(nint combo, string text)
    {
        fixed (char* p = text)
        {
            User32.SendMessage(combo, ControlConstants.CbAddString, 0, (nint)p);
        }
    }

    public static void Clear(nint combo) => User32.SendMessage(combo, ControlConstants.CbResetContent, 0, 0);

    public static int Count(nint combo) => (int)User32.SendMessage(combo, ControlConstants.CbGetCount, 0, 0);

    public static int SelectedIndex(nint combo) => (int)User32.SendMessage(combo, ControlConstants.CbGetCurSel, 0, 0);

    public static void Select(nint combo, int index) => User32.SendMessage(combo, ControlConstants.CbSetCurSel, index, 0);

    public static bool IsDropped(nint combo) => User32.SendMessage(combo, ControlConstants.CbGetDroppedState, 0, 0) != 0;

    public static void Drop(nint combo, bool show) => User32.SendMessage(combo, ControlConstants.CbShowDropDown, show ? 1 : 0, 0);

    public static string TextAt(nint combo, int index)
    {
        var length = (int)User32.SendMessage(combo, ControlConstants.CbGetLbTextLen, index, 0);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            User32.SendMessage(combo, ControlConstants.CbGetLbText, index, (nint)p);
            return new string(p, 0, length);
        }
    }
}

/// <summary>Listbox (chọn một hoặc nhiều), có thể owner-draw với huy hiệu bên phải (<see cref="NativeDialog.ListItemText"/>).</summary>
internal static unsafe class ListBoxControl
{
    public static int Count(nint list) => (int)User32.SendMessage(list, ControlConstants.LbGetCount, 0, 0);

    public static int Add(nint list, string text)
    {
        fixed (char* p = text)
        {
            return (int)User32.SendMessage(list, DialogConstants.LbAddString, 0, (nint)p);
        }
    }

    public static void Clear(nint list) => User32.SendMessage(list, DialogConstants.LbResetContent, 0, 0);

    /// <summary>Chỉ số dòng chọn (listbox chọn đơn); -1 khi không có.</summary>
    public static int SelectedIndex(nint list) => (int)User32.SendMessage(list, ControlConstants.LbGetCurSel, 0, 0);

    public static void SelectSingle(nint list, int index) => User32.SendMessage(list, ControlConstants.LbSetCurSel, index, 0);

    public static bool IsSelected(nint list, int index) => User32.SendMessage(list, ControlConstants.LbGetSel, index, 0) > 0;

    /// <summary>Đặt chọn cho một dòng của listbox đa chọn.</summary>
    public static void SetSelected(nint list, int index, bool selected)
        => User32.SendMessage(list, ControlConstants.LbSetSel, selected ? 1 : 0, index);

    /// <summary>Chọn/bỏ chọn mọi dòng (listbox đa chọn).</summary>
    public static void SelectAll(nint list, bool selected)
    {
        var count = Count(list);
        if (count > 0)
        {
            User32.SendMessage(list, ControlConstants.LbSelItemRange, selected ? 1 : 0, (nint)((ushort)(count - 1) << 16));
        }
    }

    public static IReadOnlyList<int> SelectedIndices(nint list)
    {
        var result = new List<int>();
        var count = Count(list);
        for (var i = 0; i < count; i++)
        {
            if (IsSelected(list, i))
            {
                result.Add(i);
            }
        }

        return result;
    }

    public static string TextAt(nint list, int index)
    {
        var length = (int)User32.SendMessage(list, ControlConstants.LbGetTextLen, index, 0);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            User32.SendMessage(list, ControlConstants.LbGetText, index, (nint)p);
            return new string(p, 0, length);
        }
    }

    /// <summary>Chỉ số dòng dưới điểm client <paramref name="x"/>,<paramref name="y"/>; -1 khi ngoài vùng dòng.</summary>
    public static int ItemFromPoint(nint list, int x, int y)
    {
        var packed = (uint)User32.SendMessage(list, ControlConstants.LbItemFromPoint, 0, (nint)(((uint)y << 16) | ((uint)x & 0xFFFF)));
        var outside = (packed >> 16) != 0;
        return outside ? -1 : (int)(packed & 0xFFFF);
    }

    /// <summary>Vẽ một dòng owner-draw: nền chọn/thường, chữ bên trái, huy hiệu (viên thuốc) bên phải, khung focus.</summary>
    /// <param name="drawItem">Con trỏ DRAWITEMSTRUCT.</param>
    /// <param name="subtitle">Dòng phụ vẽ dưới tiêu đề (màu phụ); null = một dòng.</param>
    /// <param name="badgeColor">Màu chữ + viền huy hiệu (COLORREF) trên nền Dark.Panel; null = huy hiệu mặc định.</param>
    public static void DrawItem(byte* drawItem, string text, string? badge, nint font, int padX, string? subtitle = null,
        uint? badgeColor = null)
    {
        var itemId = *(int*)(drawItem + 8);
        var state = *(uint*)(drawItem + 16);
        var hdc = *(nint*)(drawItem + 32);
        var rect = *(Rect*)(drawItem + 40);
        var selected = (state & ControlConstants.OdsSelected) != 0;
        var back = selected ? DialogColors.FromKey("Dark.Selection") : DialogColors.Input;

        var brush = Gdi32.CreateSolidBrush(back);
        _ = User32.FillRect(hdc, &rect, brush);
        Gdi32.DeleteObject(brush);
        if (itemId < 0)
        {
            if ((state & ControlConstants.OdsFocus) != 0)
            {
                User32.DrawFocusRect(hdc, &rect);
            }

            return;
        }

        var oldFont = font != 0 ? Gdi32.SelectObject(hdc, font) : 0;
        _ = Gdi32.SetBkMode(hdc, DialogConstants.TransparentBackground);
        var right = rect.Right - padX;
        if (!string.IsNullOrEmpty(badge))
        {
            Point extent = default;
            fixed (char* b = badge)
            {
                _ = Gdi32.GetTextExtentPoint32(hdc, b, badge.Length, &extent);
                var height = rect.Bottom - rect.Top;
                var badgeHeight = Math.Min(height - 4, extent.Y + 2);
                var badgeWidth = extent.X + padX;
                var badgeLeft = right - badgeWidth;
                var badgeTop = rect.Top + ((height - badgeHeight) / 2);
                var pen = Gdi32.CreatePen(0, 1, badgeColor ?? DialogColors.FromKey("Dark.BorderStrong"));
                var fill = Gdi32.CreateSolidBrush(DialogColors.FromKey(badgeColor is null ? "Dark.Surface" : "Dark.Panel"));
                var oldPen = Gdi32.SelectObject(hdc, pen);
                var oldBrush = Gdi32.SelectObject(hdc, fill);
                _ = Gdi32.RoundRect(hdc, badgeLeft, badgeTop, right, badgeTop + badgeHeight, badgeHeight, badgeHeight);
                Gdi32.SelectObject(hdc, oldPen);
                Gdi32.SelectObject(hdc, oldBrush);
                Gdi32.DeleteObject(pen);
                Gdi32.DeleteObject(fill);
                _ = Gdi32.SetTextColor(hdc, badgeColor ?? DialogColors.TextSecondary);
                var badgeRect = new Rect { Left = badgeLeft, Top = badgeTop, Right = right, Bottom = badgeTop + badgeHeight };
                _ = User32.DrawText(hdc, b, badge.Length, &badgeRect,
                    DialogConstants.DtSingleLine | DialogConstants.DtVCenter | DialogConstants.DtCenter | DialogConstants.DtNoPrefix);
                right = badgeLeft - padX;
            }
        }

        var textRect = new Rect { Left = rect.Left + padX, Top = rect.Top, Right = Math.Max(rect.Left + padX, right), Bottom = rect.Bottom };
        var hasSubtitle = !string.IsNullOrEmpty(subtitle);
        if (hasSubtitle)
        {
            // Hai dòng: tiêu đề nửa trên, dòng phụ nửa dưới (màu phụ).
            var middle = rect.Top + ((rect.Bottom - rect.Top) / 2);
            var subRect = new Rect { Left = textRect.Left, Top = middle, Right = textRect.Right, Bottom = rect.Bottom };
            textRect.Bottom = middle;
            _ = Gdi32.SetTextColor(hdc, DialogColors.FromKey("Dark.TextMuted"));
            fixed (char* s = subtitle)
            {
                _ = User32.DrawText(hdc, s, subtitle!.Length, &subRect,
                    DialogConstants.DtSingleLine | DialogConstants.DtVCenter | DialogConstants.DtNoPrefix | DialogConstants.DtEndEllipsis);
            }
        }

        _ = Gdi32.SetTextColor(hdc, DialogColors.TextStrong);
        fixed (char* t = text)
        {
            _ = User32.DrawText(hdc, t, text.Length, &textRect,
                DialogConstants.DtSingleLine | DialogConstants.DtVCenter | DialogConstants.DtNoPrefix | DialogConstants.DtEndEllipsis);
        }

        if (oldFont != 0)
        {
            Gdi32.SelectObject(hdc, oldFont);
        }

        if ((state & ControlConstants.OdsFocus) != 0)
        {
            User32.DrawFocusRect(hdc, &rect);
        }
    }
}

/// <summary>Listview chế độ report (cột, hàng, chọn hàng, mũi tên sắp xếp); màu nền/chữ + chủ đề tối do chrome.</summary>
internal static unsafe class ListViewControl
{
    public const uint DefaultStyle = ControlConstants.LvsReport | ControlConstants.LvsSingleSel | ControlConstants.LvsShowSelAlways
        | DialogConstants.WsTabStop | DialogConstants.WsBorder;

    /// <summary>Kiểu mở rộng + màu nền/chữ của thân (cột header do chủ đề <c>DarkMode_ItemsView</c> vẽ).</summary>
    public static void Configure(nint lv, uint background, uint text)
    {
        User32.SendMessage(lv, ControlConstants.LvmSetExtendedStyle, 0,
            (nint)(ControlConstants.LvsExFullRowSelect | ControlConstants.LvsExDoubleBuffer | ControlConstants.LvsExLabelTip));
        User32.SendMessage(lv, ControlConstants.LvmSetBkColor, 0, (nint)background);
        User32.SendMessage(lv, ControlConstants.LvmSetTextBkColor, 0, (nint)background);
        User32.SendMessage(lv, ControlConstants.LvmSetTextColor, 0, (nint)text);
    }

    public static void AddColumn(nint lv, int index, string text, int width, bool rightAligned)
    {
        byte* column = stackalloc byte[ControlConstants.LvColumnSize];
        new Span<byte>(column, ControlConstants.LvColumnSize).Clear();
        fixed (char* t = text)
        {
            *(uint*)column = ControlConstants.LvcfFmt | ControlConstants.LvcfWidth | ControlConstants.LvcfText | ControlConstants.LvcfSubItem;
            *(int*)(column + 4) = rightAligned ? ControlConstants.LvcfmtRight : ControlConstants.LvcfmtLeft;
            *(int*)(column + 8) = width;
            *(nint*)(column + 16) = (nint)t;
            *(int*)(column + 28) = index;
            User32.SendMessage(lv, ControlConstants.LvmInsertColumn, index, (nint)column);
        }
    }

    public static void SetColumnWidth(nint lv, int index, int width)
        => User32.SendMessage(lv, ControlConstants.LvmSetColumnWidth, index, width);

    public static int ColumnWidth(nint lv, int index) => (int)User32.SendMessage(lv, ControlConstants.LvmGetColumnWidth, index, 0);

    public static void SetColumnText(nint lv, int index, string text)
    {
        // LVM_SETCOLUMNW = 0x1060
        byte* column = stackalloc byte[ControlConstants.LvColumnSize];
        new Span<byte>(column, ControlConstants.LvColumnSize).Clear();
        fixed (char* t = text)
        {
            *(uint*)column = ControlConstants.LvcfText;
            *(nint*)(column + 16) = (nint)t;
            User32.SendMessage(lv, 0x1060, index, (nint)column);
        }
    }

    public static int Count(nint lv) => (int)User32.SendMessage(lv, ControlConstants.LvmGetItemCount, 0, 0);

    public static void Clear(nint lv) => User32.SendMessage(lv, ControlConstants.LvmDeleteAllItems, 0, 0);

    /// <summary>Thêm một hàng ở cuối; <paramref name="param"/> là giá trị người dùng (chỉ số nguồn). Trả chỉ số hàng.</summary>
    public static int AddRow(nint lv, IReadOnlyList<string> cells, nint param)
    {
        byte* item = stackalloc byte[ControlConstants.LvItemSize];
        new Span<byte>(item, ControlConstants.LvItemSize).Clear();
        var index = Count(lv);
        fixed (char* first = cells.Count > 0 ? cells[0] : string.Empty)
        {
            *(uint*)item = ControlConstants.LvifText | ControlConstants.LvifParam;
            *(int*)(item + 4) = index;
            *(nint*)(item + 24) = (nint)first;
            *(nint*)(item + 40) = param;
            index = (int)User32.SendMessage(lv, ControlConstants.LvmInsertItem, 0, (nint)item);
        }

        for (var column = 1; column < cells.Count; column++)
        {
            SetCell(lv, index, column, cells[column]);
        }

        return index;
    }

    public static void SetCell(nint lv, int row, int column, string text)
    {
        byte* item = stackalloc byte[ControlConstants.LvItemSize];
        new Span<byte>(item, ControlConstants.LvItemSize).Clear();
        fixed (char* t = text)
        {
            *(int*)(item + 8) = column;
            *(nint*)(item + 24) = (nint)t;
            User32.SendMessage(lv, ControlConstants.LvmSetItemText, row, (nint)item);
        }
    }

    public static string CellText(nint lv, int row, int column)
    {
        const int capacity = 512;
        var buffer = new char[capacity];
        byte* item = stackalloc byte[ControlConstants.LvItemSize];
        new Span<byte>(item, ControlConstants.LvItemSize).Clear();
        fixed (char* p = buffer)
        {
            *(int*)(item + 8) = column;
            *(nint*)(item + 24) = (nint)p;
            *(int*)(item + 32) = capacity;
            var length = (int)User32.SendMessage(lv, ControlConstants.LvmGetItemText, row, (nint)item);
            return new string(p, 0, Math.Clamp(length, 0, capacity - 1));
        }
    }

    /// <summary>Giá trị người dùng của hàng (chỉ số nguồn đã gắn khi <see cref="AddRow"/>).</summary>
    public static nint RowParam(nint lv, int row)
    {
        byte* item = stackalloc byte[ControlConstants.LvItemSize];
        new Span<byte>(item, ControlConstants.LvItemSize).Clear();
        *(uint*)item = ControlConstants.LvifParam;
        *(int*)(item + 4) = row;
        return User32.SendMessage(lv, ControlConstants.LvmGetItem, 0, (nint)item) != 0 ? *(nint*)(item + 40) : -1;
    }

    /// <summary>Chỉ số hàng đang chọn (đầu tiên); -1 khi không có.</summary>
    public static int SelectedRow(nint lv)
        => (int)User32.SendMessage(lv, ControlConstants.LvmGetNextItem, -1, ControlConstants.LvniSelected);

    public static void SelectRow(nint lv, int row)
    {
        byte* item = stackalloc byte[ControlConstants.LvItemSize];
        new Span<byte>(item, ControlConstants.LvItemSize).Clear();
        *(uint*)(item + 12) = ControlConstants.LvisSelected | ControlConstants.LvisFocused;
        *(uint*)(item + 16) = ControlConstants.LvisSelected | ControlConstants.LvisFocused;
        User32.SendMessage(lv, ControlConstants.LvmSetItemState, row, (nint)item);
        if (row >= 0)
        {
            User32.SendMessage(lv, ControlConstants.LvmEnsureVisible, row, 0);
        }
    }

    public static bool IsRowSelected(nint lv, int row)
        => ((uint)User32.SendMessage(lv, ControlConstants.LvmGetItemState, row, (nint)ControlConstants.LvisSelected) & ControlConstants.LvisSelected) != 0;

    /// <summary>
    /// NM_CUSTOMDRAW của listview: tô màu nền/chữ từng hàng (hàng tốt nhất / lỗi / xen kẽ). <paramref name="header"/> là
    /// NMLVCUSTOMDRAW; <paramref name="rowColors"/>(chỉ số hàng hiển thị, giá trị người dùng) trả (nền, chữ) COLORREF.
    /// Trả giá trị CDRF_* cho DWLP_MSGRESULT.
    /// </summary>
    public static nint CustomDraw(nint header, Func<int, nint, (uint Back, uint Text)> rowColors)
    {
        var stage = *(uint*)(header + 24);
        if (stage == ControlConstants.CddsPrePaint)
        {
            return ControlConstants.CdrfNotifyItemDraw;
        }

        if (stage == ControlConstants.CddsItemPrePaint || stage == (ControlConstants.CddsItemPrePaint | ControlConstants.CddsSubItem))
        {
            var row = (int)*(nint*)(header + 56);
            var param = *(nint*)(header + 72);
            var colors = rowColors(row, param);
            *(uint*)(header + 80) = colors.Text;
            *(uint*)(header + 84) = colors.Back;
            return ControlConstants.CdrfNewFont;
        }

        return ControlConstants.CdrfDoDefault;
    }

    /// <summary>
    /// NM_CUSTOMDRAW của HEADER (listview chuyển lên parent, <c>hwndFrom</c> = header): tự vẽ ô tiêu đề (nền Dark.Surface, chữ sáng,
    /// vạch ngăn, mũi tên sắp xếp) vì chủ đề DarkMode_ItemsView vẽ chữ xám khó đọc. Trả CDRF_*.
    /// </summary>
    public static nint DrawHeaderItem(nint customDraw, int padX, int sortColumn, int sortDirection)
    {
        var stage = *(uint*)(customDraw + 24);
        if (stage == ControlConstants.CddsPrePaint)
        {
            return ControlConstants.CdrfNotifyItemDraw;
        }

        if (stage != ControlConstants.CddsItemPrePaint)
        {
            return ControlConstants.CdrfDoDefault;
        }

        var header = *(nint*)customDraw;
        var hdc = *(nint*)(customDraw + 32);
        var rect = *(Rect*)(customDraw + 40);
        var column = (int)*(nint*)(customDraw + 56);

        const int capacity = 256;
        char* buffer = stackalloc char[capacity];
        byte* item = stackalloc byte[ControlConstants.HdItemSize];
        new Span<byte>(item, ControlConstants.HdItemSize).Clear();
        *(uint*)item = ControlConstants.HdiFormat | 0x2 /* HDI_TEXT */;
        *(nint*)(item + 8) = (nint)buffer;
        *(int*)(item + 24) = capacity;
        var text = string.Empty;
        var fmt = 0;
        if (User32.SendMessage(header, ControlConstants.HdmGetItem, column, (nint)item) != 0)
        {
            fmt = *(int*)(item + 28);
            var pointer = *(char**)(item + 8);
            text = pointer == null ? string.Empty : new string(pointer);
        }

        var back = Gdi32.CreateSolidBrush(DialogColors.FromKey("Dark.Surface"));
        _ = User32.FillRect(hdc, &rect, back);
        Gdi32.DeleteObject(back);
        var line = Gdi32.CreateSolidBrush(DialogColors.FromKey("Dark.BorderSubtle"));
        var separator = new Rect { Left = rect.Right - 1, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
        _ = User32.FillRect(hdc, &separator, line);
        var bottom = new Rect { Left = rect.Left, Top = rect.Bottom - 1, Right = rect.Right, Bottom = rect.Bottom };
        _ = User32.FillRect(hdc, &bottom, line);
        Gdi32.DeleteObject(line);

        var font = (nint)User32.SendMessage(header, DialogConstants.WmGetFont, 0, 0);
        var old = font != 0 ? Gdi32.SelectObject(hdc, font) : 0;
        _ = Gdi32.SetBkMode(hdc, DialogConstants.TransparentBackground);
        _ = Gdi32.SetTextColor(hdc, DialogColors.TextStrong);
        var sortGlyph = column != sortColumn ? null : sortDirection == 1 ? "\u25B2" : sortDirection == 2 ? "\u25BC" : null;
        var textRect = new Rect { Left = rect.Left + padX, Top = rect.Top, Right = rect.Right - padX, Bottom = rect.Bottom - 1 };
        if (sortGlyph is not null)
        {
            textRect.Right -= padX * 2;
            var glyphRect = new Rect { Left = rect.Right - (padX * 3), Top = rect.Top, Right = rect.Right - padX, Bottom = rect.Bottom - 1 };
            _ = Gdi32.SetTextColor(hdc, DialogColors.TextSecondary);
            fixed (char* g = sortGlyph)
            {
                _ = User32.DrawText(hdc, g, sortGlyph.Length, &glyphRect,
                    DialogConstants.DtSingleLine | DialogConstants.DtVCenter | DialogConstants.DtCenter | DialogConstants.DtNoPrefix);
            }

            _ = Gdi32.SetTextColor(hdc, DialogColors.TextStrong);
        }

        var align = (fmt & 0x1 /* HDF_RIGHT */) != 0 ? 0x2u /* DT_RIGHT */ : 0u;
        fixed (char* t = text)
        {
            _ = User32.DrawText(hdc, t, text.Length, &textRect,
                DialogConstants.DtSingleLine | DialogConstants.DtVCenter | DialogConstants.DtNoPrefix | DialogConstants.DtEndEllipsis | align);
        }

        if (old != 0)
        {
            Gdi32.SelectObject(hdc, old);
        }

        return ControlConstants.CdrfSkipDefault;
    }

    public static nint Header(nint lv) => User32.SendMessage(lv, ControlConstants.LvmGetHeader, 0, 0);
}

/// <summary>msctls_progress32 (PBS_SMOOTH).</summary>
internal static class ProgressControl
{
    public const uint DefaultStyle = ControlConstants.PbsSmooth;

    /// <summary>Thang chia 1000: <see cref="SetFraction"/> nhận 0..1.</summary>
    public const int Scale = 1000;

    public static void Configure(nint progress)
    {
        // Màu do chủ đề DarkMode_Explorer (nền tối, vạch sáng): dáng cổ điển + PBM_SETBARCOLOR vẽ khung 3D trắng chói trên nền tối
        // (thấy ở ảnh chụp GĐ2), nên không đặt màu tay.
        User32.SendMessage(progress, ControlConstants.PbmSetRange32, 0, Scale);
    }

    public static void SetFraction(nint progress, double fraction)
    {
        var clamped = double.IsNaN(fraction) ? 0 : Math.Clamp(fraction, 0, 1);
        User32.SendMessage(progress, ControlConstants.PbmSetPos, (nint)Math.Round(clamped * Scale), 0);
    }

    public static double Fraction(nint progress)
        => (double)User32.SendMessage(progress, ControlConstants.PbmGetPos, 0, 0) / Scale;
}

/// <summary>msctls_trackbar32 (thanh trượt ngang) cho Settings về sau.</summary>
internal static class TrackbarControl
{
    public const uint DefaultStyle = ControlConstants.TbsAutoTicks | DialogConstants.WsTabStop;

    public static void Configure(nint trackbar, int minimum, int maximum, int tickFrequency, int lineSize, int pageSize)
    {
        User32.SendMessage(trackbar, ControlConstants.TbmSetRange, 1, (nint)(((uint)maximum << 16) | (ushort)minimum));
        User32.SendMessage(trackbar, ControlConstants.TbmSetTicFreq, tickFrequency, 0);
        User32.SendMessage(trackbar, ControlConstants.TbmSetLineSize, 0, lineSize);
        User32.SendMessage(trackbar, ControlConstants.TbmSetPageSize, 0, pageSize);
    }

    public static int Position(nint trackbar) => (int)User32.SendMessage(trackbar, ControlConstants.TbmGetPos, 0, 0);

    public static void SetPosition(nint trackbar, int position)
        => User32.SendMessage(trackbar, ControlConstants.TbmSetPos, 1, position);
}

/// <summary>Checkbox / radio (BS_AUTOCHECKBOX / BS_AUTORADIOBUTTON).</summary>
internal static class ButtonStateControl
{
    public static bool IsChecked(nint button)
        => User32.SendMessage(button, ControlConstants.BmGetCheck, 0, 0) == ControlConstants.BstChecked;

    public static void SetChecked(nint button, bool value)
        => User32.SendMessage(button, ControlConstants.BmSetCheck, value ? ControlConstants.BstChecked : ControlConstants.BstUnchecked, 0);
}
