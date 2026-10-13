using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// Giao diện tối của hộp thoại KHÔNG phụ thuộc chủ đề hệ thống: phần user32/gdi32/comctl32 mà bộ vẽ tay của NativeDialogChrome cần
// (WM_PAINT/WM_NCPAINT của nút, ô chọn, thanh tiến trình, thanh trượt, viền ô nhập). Lớp riêng (không mở rộng User32/Gdi32) để không
// trùng tên với các file interop khác. CHỈ THÊM. LibraryImport (L-AOT), chỉ ở Shell.Interop (L-PINV).

internal static unsafe partial class DialogChromeNative
{
    // ---- user32 ----

    /// <summary>BeginPaint: <paramref name="paintStruct"/> trỏ tới PAINTSTRUCT (72 byte x64).</summary>
    [LibraryImport("user32.dll", EntryPoint = "BeginPaint")]
    public static partial nint BeginPaint(nint hwnd, byte* paintStruct);

    [LibraryImport("user32.dll", EntryPoint = "EndPaint")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(nint hwnd, byte* paintStruct);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowDC")]
    public static partial nint GetWindowDC(nint hwnd);

    /// <summary>TrackMouseEvent: <paramref name="track"/> trỏ tới TRACKMOUSEEVENT (24 byte x64).</summary>
    [LibraryImport("user32.dll", EntryPoint = "TrackMouseEvent")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TrackMouseEvent(byte* track);

    // ---- gdi32 ----

    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleBitmap")]
    public static partial nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "BitBlt")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY,
        uint rop);

    [LibraryImport("gdi32.dll", EntryPoint = "Ellipse")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Ellipse(nint hdc, int left, int top, int right, int bottom);

    /// <summary>Polyline: <paramref name="points"/> là mảng <c>Point</c> (POINT).</summary>
    [LibraryImport("gdi32.dll", EntryPoint = "Polyline")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Polyline(nint hdc, Point* points, int count);

    // Cờ "đã gắn" của bộ vẽ tay: window property (GetWindowSubclass chỉ xuất bởi comctl32 v6; testhost không manifest nạp v5).

    [LibraryImport("user32.dll", EntryPoint = "SetPropW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProp(nint hwnd, string name, nint value);

    [LibraryImport("user32.dll", EntryPoint = "GetPropW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetProp(nint hwnd, string name);

    [LibraryImport("user32.dll", EntryPoint = "RemovePropW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint RemoveProp(nint hwnd, string name);

    // ---- hằng ----

    public const uint SrcCopy = 0x00CC0020;
    public const int PaintStructSize = 72;
    public const int TrackMouseEventSize = 24;
    public const uint TmeLeave = 0x00000002;

    public const uint WmPaint = 0x000F;
    public const uint WmEraseBkgnd = 0x0014;
    public const uint WmNcPaint = 0x0085;
    public const uint WmPrint = 0x0317;
    public const uint WmPrintClient = 0x0318;
    public const uint WmMouseMove = 0x0200;
    public const uint WmMouseLeave = 0x02A3;
    public const uint WmSetFocus = 0x0007;
    public const uint WmKillFocus = 0x0008;
    public const uint WmQueryUiState = 0x0129;
    public const uint WmUpdateUiState = 0x0128;
    public const uint WmNcDestroy = 0x0082;
    public const uint PrfNonClient = 0x00000002;
    public const uint UisfHideFocus = 0x1;
    public const uint UisfHideAccel = 0x2;

    // Button
    public const uint BmGetState = 0x00F2;
    public const uint BstPushed = 0x0004;
    public const uint BstFocus = 0x0008;
    public const uint BsTypeMask = 0x000F;
    public const uint BsPushLike = 0x00001000;
    public const uint BsMultiline = 0x00002000;
    public const uint BsLeftText = 0x00000020;
    public const uint BsIcon = 0x00000040;
    public const uint BsBitmap = 0x00000080;

    // Combobox
    public const uint CbsTypeMask = 0x0003;
    public const uint CbsOwnerDrawFixed = 0x0010;
    public const uint CbsOwnerDrawVariable = 0x0020;

    // Progress / Trackbar
    public const uint PbmGetRange = 0x0407;
    public const uint PbsMarquee = 0x08;
    public const uint TbsVert = 0x0002;
    public const uint TbmGetNumTics = 0x0410;
    public const uint TbmGetPTics = 0x040E;
    public const uint TbmGetThumbRect = 0x0419;
    public const uint TbmGetChannelRect = 0x041A;
    public const uint TbmGetRangeMin = 0x0401;
    public const uint TbmGetRangeMax = 0x0402;

    // Static
    public const uint SsTypeMask = 0x0000001F;
    public const uint SsEtchedHorz = 0x10;
    public const uint SsEtchedVert = 0x11;
    public const uint SsEtchedFrame = 0x12;

    // Header
    public const uint HdmGetItemCount = 0x1200;
    public const uint HdmGetItemRect = 0x1207;

    // Listview
    public const uint LvmGetBkColor = 0x1000;
    public const uint CdisFocus = 0x0010;
    public const uint CdisHot = 0x0040;

    // Window styles / metrics
    public const uint WsBorder = 0x00800000;
    public const uint WsDlgFrame = 0x00400000;
    public const uint WsExClientEdge = 0x00000200;
    public const uint WsExStaticEdge = 0x00020000;
    public const int SmCxBorder = 5;
    public const int SmCxEdge = 45;

    // DrawText
    public const uint DtHidePrefix = 0x00100000;
    public const uint DtRight = 0x0002;
}
