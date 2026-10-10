using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Win32.Hosting.PendingInterop;

// KHAI BÁO TẠM (WP-14, thẻ NO-WPF-EXEC-PLAN-WP mục WP-14): WP-13a đang viết PhotoReview.Shell.Interop/Win32/* song song.
// Khi WP-13a merge, xoá cả thư mục PendingInterop và đổi `using PhotoReview.Shell.Win32.Hosting.PendingInterop;` trong
// Hosting/* sang namespace interop của WP-13a (đối chiếu tên hàm/struct ở danh sách trong NOWPF-WP14-WINDOW-LOOP.md).
// Chỉ LibraryImport (L-AOT); không logic.

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PendingWndClassEx
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

/// <summary>MSG.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PendingMsg
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

/// <summary>RECT.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PendingRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public PendingRect(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public readonly int Width => Right - Left;

    public readonly int Height => Bottom - Top;
}

/// <summary>POINT.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PendingPoint
{
    public int X;
    public int Y;
}

/// <summary>MINMAXINFO.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PendingMinMaxInfo
{
    public PendingPoint Reserved;
    public PendingPoint MaxSize;
    public PendingPoint MaxPosition;
    public PendingPoint MinTrackSize;
    public PendingPoint MaxTrackSize;
}

/// <summary>CREATESTRUCTW (chỉ đọc CreateParams).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PendingCreateStruct
{
    public nint CreateParams;
    public nint Instance;
    public nint Menu;
    public nint Parent;
    public int Cy;
    public int Cx;
    public int Y;
    public int X;
    public int Style;
    public nint Name;
    public nint ClassName;
    public uint ExStyle;
}

internal static unsafe partial class PendingUser32
{
    public const uint WmCreate = 0x0001;
    public const uint WmDestroy = 0x0002;
    public const uint WmSize = 0x0005;
    public const uint WmActivate = 0x0006;
    public const uint WmPaint = 0x000F;
    public const uint WmClose = 0x0010;
    public const uint WmQuit = 0x0012;
    public const uint WmEraseBkgnd = 0x0014;
    public const uint WmSetCursor = 0x0020;
    public const uint WmGetMinMaxInfo = 0x0024;
    public const uint WmNcCreate = 0x0081;
    public const uint WmNcDestroy = 0x0082;
    public const uint WmDpiChanged = 0x02E0;
    public const uint WmApp = 0x8000;

    public const uint WsOverlappedWindow = 0x00CF0000;
    public const int CwUseDefault = unchecked((int)0x80000000);
    public const nint HwndMessage = -3;
    public const int GwlpUserData = -21;

    public const int SwHide = 0;
    public const int SwShowNormal = 1;
    public const int SwShowNoActivate = 4;

    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;

    public const int HtClient = 1;
    public const int WaInactive = 0;

    public const uint PmNoRemove = 0x0000;
    public const uint PmRemove = 0x0001;
    public const uint QsAllInput = 0x04FF;
    public const uint MwmoInputAvailable = 0x0004;
    public const uint WaitObject0 = 0x00000000;
    public const uint WaitTimeout = 0x00000102;
    public const uint WaitFailed = 0xFFFFFFFF;
    public const uint Infinite = 0xFFFFFFFF;

    public const int IdcArrow = 32512;
    public const int IdcWait = 32514;
    public const int IdcSizeAll = 32646;

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(PendingWndClassEx* windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(PendingMsg* message, nint hwnd, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(PendingMsg* message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(PendingMsg* message);

    [LibraryImport("user32.dll", EntryPoint = "MsgWaitForMultipleObjectsEx", SetLastError = true)]
    public static partial uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PostQuitMessage")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    public static partial nint LoadCursor(nint instance, nint cursorName);

    [LibraryImport("user32.dll", EntryPoint = "SetCursor")]
    public static partial nint SetCursor(nint cursor);

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(PendingPoint* point);

    [LibraryImport("user32.dll", EntryPoint = "WindowFromPoint")]
    public static partial nint WindowFromPoint(PendingPoint point);

    [LibraryImport("user32.dll", EntryPoint = "SetCapture")]
    public static partial nint SetCapture(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseCapture", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "GetCapture")]
    public static partial nint GetCapture();

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "AdjustWindowRectExForDpi", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustWindowRectExForDpi(PendingRect* rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle, uint dpi);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, PendingRect* rect);

    [LibraryImport("user32.dll", EntryPoint = "ScreenToClient")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hwnd, PendingPoint* point);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowText(nint hwnd, string text);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll", EntryPoint = "ValidateRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ValidateRect(nint hwnd, PendingRect* rect);
}

internal static unsafe partial class PendingKernel32
{
    public const uint CreateWaitableTimerHighResolution = 0x00000002;
    public const uint TimerAllAccess = 0x001F0003;

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true)]
    public static partial nint GetModuleHandle(nint moduleName);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
    public static partial nint CreateWaitableTimerEx(nint attributes, nint name, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll", EntryPoint = "SetWaitableTimer", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWaitableTimer(nint timer, long* dueTime, int period, nint completionRoutine, nint argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", EntryPoint = "CancelWaitableTimer", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelWaitableTimer(nint timer);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);
}
