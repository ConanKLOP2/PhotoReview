using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-35 GĐ1: phần user32/gdi32/uxtheme/kernel32 mà hộp thoại Win32 gốc (Shell.Win32/Dialogs/Native) cần. CHỈ THÊM (file mới, cùng
// lớp partial). LibraryImport (L-AOT), chỉ ở Shell.Interop (L-PINV).

internal static unsafe partial class User32
{
    /// <summary>CreateDialogIndirectParamW: tạo hộp thoại modeless từ template trong bộ nhớ (WM_INITDIALOG nhận <paramref name="initParam"/>).</summary>
    [LibraryImport("user32.dll", EntryPoint = "CreateDialogIndirectParamW", SetLastError = true)]
    public static partial nint CreateDialogIndirectParam(nint instance, byte* template, nint parent,
        delegate* unmanaged<nint, uint, nint, nint, nint> dialogProc, nint initParam);

    [LibraryImport("user32.dll", EntryPoint = "IsDialogMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsDialogMessage(nint dialog, Msg* message);

    [LibraryImport("user32.dll", EntryPoint = "EnableWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnableWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [LibraryImport("user32.dll", EntryPoint = "IsWindowEnabled")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowEnabled(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetDlgItem")]
    public static partial nint GetDlgItem(nint dialog, int id);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowText(nint hwnd, string text);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    public static partial int GetWindowTextLength(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true)]
    public static partial int GetWindowText(nint hwnd, char* buffer, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "MoveWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveWindow(nint hwnd, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [LibraryImport("user32.dll", EntryPoint = "SetFocus")]
    public static partial nint SetFocus(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetFocus")]
    public static partial nint GetFocus();

    [LibraryImport("user32.dll", EntryPoint = "GetDC")]
    public static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC")]
    public static partial int ReleaseDC(nint hwnd, nint hdc);

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW")]
    public static partial int DrawText(nint hdc, char* text, int count, Rect* rect, uint format);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoForDpi", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfoForDpi(uint action, uint param, void* data, uint winIni, uint dpi);

    [LibraryImport("user32.dll", EntryPoint = "ScreenToClient")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hwnd, Point* point);

    /// <summary>GetGuiResources (flags GR_GDIOBJECTS = 0, GR_USEROBJECTS = 1): đếm handle của tiến trình (test rò rỉ).</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetGuiResources")]
    public static partial uint GetGuiResources(nint process, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindow")]
    public static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll", EntryPoint = "GetDlgCtrlID")]
    public static partial int GetDlgCtrlID(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "InvalidateRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(nint hwnd, Rect* rect, [MarshalAs(UnmanagedType.Bool)] bool erase);
}

internal static unsafe partial class Gdi32
{
    [LibraryImport("gdi32.dll", EntryPoint = "CreateSolidBrush")]
    public static partial nint CreateSolidBrush(uint colorRef);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll", EntryPoint = "SetTextColor")]
    public static partial uint SetTextColor(nint hdc, uint colorRef);

    [LibraryImport("gdi32.dll", EntryPoint = "SetBkColor")]
    public static partial uint SetBkColor(nint hdc, uint colorRef);

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextColor")]
    public static partial uint GetTextColor(nint hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "GetBkColor")]
    public static partial uint GetBkColor(nint hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "SelectObject")]
    public static partial nint SelectObject(nint hdc, nint obj);

    /// <summary>GetTextExtentPoint32W; <paramref name="size"/> là SIZE (cx, cy) khai báo bằng <see cref="Point"/> (X = cx, Y = cy).</summary>
    [LibraryImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTextExtentPoint32(nint hdc, char* text, int count, Point* size);

    /// <summary>GetTextMetricsW; <paramref name="metrics"/> trỏ tới TEXTMETRICW (60 byte; 5 LONG đầu: Height, Ascent, Descent, InternalLeading, ExternalLeading).</summary>
    [LibraryImport("gdi32.dll", EntryPoint = "GetTextMetricsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTextMetrics(nint hdc, void* metrics);

    /// <summary>CreateFontIndirectW với con trỏ tới LOGFONTW (92 byte).</summary>
    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontIndirectW")]
    public static partial nint CreateFontIndirect(void* logFont);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static partial int GetObject(nint handle, int size, void* buffer);
}

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    public static partial nint GetCurrentProcess();
}

/// <summary>uxtheme.dll (tên công bố): chủ đề điều khiển (DarkMode_Explorer / DarkMode_CFD cho hộp thoại tối).</summary>
internal static partial class Uxtheme
{
    [LibraryImport("uxtheme.dll", EntryPoint = "SetWindowTheme", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SetWindowTheme(nint hwnd, string? subAppName, string? subIdList);
}

/// <summary>Hằng của hộp thoại/điều khiển chuẩn (WinUser.h, CommCtrl.h). Chỉ khai báo.</summary>
internal static class DialogConstants
{
    // Dialog + control messages
    public const uint WmSetFont = 0x0030;
    public const uint WmGetFont = 0x0031;
    public const uint WmInitDialog = 0x0110;
    public const uint WmCtlColorMsgBox = 0x0132;
    public const uint WmCtlColorEdit = 0x0133;
    public const uint WmCtlColorListBox = 0x0134;
    public const uint WmCtlColorBtn = 0x0135;
    public const uint WmCtlColorDlg = 0x0136;
    public const uint WmCtlColorScrollBar = 0x0137;
    public const uint WmCtlColorStatic = 0x0138;
    public const uint DmGetDefId = 0x0400;
    public const uint DmSetDefId = 0x0401;
    public const uint BmSetStyle = 0x00F4;
    public const uint EmSetSel = 0x00B1;
    public const uint EmGetSel = 0x00B0;
    public const uint EmSetLimitText = 0x00C5;
    public const uint EmGetRect = 0x00B2;
    public const uint EmSetRect = 0x00B3;
    public const uint EsMultiline = 0x0004;
    public const uint LbAddString = 0x0180;
    public const uint LbResetContent = 0x0184;
    public const uint LbSetCurSel = 0x0186;
    public const uint LbGetCount = 0x018B;
    public const uint LbGetTextLen = 0x018A;
    public const uint LbGetText = 0x0189;
    public const uint LbSetHorizontalExtent = 0x0194;
    public const uint LbInitStorage = 0x01A8;
    public const uint BnClicked = 0;

    // DWLP_* (x64): DWLP_MSGRESULT 0, DWLP_DLGPROC 8, DWLP_USER 16.
    public const int DwlpMsgResult = 0;
    public const int DwlpUser = 16;

    // Window styles used by the controls
    public const uint WsTabStop = 0x00010000;
    public const uint WsGroup = 0x00020000;
    public const uint WsBorder = 0x00800000;
    public const uint WsVScroll = 0x00200000;
    public const uint WsHScroll = 0x00100000;
    public const uint WsDisabled = 0x08000000;
    public const uint WsDlgFrame = 0x00400000;
    public const uint WsExDlgModalFrame = 0x00000001;
    public const uint WsExClientEdge = 0x00000200;
    public const uint WsExControlParent = 0x00010000;
    public const uint BsPushButton = 0x0000;
    public const uint BsDefPushButton = 0x0001;
    public const uint EsAutoHScroll = 0x0080;
    public const uint SsLeft = 0x0000;
    public const uint SsNoPrefix = 0x0080;
    public const uint LbsNotify = 0x0001;
    public const uint LbsNoIntegralHeight = 0x0100;
    public const uint DsModalFrame = 0x0080;
    public const uint DsSetFont = 0x0040;
    public const uint DsNoFailCreate = 0x0010;

    // DrawText
    public const uint DtCenter = 0x0001;
    public const uint DtVCenter = 0x0004;
    public const uint DtEndEllipsis = 0x8000;
    public const uint DtWordBreak = 0x0010;
    public const uint DtSingleLine = 0x0020;
    public const uint DtCalcRect = 0x0400;
    public const uint DtNoPrefix = 0x0800;
    public const uint DtEditControl = 0x2000;

    // SystemParametersInfo
    public const uint SpiGetNonClientMetrics = 0x0029;
    public const int NonClientMetricsSize = 504;
    public const int NonClientMessageFontOffset = 408;
    public const int LogFontSize = 92;

    // Misc
    public const int GrGdiObjects = 0;
    public const int GrUserObjects = 1;
    public const int TransparentBackground = 1;
}
