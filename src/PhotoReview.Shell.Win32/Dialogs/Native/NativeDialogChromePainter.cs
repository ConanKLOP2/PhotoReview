using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Windowing;
using N = PhotoReview.Shell.Interop.DialogChromeNative;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>Cách <see cref="NativeDialogChromePainter"/> vẽ một điều khiển (theo lớp + kiểu cửa sổ).</summary>
internal enum ChromePaintKind
{
    /// <summary>Không vẽ tay (điều khiển giữ dáng cổ điển của hệ thống: chữ do WM_CTLCOLOR* tô).</summary>
    None = 0,
    PushButton,
    CheckBox,
    RadioButton,
    GroupBox,
    ComboBox,
    Progress,
    Trackbar,
    EtchedStatic,

    /// <summary>Chỉ viền (WM_NCPAINT): ô nhập, listbox, listview. Ruột do WM_CTLCOLOR*/LVM_SETBKCOLOR/NM_CUSTOMDRAW.</summary>
    EditBorder,
    ListBoxBorder,
    ListViewBorder,

    /// <summary>Header của listview: phần thừa bên phải cột cuối (hệ thống tô COLOR_BTNFACE sáng) được tô lại bằng Dark.Surface.</summary>
    HeaderFiller,
}

/// <summary>
/// Giao diện tối của điều khiển hộp thoại Win32 KHÔNG phụ thuộc chủ đề hệ thống (sửa lỗi "nền trắng + chữ sáng" trên Windows 11
/// build 26300: <c>SetPreferredAppMode</c>/<c>FlushMenuThemes</c> ordinal uxtheme và các chủ đề <c>DarkMode_*</c> chạy nửa vời trên
/// desktop thật). Cách làm, như menu (PR #492): chủ đề điều khiển bị xoá (<c>SetWindowTheme("", "")</c> = dáng cổ điển, mọi bộ phận
/// vẽ bằng màu hệ thống đã biết trước) rồi từng điều khiển được vẽ tay bằng bảng màu <c>Dark.*</c> qua subclass:
/// <list type="bullet">
/// <item>nút bấm / hộp chọn / nút radio / group box / ô chọn (phần ô đóng) / thanh tiến trình / thanh trượt / đường kẻ: toàn bộ
/// WM_PAINT (đệm kép), hover do WM_MOUSEMOVE + TrackMouseEvent;</item>
/// <item>ô nhập / listbox / listview: chỉ viền phẳng (WM_NCPAINT, WM_PRINT) - ruột đã do WM_CTLCOLOREDIT/LISTBOX hoặc LVM_SETBKCOLOR;</item>
/// <item>hàng đang chọn của listview: subclass hộp thoại sửa NM_CUSTOMDRAW (bỏ cờ chọn của hệ thống để màu chọn không thành xám sáng
/// + chữ sáng khi listview mất focus).</item>
/// </list>
/// Thanh cuộn giữ dáng cổ điển (không chữ nên luôn đọc được). Title bar tối vẫn qua DWMWA_USE_IMMERSIVE_DARK_MODE (API công bố).
/// Không ném ngoại lệ qua ranh giới unmanaged; thiếu hàm = giữ dáng cổ điển.
/// </summary>
internal static unsafe class NativeDialogChromePainter
{
    private const nuint ControlSubclassId = 0x43484D50; // "CHMP"
    private const nuint DialogSubclassId = 0x43484D44; // "CHMD"
    private const string ControlProp = "PhotoReview.ChromePaint";
    private const string DialogProp = "PhotoReview.ChromeDialog";

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintState
    {
        public int Kind;
        public int Hot;
        public int Tracking;
        public int Reserved;
    }

    // ---- phân loại --------------------------------------------------------------------------------------------------------

    /// <summary>Cách vẽ của điều khiển <paramref name="hwnd"/> thuộc lớp <paramref name="controlClass"/> (đọc kiểu cửa sổ hiện tại).</summary>
    internal static ChromePaintKind KindFor(nint hwnd, DialogControlClass controlClass)
    {
        var style = (uint)User32.GetWindowLongPtr(hwnd, WindowMessages.GwlStyle);
        switch (controlClass)
        {
            case DialogControlClass.Button:
                if ((style & (N.BsIcon | N.BsBitmap)) != 0)
                {
                    return ChromePaintKind.None;
                }

                var pushLike = (style & N.BsPushLike) != 0;
                return (style & N.BsTypeMask) switch
                {
                    0 or 1 => ChromePaintKind.PushButton,
                    2 or 3 or 5 or 6 => pushLike ? ChromePaintKind.PushButton : ChromePaintKind.CheckBox,
                    4 or 9 => pushLike ? ChromePaintKind.PushButton : ChromePaintKind.RadioButton,
                    7 => ChromePaintKind.GroupBox,
                    _ => ChromePaintKind.None,
                };

            case DialogControlClass.ComboBox:
                return (style & N.CbsTypeMask) == 3 && (style & (N.CbsOwnerDrawFixed | N.CbsOwnerDrawVariable)) == 0
                    ? ChromePaintKind.ComboBox
                    : ChromePaintKind.None;

            case DialogControlClass.Edit:
                return ChromePaintKind.EditBorder;

            case DialogControlClass.ListBox:
                return ChromePaintKind.ListBoxBorder;

            case DialogControlClass.ListView:
                return ChromePaintKind.ListViewBorder;

            case DialogControlClass.Progress:
                return (style & N.PbsMarquee) == 0 ? ChromePaintKind.Progress : ChromePaintKind.None;

            case DialogControlClass.Trackbar:
                return (style & N.TbsVert) == 0 ? ChromePaintKind.Trackbar : ChromePaintKind.None;

            case DialogControlClass.Static:
                return (style & N.SsTypeMask) is N.SsEtchedHorz or N.SsEtchedVert or N.SsEtchedFrame
                    ? ChromePaintKind.EtchedStatic
                    : ChromePaintKind.None;

            default:
                return ChromePaintKind.None;
        }
    }

