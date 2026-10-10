using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-13a: P/Invoke user32.dll (LibraryImport, không DllImport). Tên hằng WP-01 giữ nguyên làm alias để không đổi Shell.Win32.
internal static unsafe partial class User32
{
    public const uint WmCreate = WindowMessages.WmCreate;
    public const uint WmDestroy = WindowMessages.WmDestroy;
    public const uint WmClose = WindowMessages.WmClose;
    public const uint WsOverlappedWindow = WindowMessages.WsOverlappedWindow;
    public const int CwUseDefault = unchecked((int)0x80000000);
    public const int SwShowNoActivate = WindowMessages.SwShowNoActivate;
    public const int SwShowDefault = WindowMessages.SwShowDefault;
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

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(Msg* message, nint hwnd, uint filterMin, uint filterMax, uint removeFlags);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(Msg* message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(Msg* message);

    [LibraryImport("user32.dll", EntryPoint = "MsgWaitForMultipleObjectsEx", SetLastError = true)]
    public static partial uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    public static partial nint LoadCursor(nint instance, nint cursorName);

    [LibraryImport("user32.dll", EntryPoint = "SetCursor")]
    public static partial nint SetCursor(nint cursor);

    [LibraryImport("user32.dll", EntryPoint = "SetCapture")]
    public static partial nint SetCapture(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseCapture")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForSystem")]
    public static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetricsForDpi")]
    public static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll", EntryPoint = "AdjustWindowRectExForDpi", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustWindowRectExForDpi(Rect* rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle, uint dpi);

    [LibraryImport("user32.dll", EntryPoint = "GetThreadDpiAwarenessContext")]
    public static partial nint GetThreadDpiAwarenessContext();

    [LibraryImport("user32.dll", EntryPoint = "GetAwarenessFromDpiAwarenessContext")]
    public static partial int GetAwarenessFromDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    public static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "MonitorFromRect")]
    public static partial nint MonitorFromRect(Rect* rect, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    public static partial nint MonitorFromPoint(Point point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitor, MonitorInfo* info);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoEx(nint monitor, MonitorInfoEx* info);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplayMonitors", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(nint hdc, Rect* clip, delegate* unmanaged<nint, nint, Rect*, nint, int> callback, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowPlacement", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowPlacement(nint hwnd, WindowPlacement* placement);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPlacement", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPlacement(nint hwnd, WindowPlacement* placement);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, Rect* rect);

    [LibraryImport("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, Rect* rect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "GetCurrentInputMessageSource")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCurrentInputMessageSource(InputMessageSource* source);

    [LibraryImport("user32.dll", EntryPoint = "OpenClipboard", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint hwndOwner);

    [LibraryImport("user32.dll", EntryPoint = "EmptyClipboard", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", EntryPoint = "SetClipboardData", SetLastError = true)]
    public static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("user32.dll", EntryPoint = "GetClipboardData", SetLastError = true)]
    public static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll", EntryPoint = "CloseClipboard", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll", EntryPoint = "CreatePopupMenu", SetLastError = true)]
    public static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "DestroyMenu", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(nint menu, uint flags, nuint itemId, string? newItem);

    [LibraryImport("user32.dll", EntryPoint = "TrackPopupMenuEx", SetLastError = true)]
    public static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpmParams);
}
