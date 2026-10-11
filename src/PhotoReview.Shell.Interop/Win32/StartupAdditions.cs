using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Interop;

// WP-20: phần user32 mà startup shell cần và WP-13a chưa khai báo. CHỈ THÊM (file mới, cùng lớp partial) - không đổi
// khai báo của WP-13a. LibraryImport (L-AOT); chỉ nằm ở Shell.Interop (L-PINV).
internal static unsafe partial class User32
{
    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 (windef.h: -4).</summary>
    public const nint DpiAwarenessContextPerMonitorAwareV2 = -4;

    [LibraryImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(nint value);

    [LibraryImport("user32.dll", EntryPoint = "AreDpiAwarenessContextsEqual")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AreDpiAwarenessContextsEqual(nint first, nint second);

    [LibraryImport("user32.dll", EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);
}
