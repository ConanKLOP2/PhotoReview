using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>TOOLINFOW (commctrl.h) - 72 byte trên x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OverlayToolInfo
{
    public uint CbSize;
    public uint Flags;
    public nint Hwnd;
    public nuint Id;
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
    public nint Instance;
    public nint Text;
    public nint Param;
    public nint Reserved;
}

/// <summary>MSG (winuser.h) - cho <c>TTM_RELAYEVENT</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OverlayMsg
{
    public nint Hwnd;
    public uint Message;
    public nint WParam;
    public nint LParam;
    public uint Time;
    public int X;
    public int Y;
    public uint Private;
}

/// <summary>TRACKMOUSEEVENT.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OverlayTrackMouseEvent
{
    public uint Size;
    public uint Flags;
    public nint Hwnd;
    public uint HoverTime;
}

/// <summary>
/// WP-23: user32 mà overlay cần và chưa có ở <c>Shell.Interop</c> (tooltip native, đổi toạ độ, theo dõi chuột rời). Giữ ở đây
/// (không sửa file của gói khác); chỉ <c>LibraryImport</c> (L-AOT), không logic.
/// </summary>
internal static unsafe partial class OverlayNative
{
    public const string TooltipClass = "tooltips_class32";

    public const uint TtsAlwaysTip = 0x01;
    public const uint TtsNoPrefix = 0x02;

    public const uint TtmActivate = 0x0401;
    public const uint TtmSetDelayTime = 0x0403;
    public const uint TtmRelayEvent = 0x0407;
    public const uint TtmGetToolCount = 0x040D;
    public const uint TtmSetMaxTipWidth = 0x0418;
    public const uint TtmPopup = 0x0422;
    public const uint TtmAddTool = 0x0432;
    public const uint TtmDelTool = 0x0433;
    public const uint TtmGetCurrentTool = 0x043B;
    public const uint TtmGetText = 0x0438;

    public const int TtdtAutoPop = 2;
    public const int TtdtInitial = 3;

    public const uint TmeLeave = 0x00000002;
    public const uint WmMouseLeave = 0x02A3;

    public const int VkShift = 0x10;
    public const int VkTab = 0x09;
    public const int VkReturn = 0x0D;
    public const int VkSpace = 0x20;

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hwnd, Point* point);

    [LibraryImport("user32.dll", EntryPoint = "GetKeyState")]
    public static partial short GetKeyState(int virtualKey);

    [LibraryImport("user32.dll", EntryPoint = "TrackMouseEvent", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TrackMouseEvent(OverlayTrackMouseEvent* track);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? moduleName);

    [LibraryImport("comctl32.dll", EntryPoint = "InitCommonControlsEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitCommonControlsEx(uint* controls);

    /// <summary>Vị trí gốc vùng client của <paramref name="hwnd"/> trên màn hình (pixel vật lý).</summary>
    public static (int X, int Y) ClientOriginOnScreen(nint hwnd)
    {
        var origin = new Point { X = 0, Y = 0 };
        return ClientToScreen(hwnd, &origin) ? (origin.X, origin.Y) : (0, 0);
    }

    /// <summary>Xin <c>WM_MOUSELEAVE</c> một lần cho <paramref name="hwnd"/>.</summary>
    public static void RequestMouseLeave(nint hwnd)
    {
        var track = new OverlayTrackMouseEvent
        {
            Size = (uint)sizeof(OverlayTrackMouseEvent),
            Flags = TmeLeave,
            Hwnd = hwnd,
        };
        TrackMouseEvent(&track);
    }

    public static bool IsKeyDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;
}