    private static bool IsFullPaint(int kind) => kind is >= (int)ChromePaintKind.PushButton and <= (int)ChromePaintKind.EtchedStatic;

    /// <summary>Kind có viền phẳng vẽ đè ở WM_NCPAINT/WM_PRINT (ruột do hệ thống hoặc do bộ vẽ tay).</summary>
    private static bool HasFlatBorder(int kind)
        => kind is >= (int)ChromePaintKind.EditBorder and <= (int)ChromePaintKind.ListViewBorder or (int)ChromePaintKind.Progress;

    private static bool TracksHover(int kind)
        => kind is (int)ChromePaintKind.PushButton or (int)ChromePaintKind.CheckBox or (int)ChromePaintKind.RadioButton
            or (int)ChromePaintKind.ComboBox or (int)ChromePaintKind.Trackbar;

    // ---- gắn / tháo ---------------------------------------------------------------------------------------------------------

    /// <summary>Gắn bộ vẽ tay cho <paramref name="hwnd"/>; idempotent. False khi <paramref name="kind"/> là <see cref="ChromePaintKind.None"/>.</summary>
    internal static bool Attach(nint hwnd, ChromePaintKind kind)
    {
        if (hwnd == 0 || kind == ChromePaintKind.None || !User32.IsWindow(hwnd))
        {
            return false;
        }

        if (N.GetProp(hwnd, ControlProp) != 0)
        {
            return true;
        }

        var state = (PaintState*)NativeMemory.AllocZeroed((nuint)sizeof(PaintState));
        state->Kind = (int)kind;
        if (!CommonControls.SetWindowSubclass(hwnd, &ControlProc, ControlSubclassId, (nuint)state))
        {
            NativeMemory.Free(state);
            return false;
        }

        N.SetProp(hwnd, ControlProp, (nint)state);

        User32.InvalidateRect(hwnd, null, true);
        return true;
    }

    /// <summary>True khi <paramref name="hwnd"/> đang được bộ vẽ tay quản lý (test).</summary>
    internal static bool IsAttached(nint hwnd)
    {
        return hwnd != 0 && N.GetProp(hwnd, ControlProp) != 0;
    }

    /// <summary>Subclass hộp thoại: chỉnh NM_CUSTOMDRAW của listview con (màu hàng chọn). Idempotent.</summary>
    internal static bool AttachDialog(nint dialog)
    {
        if (dialog == 0 || !User32.IsWindow(dialog))
        {
            return false;
        }

        if (N.GetProp(dialog, DialogProp) != 0)
        {
            return true;
        }

        if (!CommonControls.SetWindowSubclass(dialog, &DialogProc, DialogSubclassId, 0))
        {
            return false;
        }

        N.SetProp(dialog, DialogProp, 1);
        return true;
    }

