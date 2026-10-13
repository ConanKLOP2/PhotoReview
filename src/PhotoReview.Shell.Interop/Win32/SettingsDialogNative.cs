using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-35 GĐ3a: phần user32 mà khung Settings Win32 cần (trang cuộn: cửa sổ chứa trang + thanh cuộn dọc riêng). CHỈ THÊM (file mới,
// cùng lớp partial). LibraryImport (L-AOT), chỉ ở Shell.Interop (L-PINV).

internal static unsafe partial class User32
{
    /// <summary>SetParent: đưa điều khiển con (do template tạo trong hộp thoại) vào cửa sổ chứa trang (viewport cuộn).</summary>
    [LibraryImport("user32.dll", EntryPoint = "SetParent")]
    public static partial nint SetParent(nint child, nint newParent);

    [LibraryImport("user32.dll", EntryPoint = "SetScrollInfo")]
    public static partial int SetScrollInfo(nint hwnd, int bar, ScrollInfo* info, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("user32.dll", EntryPoint = "GetScrollInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetScrollInfo(nint hwnd, int bar, ScrollInfo* info);

    /// <summary>GetKeyState: bit cao (số âm) = đang nhấn; dùng cho Ctrl+Tab đổi trang.</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetKeyState")]
    public static partial short GetKeyState(int virtualKey);
}

/// <summary>SCROLLINFO (28 byte).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ScrollInfo
{
    public uint Size;
    public uint Mask;
    public int Min;
    public int Max;
    public uint Page;
    public int Pos;
    public int TrackPos;
}

/// <summary>Hằng thanh cuộn (WinUser.h). Chỉ khai báo.</summary>
internal static class ScrollConstants
{
    public const int SbVert = 1;
    public const int SbCtl = 2;
    public const uint SifRange = 0x0001;
    public const uint SifPage = 0x0002;
    public const uint SifPos = 0x0004;
    public const uint SifDisableNoScroll = 0x0008;
    public const uint SifTrackPos = 0x0010;
    public const int SbLineUp = 0;
    public const int SbLineDown = 1;
    public const int SbPageUp = 2;
    public const int SbPageDown = 3;
    public const int SbThumbPosition = 4;
    public const int SbThumbTrack = 5;
    public const int SbTop = 6;
    public const int SbBottom = 7;
    public const int SbEndScroll = 8;
    public const uint SbsVert = 0x0001;
    public const uint WsClipChildren = 0x02000000;
    public const uint WsClipSiblings = 0x04000000;
}
