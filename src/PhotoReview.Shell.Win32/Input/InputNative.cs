using System.Runtime.InteropServices;
using PhotoReview.App.Input;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;

namespace PhotoReview.Shell.Win32.Input;

/// <summary>
/// WP-22: phần user32 mà WP-13a chưa khai báo (trạng thái phím, thời gian thông điệp, thời gian double-click) và các đọc hệ thống
/// của nhập liệu. Chỉ dùng trong <c>Shell.Win32/Input</c>; mọi giá trị đi qua <see cref="ShellInputEnvironment"/> để test thay được.
/// </summary>
internal static unsafe partial class InputNative
{
    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkMenu = 0x12;
    public const int VkLShift = 0xA0;
    public const int VkRShift = 0xA1;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;

    /// <summary>Cờ phím trong LOWORD(wParam) của WM_MOUSEWHEEL / WM_MOUSEHWHEEL / WM_MOUSEMOVE / WM_*BUTTON*.</summary>
    public const int MkLButton = 0x0001;

    public const int MkControl = 0x0008;

    /// <summary>Bit 30 của lParam WM_KEYDOWN: phím đã đang giữ (auto-repeat).</summary>
    public const long KeyRepeatBit = 0x40000000;

    /// <summary>Bit 24 của lParam: phím mở rộng (Ctrl/Alt phải, phím điều hướng cụm riêng).</summary>
    public const long KeyExtendedBit = 0x01000000;

    /// <summary>Scan code của Shift phải (<c>MapVirtualKey(SC, MAPVK_VSC_TO_VK_EX)</c> trả VK_RSHIFT).</summary>
    public const int ScanCodeRightShift = 0x36;

    [LibraryImport("user32.dll", EntryPoint = "GetKeyState")]
    public static partial short GetKeyState(int virtualKey);

    [LibraryImport("user32.dll", EntryPoint = "GetDoubleClickTime")]
    public static partial uint GetDoubleClickTime();

    [LibraryImport("user32.dll", EntryPoint = "GetMessageTime")]
    public static partial int GetMessageTime();

    /// <summary><c>Keyboard.Modifiers</c> của WPF: trạng thái phím chỉnh tại thời điểm xử lý thông điệp.</summary>
    public static KeyModifiers ReadModifiers()
    {
        var modifiers = KeyModifiers.None;
        if (GetKeyState(VkControl) < 0)
        {
            modifiers |= KeyModifiers.Control;
        }

        if (GetKeyState(VkMenu) < 0)
        {
            modifiers |= KeyModifiers.Alt;
        }

        if (GetKeyState(VkShift) < 0)
        {
            modifiers |= KeyModifiers.Shift;
        }

        if (GetKeyState(VkLWin) < 0 || GetKeyState(VkRWin) < 0)
        {
            modifiers |= KeyModifiers.Windows;
        }

        return modifiers;
    }

    /// <summary>Con trỏ theo pixel màn hình (GetCursorPos); null khi không đọc được.</summary>
    public static PointD? CursorScreenPixel()
    {
        Point point;
        return PendingUser32.GetCursorPos(&point) ? new PointD(point.X, point.Y) : null;
    }

    /// <summary>SM_CXDRAG/SM_CYDRAG (pixel) theo DPI của cửa sổ: ngưỡng kéo của <c>SystemParameters.MinimumHorizontalDragDistance</c>.</summary>
    public static (int Horizontal, int Vertical) DragMetricsPixels(uint dpi) =>
        (User32.GetSystemMetricsForDpi(WindowMessages.SmCxDrag, dpi), User32.GetSystemMetricsForDpi(WindowMessages.SmCyDrag, dpi));

    /// <summary>SM_CXDOUBLECLK/SM_CYDOUBLECLK (pixel) theo DPI: hình chữ nhật mà hai lần nhấn phải nằm trong.</summary>
    public static (int Width, int Height) DoubleClickSizePixels(uint dpi) =>
        (User32.GetSystemMetricsForDpi(WindowMessages.SmCxDoubleClk, dpi), User32.GetSystemMetricsForDpi(WindowMessages.SmCyDoubleClk, dpi));

    /// <summary>Số nguyên có dấu ở LOWORD / HIWORD của lParam chuột (toạ độ có thể âm khi đang capture).</summary>
    public static int SignedLowWord(nint value) => unchecked((short)((ulong)value & 0xFFFF));

    public static int SignedHighWord(nint value) => unchecked((short)(((ulong)value >> 16) & 0xFFFF));

    public static int LowWord(nint value) => (int)((ulong)value & 0xFFFF);

    /// <summary>Delta có dấu ở HIWORD(wParam) của thông điệp wheel.</summary>
    public static int WheelDelta(nint wParam) => SignedHighWord(wParam);

    /// <summary>Trích khoảng DPI nguyên của cửa sổ từ hệ số (96 x scale).</summary>
    public static uint DpiOf(double dpiScale) => (uint)Math.Max(1, (int)Math.Round(dpiScale * 96.0));
}