    // ---- window procs -----------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static nint ControlProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint refData)
    {
        var state = (PaintState*)refData;
        try
        {
            var kind = state->Kind;
            switch (message)
            {
                case N.WmEraseBkgnd when IsFullPaint(kind):
                    return 1;

                case N.WmPaint when IsFullPaint(kind):
                    PaintWindow(hwnd, state);
                    return 0;

                case N.WmPrintClient when IsFullPaint(kind):
                    PaintTo(hwnd, wParam, state);
                    return 0;

                case N.WmMouseMove when TracksHover(kind):
                    if (state->Hot == 0)
                    {
                        state->Hot = 1;
                        byte* track = stackalloc byte[N.TrackMouseEventSize];
                        new Span<byte>(track, N.TrackMouseEventSize).Clear();
                        *(uint*)track = N.TrackMouseEventSize;
                        *(uint*)(track + 4) = N.TmeLeave;
                        *(nint*)(track + 8) = hwnd;
                        _ = N.TrackMouseEvent(track);
                        User32.InvalidateRect(hwnd, null, false);
                    }

                    break;

                case N.WmMouseLeave when TracksHover(kind):
                    state->Hot = 0;
                    User32.InvalidateRect(hwnd, null, false);
                    break;

                case N.WmNcPaint when HasFlatBorder(kind):
                {
                    var result = CommonControls.DefSubclassProc(hwnd, message, wParam, lParam);
                    var hdc = N.GetWindowDC(hwnd);
                    if (hdc != 0)
                    {
                        PaintBorder(hwnd, hdc, kind);
                        _ = User32.ReleaseDC(hwnd, hdc);
                    }

                    return result;
                }

                case N.WmPrint when HasFlatBorder(kind) && ((uint)lParam & N.PrfNonClient) != 0:
                {
                    var result = CommonControls.DefSubclassProc(hwnd, message, wParam, lParam);
                    PaintBorder(hwnd, wParam, kind);
                    return result;
                }

                case N.WmPaint when kind == (int)ChromePaintKind.HeaderFiller:
                {
                    var result = CommonControls.DefSubclassProc(hwnd, message, wParam, lParam);
                    var hdc = User32.GetDC(hwnd);
                    if (hdc != 0)
                    {
                        PaintHeaderFiller(hwnd, hdc);
                        _ = User32.ReleaseDC(hwnd, hdc);
                    }

                    return result;
                }

                case N.WmPrintClient when kind == (int)ChromePaintKind.HeaderFiller:
                {
                    var result = CommonControls.DefSubclassProc(hwnd, message, wParam, lParam);
                    PaintHeaderFiller(hwnd, wParam);
                    return result;
                }

                case N.WmUpdateUiState when IsFullPaint(kind):
                {
                    var result = CommonControls.DefSubclassProc(hwnd, message, wParam, lParam);
                    User32.InvalidateRect(hwnd, null, false);
                    return result;
                }

                case N.WmNcDestroy:
                    CommonControls.RemoveWindowSubclass(hwnd, &ControlProc, id);
                    N.RemoveProp(hwnd, ControlProp);
                    NativeMemory.Free(state);
                    break;
            }
        }
        catch (Exception)
        {
            // Ngoại lệ không được vượt ranh giới unmanaged; điều khiển chỉ mất dáng tối.
        }

        return CommonControls.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static nint DialogProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint refData)
    {
        try
        {
            if (message == ControlConstants.WmNotify && lParam != 0 && *(int*)(lParam + 16) == ControlConstants.NmCustomDraw
                && IsListView(*(nint*)lParam))
            {
                var result = CommonControls.DefSubclassProc(hwnd, message, wParam, lParam);
                return AdjustListViewCustomDraw(lParam, result);
            }

            if (message == N.WmNcDestroy)
            {
                CommonControls.RemoveWindowSubclass(hwnd, &DialogProc, id);
                N.RemoveProp(hwnd, DialogProp);
            }
        }
        catch (Exception)
        {
            // Ngoại lệ không được vượt ranh giới unmanaged.
        }

        return CommonControls.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private static bool IsListView(nint hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        char* name = stackalloc char[16];
        var length = User32.GetClassName(hwnd, name, 16);
        return length == 13 && new ReadOnlySpan<char>(name, length).Equals(ControlConstants.ListViewClass, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Hàng đang chọn của listview: màu chọn của hệ thống (COLOR_HIGHLIGHT, hoặc COLOR_BTNFACE khi listview mất focus + chữ do
    /// <c>LVM_SETTEXTCOLOR</c> = trắng => chữ sáng trên nền sáng) bị thay bằng <c>Dark.Selection</c> + chữ sáng. Cờ CDIS_SELECTED bị
    /// bỏ để listview không tự tô lại. <paramref name="result"/> là kết quả của hộp thoại (CDRF_*), giữ nguyên các cờ của nó.
    /// </summary>
    internal static nint AdjustListViewCustomDraw(nint customDraw, nint result)
    {
        var stage = *(uint*)(customDraw + 24);
        if (stage == ControlConstants.CddsPrePaint)
        {
            return (result & ControlConstants.CdrfSkipDefault) == 0 ? result | ControlConstants.CdrfNotifyItemDraw : result;
        }

        if ((stage & ControlConstants.CddsItem) != 0 && (stage & 0xFFFF) == 1 /* CDDS_PREPAINT trong ngữ cảnh item */)
        {
            // Cờ CDIS_SELECTED của NMCUSTOMDRAW không đáng tin ở dáng cổ điển (thấy 0x201 cho hàng KHÔNG chọn): hỏi trạng thái thật.
            var itemState = (uint*)(customDraw + 64);
            var row = *(nint*)(customDraw + 56);
            var from = *(nint*)customDraw;
            var selected = ((uint)User32.SendMessage(from, ControlConstants.LvmGetItemState, row, (nint)ControlConstants.LvisSelected)
                & ControlConstants.LvisSelected) != 0;
            if (selected)
            {
                *itemState &= ~ControlConstants.CdisSelected;
                *(uint*)(customDraw + 80) = DialogColors.TextStrong;
                *(uint*)(customDraw + 84) = DialogColors.FromKey("Dark.Selection");
                return result | ControlConstants.CdrfNewFont;
            }
        }

        return result;
    }

    // ---- vẽ --------------------------------------------------------------------------------------------------------------

    private static void PaintWindow(nint hwnd, PaintState* state)
    {
        byte* paint = stackalloc byte[N.PaintStructSize];
        var hdc = N.BeginPaint(hwnd, paint);
        try
        {
            if (hdc != 0)
            {
                PaintTo(hwnd, hdc, state);
            }
        }
        finally
        {
            _ = N.EndPaint(hwnd, paint);
        }
    }

    private static void PaintTo(nint hwnd, nint hdc, PaintState* state)
    {
        Rect client = default;
        if (!User32.GetClientRect(hwnd, &client))
        {
            return;
        }

        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var memory = N.CreateCompatibleDC(hdc);
        var bitmap = memory != 0 ? N.CreateCompatibleBitmap(hdc, width, height) : 0;
        if (memory == 0 || bitmap == 0)
        {
            if (bitmap != 0)
            {
                Gdi32.DeleteObject(bitmap);
            }

            if (memory != 0)
            {
                N.DeleteDC(memory);
            }

            Draw(hwnd, hdc, width, height, state);
            return;
        }

        var old = Gdi32.SelectObject(memory, bitmap);
        try
        {
            Draw(hwnd, memory, width, height, state);
            _ = N.BitBlt(hdc, 0, 0, width, height, memory, 0, 0, N.SrcCopy);
        }
        finally
        {
            Gdi32.SelectObject(memory, old);
            Gdi32.DeleteObject(bitmap);
            N.DeleteDC(memory);
        }
    }

    private static void Draw(nint hwnd, nint hdc, int width, int height, PaintState* state)
    {
        var scale = Math.Max(1.0, User32.GetDpiForWindow(hwnd) / 96.0);
        var font = (nint)User32.SendMessage(hwnd, DialogConstants.WmGetFont, 0, 0);
        var oldFont = font != 0 ? Gdi32.SelectObject(hdc, font) : 0;
        try
        {
            var kind = (ChromePaintKind)state->Kind;
            Fill(hdc, 0, 0, width, height, DialogColors.Window);
            switch (kind)
            {
                case ChromePaintKind.PushButton:
                    DrawPushButton(hwnd, hdc, width, height, scale, state->Hot != 0);
                    break;
                case ChromePaintKind.CheckBox:
                case ChromePaintKind.RadioButton:
                    DrawCheckOrRadio(hwnd, hdc, width, height, scale, kind == ChromePaintKind.RadioButton, state->Hot != 0);
                    break;
                case ChromePaintKind.GroupBox:
                    DrawGroupBox(hwnd, hdc, width, height, scale);
                    break;
                case ChromePaintKind.ComboBox:
                    DrawComboBox(hwnd, hdc, width, height, scale, state->Hot != 0);
                    break;
                case ChromePaintKind.Progress:
                    DrawProgress(hwnd, hdc, width, height);
                    break;
                case ChromePaintKind.Trackbar:
                    DrawTrackbar(hwnd, hdc, width, height, scale, state->Hot != 0);
                    break;
                case ChromePaintKind.EtchedStatic:
                    DrawEtched(hwnd, hdc, width, height);
                    break;
            }
        }
        finally
        {
            if (oldFont != 0)
            {
                Gdi32.SelectObject(hdc, oldFont);
            }
        }
    }

    private static int S(double scale, int value) => Math.Max(1, (int)Math.Round(value * scale));

    private static uint Key(string key) => DialogColors.FromKey(key);

    private static void Fill(nint hdc, int left, int top, int right, int bottom, uint color)
    {
        if (right <= left || bottom <= top)
        {
            return;
        }

        var rect = new Rect { Left = left, Top = top, Right = right, Bottom = bottom };
        var brush = Gdi32.CreateSolidBrush(color);
        _ = User32.FillRect(hdc, &rect, brush);
        Gdi32.DeleteObject(brush);
    }

    private static void Frame(nint hdc, int left, int top, int right, int bottom, uint color, int thickness = 1)
    {
        Fill(hdc, left, top, right, top + thickness, color);
        Fill(hdc, left, bottom - thickness, right, bottom, color);
        Fill(hdc, left, top, left + thickness, bottom, color);
        Fill(hdc, right - thickness, top, right, bottom, color);
    }

    private static string WindowText(nint hwnd)
    {
        var length = User32.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            var copied = User32.GetWindowText(hwnd, p, buffer.Length);
            return new string(p, 0, Math.Max(0, copied));
        }
    }

    private static void DrawText(nint hdc, string text, Rect rect, uint color, uint flags)
    {
        if (text.Length == 0)
        {
            return;
        }

        _ = Gdi32.SetBkMode(hdc, DialogConstants.TransparentBackground);
        _ = Gdi32.SetTextColor(hdc, color);
        fixed (char* p = text)
        {
            _ = User32.DrawText(hdc, p, text.Length, &rect, flags);
        }
    }

    private static int TextWidth(nint hdc, string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        Point extent = default;
        fixed (char* p = text)
        {
            _ = Gdi32.GetTextExtentPoint32(hdc, p, text.Length, &extent);
        }

        return extent.X;
    }

    private static int TextHeight(nint hdc)
    {
        Point extent = default;
        fixed (char* p = "Ag")
        {
            _ = Gdi32.GetTextExtentPoint32(hdc, p, 2, &extent);
        }

        return extent.Y;
    }

    private static (bool HideFocus, bool HideAccel) UiState(nint hwnd)
    {
        var state = (uint)User32.SendMessage(hwnd, N.WmQueryUiState, 0, 0);
        return ((state & N.UisfHideFocus) != 0, (state & N.UisfHideAccel) != 0);
    }

    private static void DrawPushButton(nint hwnd, nint hdc, int width, int height, double scale, bool hot)
    {
        var style = (uint)User32.GetWindowLongPtr(hwnd, WindowMessages.GwlStyle);
        var enabled = User32.IsWindowEnabled(hwnd);
        var buttonState = (uint)User32.SendMessage(hwnd, N.BmGetState, 0, 0);
        var pressed = (buttonState & N.BstPushed) != 0;
        var focused = (buttonState & N.BstFocus) != 0;
        var isDefault = (style & N.BsTypeMask) == 1;
        var latched = (style & N.BsPushLike) != 0 && User32.SendMessage(hwnd, ControlConstants.BmGetCheck, 0, 0) == ControlConstants.BstChecked;

        var back = !enabled ? Key("Dark.Button")
            : pressed || latched ? Key("Dark.ButtonPressed")
            : hot ? Key("Dark.ButtonHover")
            : Key("Dark.Button");
        var border = !enabled ? Key("Dark.Border") : isDefault ? Key("Dark.TextSoft") : focused ? Key("Dark.Accent") : Key("Dark.BorderStrong");
        var radius = S(scale, 6);
        var pen = Gdi32.CreatePen(0, 1, border);
        var brush = Gdi32.CreateSolidBrush(back);
        var oldPen = Gdi32.SelectObject(hdc, pen);
        var oldBrush = Gdi32.SelectObject(hdc, brush);
        _ = Gdi32.RoundRect(hdc, 0, 0, width, height, radius, radius);
        Gdi32.SelectObject(hdc, oldPen);
        Gdi32.SelectObject(hdc, oldBrush);
        Gdi32.DeleteObject(pen);
        Gdi32.DeleteObject(brush);

        var ui = UiState(hwnd);
        var offset = pressed ? 1 : 0;
        var textRect = new Rect { Left = S(scale, 4) + offset, Top = offset, Right = width - S(scale, 4) + offset, Bottom = height + offset };
        var flags = (style & N.BsMultiline) != 0
            ? DialogConstants.DtCenter | DialogConstants.DtVCenter | DialogConstants.DtWordBreak
            : DialogConstants.DtCenter | DialogConstants.DtVCenter | DialogConstants.DtSingleLine | DialogConstants.DtEndEllipsis;
        if (ui.HideAccel)
        {
            flags |= N.DtHidePrefix;
        }

        DrawText(hdc, WindowText(hwnd), textRect, enabled ? Key("Dark.TextStrong") : Key("Dark.TextMuted"), flags);
        if (focused && !ui.HideFocus && enabled)
        {
            var focus = new Rect { Left = S(scale, 3), Top = S(scale, 3), Right = width - S(scale, 3), Bottom = height - S(scale, 3) };
            User32.DrawFocusRect(hdc, &focus);
        }
    }

    private static void DrawCheckOrRadio(nint hwnd, nint hdc, int width, int height, double scale, bool radio, bool hot)
    {
        var style = (uint)User32.GetWindowLongPtr(hwnd, WindowMessages.GwlStyle);
        var enabled = User32.IsWindowEnabled(hwnd);
        var buttonState = (uint)User32.SendMessage(hwnd, N.BmGetState, 0, 0);
        var focused = (buttonState & N.BstFocus) != 0;
        var isChecked = User32.SendMessage(hwnd, ControlConstants.BmGetCheck, 0, 0) != ControlConstants.BstUnchecked;
        var multiline = (style & N.BsMultiline) != 0;
        var leftText = (style & N.BsLeftText) != 0;

        var box = S(scale, 13);
        var textHeight = TextHeight(hdc);
        var boxLeft = leftText ? width - box : 0;
        var boxTop = multiline ? Math.Max(0, (textHeight - box) / 2) : Math.Max(0, (height - box) / 2);
        var accent = Key("Dark.Accent");
        var border = !enabled ? Key("Dark.TextFaint") : isChecked ? accent : hot ? Key("Dark.TextMuted") : Key("Dark.BorderStrong");
        var inner = Key("Dark.Input");
        if (radio)
        {
            var pen = Gdi32.CreatePen(0, 1, border);
            var brush = Gdi32.CreateSolidBrush(inner);
            var oldPen = Gdi32.SelectObject(hdc, pen);
            var oldBrush = Gdi32.SelectObject(hdc, brush);
            _ = N.Ellipse(hdc, boxLeft, boxTop, boxLeft + box, boxTop + box);
            Gdi32.SelectObject(hdc, oldPen);
            Gdi32.SelectObject(hdc, oldBrush);
            Gdi32.DeleteObject(pen);
            Gdi32.DeleteObject(brush);
            if (isChecked)
            {
                var dotColor = enabled ? accent : Key("Dark.TextFaint");
                var dotPen = Gdi32.CreatePen(0, 1, dotColor);
                var dotBrush = Gdi32.CreateSolidBrush(dotColor);
                var oldDotPen = Gdi32.SelectObject(hdc, dotPen);
                var oldDotBrush = Gdi32.SelectObject(hdc, dotBrush);
                var inset = Math.Max(3, (int)Math.Round(box * 0.28));
                _ = N.Ellipse(hdc, boxLeft + inset, boxTop + inset, boxLeft + box - inset, boxTop + box - inset);
                Gdi32.SelectObject(hdc, oldDotPen);
                Gdi32.SelectObject(hdc, oldDotBrush);
                Gdi32.DeleteObject(dotPen);
                Gdi32.DeleteObject(dotBrush);
            }
        }
        else
        {
            Fill(hdc, boxLeft, boxTop, boxLeft + box, boxTop + box, isChecked && enabled ? accent : inner);
            Frame(hdc, boxLeft, boxTop, boxLeft + box, boxTop + box, border);
            if (isChecked)
            {
                var markWidth = S(scale, 2);
                var pen = Gdi32.CreatePen(0, markWidth, enabled ? Key("Dark.TextStrong") : Key("Dark.TextFaint"));
                var oldPen = Gdi32.SelectObject(hdc, pen);
                Point* points = stackalloc Point[3];
                points[0] = new Point { X = boxLeft + (int)Math.Round(box * 0.22), Y = boxTop + (int)Math.Round(box * 0.52) };
                points[1] = new Point { X = boxLeft + (int)Math.Round(box * 0.42), Y = boxTop + (int)Math.Round(box * 0.72) };
                points[2] = new Point { X = boxLeft + (int)Math.Round(box * 0.80), Y = boxTop + (int)Math.Round(box * 0.28) };
                _ = N.Polyline(hdc, points, 3);
                Gdi32.SelectObject(hdc, oldPen);
                Gdi32.DeleteObject(pen);
            }
        }

        var gap = S(scale, 6);
        var textRect = leftText
            ? new Rect { Left = 0, Top = 0, Right = Math.Max(0, boxLeft - gap), Bottom = height }
            : new Rect { Left = box + gap, Top = 0, Right = width, Bottom = height };
        var ui = UiState(hwnd);
        var flags = multiline
            ? DialogConstants.DtWordBreak
            : DialogConstants.DtVCenter | DialogConstants.DtSingleLine | DialogConstants.DtEndEllipsis;
        if (ui.HideAccel)
        {
            flags |= N.DtHidePrefix;
        }

        var text = WindowText(hwnd);
        DrawText(hdc, text, textRect, enabled ? Key("Dark.TextStrong") : Key("Dark.TextMuted"), flags);
        if (focused && !ui.HideFocus && enabled)
        {
            var textWidth = Math.Min(textRect.Right - textRect.Left, TextWidth(hdc, text));
            var textTop = multiline ? 0 : Math.Max(0, (height - textHeight) / 2);
            var focus = new Rect
            {
                Left = textRect.Left - 1,
                Top = Math.Max(0, textTop - 1),
                Right = Math.Min(width, textRect.Left + textWidth + 2),
                Bottom = Math.Min(height, textTop + textHeight + 1),
            };
            User32.DrawFocusRect(hdc, &focus);
        }
    }

    private static void DrawGroupBox(nint hwnd, nint hdc, int width, int height, double scale)
    {
        var enabled = User32.IsWindowEnabled(hwnd);
        var text = WindowText(hwnd);
        var textHeight = TextHeight(hdc);
        var top = textHeight / 2;
        Frame(hdc, 0, top, width, height, Key("Dark.BorderStrong"));
        if (text.Length == 0)
        {
            return;
        }

        var pad = S(scale, 3);
        var left = S(scale, 8);
        var textWidth = Math.Min(Math.Max(0, width - left - pad * 2), TextWidth(hdc, text));
        Fill(hdc, left - pad, 0, left + textWidth + pad, textHeight, DialogColors.Window);
        var textRect = new Rect { Left = left, Top = 0, Right = left + textWidth, Bottom = textHeight };
        var flags = DialogConstants.DtSingleLine | DialogConstants.DtEndEllipsis;
        if (UiState(hwnd).HideAccel)
        {
            flags |= N.DtHidePrefix;
        }

        DrawText(hdc, text, textRect, enabled ? Key("Dark.TextStrong") : Key("Dark.TextMuted"), flags);
    }

    private static void DrawComboBox(nint hwnd, nint hdc, int width, int height, double scale, bool hot)
    {
        var enabled = User32.IsWindowEnabled(hwnd);
        var focused = User32.GetFocus() == hwnd;
        var dropped = User32.SendMessage(hwnd, ControlConstants.CbGetDroppedState, 0, 0) != 0;
        var border = !enabled ? Key("Dark.Border") : focused || dropped ? Key("Dark.Accent") : hot ? Key("Dark.TextMuted") : Key("Dark.BorderStrong");
        Fill(hdc, 0, 0, width, height, Key("Dark.Input"));
        Frame(hdc, 0, 0, width, height, border);

        var arrowWidth = Math.Max(height, S(scale, 18));
        var index = (int)User32.SendMessage(hwnd, ControlConstants.CbGetCurSel, 0, 0);
        if (index >= 0)
        {
            var text = ComboBoxControl.TextAt(hwnd, index);
            var textRect = new Rect { Left = S(scale, 6), Top = 1, Right = Math.Max(S(scale, 6), width - arrowWidth), Bottom = height - 1 };
            DrawText(hdc, text, textRect, enabled ? Key("Dark.TextStrong") : Key("Dark.TextMuted"),
                DialogConstants.DtSingleLine | DialogConstants.DtVCenter | DialogConstants.DtNoPrefix | DialogConstants.DtEndEllipsis);
        }

        var cx = width - (arrowWidth / 2) - 1;
        var cy = height / 2;
        var half = S(scale, 4);
        var pen = Gdi32.CreatePen(0, S(scale, 2) > 1 ? 2 : 1, enabled ? Key("Dark.TextSecondary") : Key("Dark.TextFaint"));
        var oldPen = Gdi32.SelectObject(hdc, pen);
        Point* points = stackalloc Point[3];
        points[0] = new Point { X = cx - half, Y = cy - (half / 2) };
        points[1] = new Point { X = cx, Y = cy + (half / 2) + 1 };
        points[2] = new Point { X = cx + half, Y = cy - (half / 2) };
        _ = N.Polyline(hdc, points, 3);
        Gdi32.SelectObject(hdc, oldPen);
        Gdi32.DeleteObject(pen);

        if (focused && !dropped && !UiState(hwnd).HideFocus)
        {
            var focus = new Rect { Left = 3, Top = 3, Right = Math.Max(3, width - arrowWidth), Bottom = height - 3 };
            User32.DrawFocusRect(hdc, &focus);
        }
    }

    private static void DrawProgress(nint hwnd, nint hdc, int width, int height)
    {
        Fill(hdc, 0, 0, width, height, Key("Dark.Surface"));
        var framed = BorderThickness(hwnd).Edge > 0; // viền ngoài do WM_NCPAINT (PaintBorder) vẽ
        var inset = framed ? 0 : 1;
        if (!framed)
        {
            Frame(hdc, 0, 0, width, height, Key("Dark.BorderStrong"));
        }

        var low = (int)User32.SendMessage(hwnd, N.PbmGetRange, 1, 0);
        var high = (int)User32.SendMessage(hwnd, N.PbmGetRange, 0, 0);
        var position = (int)User32.SendMessage(hwnd, ControlConstants.PbmGetPos, 0, 0);
        if (high > low)
        {
            var fraction = Math.Clamp((position - low) / (double)(high - low), 0, 1);
            var fill = (int)Math.Round((width - (2 * inset)) * fraction);
            Fill(hdc, inset, inset, inset + fill, height - inset, Key("Dark.Accent"));
        }
    }

    private static void DrawTrackbar(nint hwnd, nint hdc, int width, int height, double scale, bool hot)
    {
        var enabled = User32.IsWindowEnabled(hwnd);
        var style = (uint)User32.GetWindowLongPtr(hwnd, WindowMessages.GwlStyle);
        Rect channel = default;
        Rect thumb = default;
        User32.SendMessage(hwnd, N.TbmGetChannelRect, 0, (nint)(&channel));
        User32.SendMessage(hwnd, N.TbmGetThumbRect, 0, (nint)(&thumb));
        var thickness = S(scale, 4);
        var channelMid = (channel.Top + channel.Bottom) / 2;
        var channelTop = channelMid - (thickness / 2);
        var trackColor = enabled ? Key("Dark.BorderStrong") : Key("Dark.Border");
        Fill(hdc, channel.Left, channelTop, channel.Right, channelTop + thickness, trackColor);
        var thumbCenter = (thumb.Left + thumb.Right) / 2;
        Fill(hdc, channel.Left, channelTop, Math.Clamp(thumbCenter, channel.Left, channel.Right), channelTop + thickness,
            enabled ? Key("Dark.Accent") : Key("Dark.TextFaint"));

        if ((style & ControlConstants.TbsNoTicks) == 0)
        {
            var count = (int)User32.SendMessage(hwnd, N.TbmGetNumTics, 0, 0);
            var min = (int)User32.SendMessage(hwnd, N.TbmGetRangeMin, 0, 0);
            var max = (int)User32.SendMessage(hwnd, N.TbmGetRangeMax, 0, 0);
            if (count >= 2 && max > min)
            {
                // TBS_AUTOTICKS: hệ thống không lưu mảng vị trí (TBM_GETPTICS trả rác) - đều nhau từ min tới max, count gồm cả hai đầu.
                var auto = (style & ControlConstants.TbsAutoTicks) != 0;
                var inner = auto ? null : (int*)User32.SendMessage(hwnd, N.TbmGetPTics, 0, 0);
                var tickColor = enabled ? Key("Dark.TextMuted") : Key("Dark.TextFaint");
                var top = thumb.Bottom + S(scale, 1);
                var bottom = Math.Min(height, top + S(scale, 4));
                var span = Math.Max(1, channel.Right - channel.Left - 1);
                // Quá dày (tickFrequency 1 trên thang 0..100) thì chỉ vẽ mỗi k vạch để không thành một vệt.
                var minGap = S(scale, 6);
                var step = Math.Max(1, (int)Math.Ceiling(minGap / Math.Max(0.01, span / (double)(count - 1))));
                for (var i = 0; i < count; i += step)
                {
                    var value = auto
                        ? min + (int)Math.Round((double)i * (max - min) / (count - 1))
                        : i == 0 ? min : i == count - 1 ? max : inner != null ? inner[i - 1] : min;
                    var x = channel.Left + (int)Math.Round((value - min) * (double)span / (max - min));
                    Fill(hdc, x, top, x + 1, bottom, tickColor);
                }

                if ((count - 1) % step != 0)
                {
                    Fill(hdc, channel.Left + span, top, channel.Left + span + 1, bottom, tickColor);
                }
            }
        }

        var thumbColor = !enabled ? Key("Dark.TextFaint") : hot ? Key("Dark.AccentHover") : Key("Dark.Accent");
        var thumbBorder = !enabled ? Key("Dark.Border") : Key("Dark.TextSoft");
        Fill(hdc, thumb.Left, thumb.Top, thumb.Right, thumb.Bottom, thumbColor);
        Frame(hdc, thumb.Left, thumb.Top, thumb.Right, thumb.Bottom, thumbBorder);

        if (User32.GetFocus() == hwnd && !UiState(hwnd).HideFocus)
        {
            var focus = new Rect { Left = 0, Top = 0, Right = width, Bottom = height };
            User32.DrawFocusRect(hdc, &focus);
        }
    }

    private static void DrawEtched(nint hwnd, nint hdc, int width, int height)
    {
        var type = (uint)User32.GetWindowLongPtr(hwnd, WindowMessages.GwlStyle) & N.SsTypeMask;
        var color = Key("Dark.Border");
        switch (type)
        {
            case N.SsEtchedHorz:
                Fill(hdc, 0, height / 2, width, (height / 2) + 1, color);
                break;
            case N.SsEtchedVert:
                Fill(hdc, width / 2, 0, (width / 2) + 1, height, color);
                break;
            default:
                Frame(hdc, 0, 0, width, height, color);
                break;
        }
    }

    private static void PaintHeaderFiller(nint header, nint hdc)
    {
        var count = (int)User32.SendMessage(header, N.HdmGetItemCount, 0, 0);
        Rect client = default;
        if (count <= 0 || !User32.GetClientRect(header, &client))
        {
            return;
        }

        Rect last = default;
        if (User32.SendMessage(header, N.HdmGetItemRect, count - 1, (nint)(&last)) == 0 || last.Right >= client.Right)
        {
            return;
        }

        Fill(hdc, last.Right, client.Top, client.Right, client.Bottom, Key("Dark.Surface"));
        Fill(hdc, last.Right, client.Bottom - 1, client.Right, client.Bottom, Key("Dark.BorderSubtle"));
    }

    // ---- viền phẳng (WM_NCPAINT / WM_PRINT) -----------------------------------------------------------------------------

    /// <summary>Độ dày viền (px) của cửa sổ theo kiểu hiện tại: client edge = 2 cạnh, WS_BORDER = 1 cạnh; 0 khi không có.</summary>
    internal static (int Edge, bool ClientEdge) BorderThickness(nint hwnd)
    {
        var style = (uint)User32.GetWindowLongPtr(hwnd, WindowMessages.GwlStyle);
        var exStyle = (uint)User32.GetWindowLongPtr(hwnd, WindowMessages.GwlExStyle);
        var dpi = Math.Max(96u, User32.GetDpiForWindow(hwnd));
        if ((exStyle & N.WsExClientEdge) != 0)
        {
            return (Math.Max(2, User32.GetSystemMetricsForDpi(N.SmCxEdge, dpi)), true);
        }

        if ((exStyle & N.WsExStaticEdge) != 0)
        {
            return (Math.Max(1, User32.GetSystemMetricsForDpi(N.SmCxBorder, dpi)), false);
        }

        if ((style & N.WsBorder) != 0 && (style & N.WsDlgFrame) != N.WsDlgFrame)
        {
            return (Math.Max(1, User32.GetSystemMetricsForDpi(N.SmCxBorder, dpi)), false);
        }

        return (0, false);
    }

    private static void PaintBorder(nint hwnd, nint hdc, int kind)
    {
        var (edge, clientEdge) = BorderThickness(hwnd);
        if (edge <= 0)
        {
            return;
        }

        Rect window = default;
        if (!User32.GetWindowRect(hwnd, &window))
        {
            return;
        }

        var width = window.Right - window.Left;
        var height = window.Bottom - window.Top;
        var outer = Key("Dark.BorderStrong");
        Frame(hdc, 0, 0, width, height, outer);
        if (clientEdge && edge > 1)
        {
            var back = kind == (int)ChromePaintKind.ListViewBorder
                ? (uint)User32.SendMessage(hwnd, N.LvmGetBkColor, 0, 0)
                : kind == (int)ChromePaintKind.Progress ? Key("Dark.Surface") : Key("Dark.Input");
            for (var i = 1; i < edge; i++)
            {
                Frame(hdc, i, i, width - i, height - i, back);
            }
        }
    }
}
