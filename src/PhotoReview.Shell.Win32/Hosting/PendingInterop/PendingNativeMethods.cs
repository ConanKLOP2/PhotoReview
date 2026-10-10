using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Hosting.PendingInterop;

// KHAI BÁO TẠM (WP-14): những hàm/struct/hằng Hosting/* cần mà PhotoReview.Shell.Interop (WP-13a, #400) CHƯA có. WP-14 không được
// sửa file của WP-13a, nên giữ ở đây; lead (hoặc gói interop kế) chuyển chúng sang Shell.Interop/Win32/* rồi xoá thư mục này và
// `using PhotoReview.Shell.Win32.Hosting.PendingInterop;` trong Hosting/* + test. Danh sách: NOWPF-WP14-WINDOW-LOOP.md mục 3.
// Chỉ LibraryImport (L-AOT); không logic.

/// <summary>MINMAXINFO.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PendingMinMaxInfo
{
    public Point Reserved;
    public Point MaxSize;
    public Point MaxPosition;
    public Point MinTrackSize;
    public Point MaxTrackSize;
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
    public const nint HwndMessage = -3;
    public const int HtClient = 1;
    public const int WaInactive = 0;
    public const int IdcWait = 32514;
    public const int IdcSizeAll = 32646;

    [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(Point* point);

    /// <summary>POINT truyền theo giá trị = 8 byte (x thấp, y cao); Point của assembly khác không coi là blittable ở đây.</summary>
    [LibraryImport("user32.dll", EntryPoint = "WindowFromPoint")]
    public static partial nint WindowFromPoint(long packedPoint);

    [LibraryImport("user32.dll", EntryPoint = "GetCapture")]
    public static partial nint GetCapture();

    [LibraryImport("user32.dll", EntryPoint = "ScreenToClient")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hwnd, Point* point);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowText(nint hwnd, string text);

    [LibraryImport("user32.dll", EntryPoint = "ValidateRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ValidateRect(nint hwnd, Rect* rect);
}

internal static unsafe partial class PendingKernel32
{
    public const uint CreateWaitableTimerHighResolution = 0x00000002;
    public const uint TimerAllAccess = 0x001F0003;

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
