using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-01: chỉ những gì cửa sổ khởi động tối thiểu (Shell.Win32/Startup/BootstrapWindow.cs) cần. WP-13a thêm phần còn lại.

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WndClassEx
{
    public uint Size;
    public uint Style;
    public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
    public int ClassExtra;
    public int WindowExtra;
    public nint Instance;
    public nint Icon;
    public nint Cursor;
    public nint Background;
    public char* MenuName;
    public char* ClassName;
    public nint IconSmall;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    public nint Hwnd;
    public uint Message;
    public nint WParam;
    public nint LParam;
    public uint Time;
    public int PointX;
    public int PointY;
    public uint Private;
}

internal static unsafe partial class User32
{
    public const uint WmCreate = 0x0001;
    public const uint WmDestroy = 0x0002;
    public const uint WmClose = 0x0010;

    public const uint WsOverlappedWindow = 0x00CF0000;
    public const int CwUseDefault = unchecked((int)0x80000000);

    public const int SwShowNoActivate = 4;
    public const int SwShowDefault = 10;

    public const int IdcArrow = 32512;

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(WndClassEx* windowClass);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterClass(string className, nint instance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PostQuitMessage")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    public static partial int GetMessage(Msg* message, nint hwnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(Msg* message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(Msg* message);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    public static partial nint LoadCursor(nint instance, nint cursorName);
}

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true)]
    public static partial nint GetModuleHandle(nint moduleName);
}

internal static partial class Gdi32
{
    public const int BlackBrush = 4;

    [LibraryImport("gdi32.dll", EntryPoint = "GetStockObject")]
    public static partial nint GetStockObject(int index);
}
