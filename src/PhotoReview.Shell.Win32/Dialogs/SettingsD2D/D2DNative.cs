using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsD2D;

/// <summary>
/// SPIKE: các lời gọi Win32 mà bề mặt tự vẽ cần và <c>Shell.Interop</c> chưa có. Giữ ở đây (file mới) để spike KHÔNG sửa Interop; nếu
/// đổi sang D2D thật thì chuyển sang Interop theo quy ước ABI WP-13b.
/// </summary>
internal static unsafe partial class D2DNative
{
    [LibraryImport("user32.dll", EntryPoint = "GetKeyState")]
    public static partial short GetKeyState(int virtualKey);

    [LibraryImport("user32.dll", EntryPoint = "SetTimer")]
    public static partial nuint SetTimer(nint hwnd, nuint id, uint elapse, nint proc);

    [LibraryImport("user32.dll", EntryPoint = "KillTimer")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(nint hwnd, nuint id);

    [LibraryImport("user32.dll", EntryPoint = "GetCaretBlinkTime")]
    public static partial uint GetCaretBlinkTime();

    [LibraryImport("user32.dll", EntryPoint = "ValidateRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ValidateRect(nint hwnd, Rect* rect);

    [LibraryImport("user32.dll", EntryPoint = "UpdateWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "RedrawWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RedrawWindow(nint hwnd, Rect* update, nint region, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "EnableWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnableWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowRgn")]
    public static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hwnd, Point* point);

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(Point* point);

    [LibraryImport("user32.dll", EntryPoint = "GetParent")]
    public static partial nint GetParent(nint hwnd);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateRectRgn")]
    public static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
        uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [LibraryImport("imm32.dll", EntryPoint = "ImmGetContext")]
    public static partial nint ImmGetContext(nint hwnd);

    [LibraryImport("imm32.dll", EntryPoint = "ImmReleaseContext")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ImmReleaseContext(nint hwnd, nint context);

    [LibraryImport("imm32.dll", EntryPoint = "ImmSetCompositionWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ImmSetCompositionWindow(nint context, CompositionForm* form);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalSize")]
    public static partial nuint GlobalSize(nint memory);

    public const uint RdwInvalidate = 0x0001;
    public const uint RdwErase = 0x0004;
    public const uint RdwUpdateNow = 0x0100;
    public const uint RdwAllChildren = 0x0080;

    [StructLayout(LayoutKind.Sequential)]
    public struct CompositionForm
    {
        public uint Style;
        public Point CurrentPos;
        public Rect Area;
    }
}
