using PhotoReview.App.Input;

namespace PhotoReview.Shell.Win32.Input;

/// <summary>
/// WP-22: mọi thứ <see cref="ShellInput"/> đọc từ hệ thống ngoài chính thông điệp (trạng thái phím chỉnh, dấu thời gian của
/// thông điệp, thiết bị phát wheel, ngưỡng double-click). Mặc định = Win32 thật; test (replay G-INPUT bằng SendMessage trên
/// desktop ẩn, nơi <c>GetKeyState</c>/<c>GetMessageTime</c>/<c>GetCurrentInputMessageSource</c> không phản ánh thông điệp giả)
/// thay từng cái. Không nhận DPI: các hàm theo DPI nhận DPI nguyên của cửa sổ.
/// </summary>
internal sealed class ShellInputEnvironment
{
    /// <summary><c>Keyboard.Modifiers</c> (chỉ dùng cho phím; chuột dùng cờ MK_* trong thông điệp).</summary>
    public Func<KeyModifiers> Modifiers { get; init; } = InputNative.ReadModifiers;

    /// <summary><c>GetMessageTime</c>: dấu thời gian (ms) của thông điệp đang xử lý (<c>e.Timestamp</c> của WPF).</summary>
    public Func<int> MessageTime { get; init; } = InputNative.GetMessageTime;

    /// <summary><c>Environment.TickCount</c> (WM_MOUSEHWHEEL không có <c>e.Timestamp</c> trong bản WPF).</summary>
    public Func<int> TickCount { get; init; } = static () => Environment.TickCount;

    /// <summary><c>WheelMessageSource.Current()</c>: thiết bị của thông điệp đang xử lý (chỉ hợp lệ trong lúc phân phối).</summary>
    public Func<WheelDeviceHint> WheelDevice { get; init; } = WheelMessageSource.Current;

    /// <summary><c>GetDoubleClickTime</c> (ms).</summary>
    public Func<int> DoubleClickTimeMs { get; init; } = static () => (int)InputNative.GetDoubleClickTime();

    /// <summary><c>SM_CXDOUBLECLK/SM_CYDOUBLECLK</c> (pixel) theo DPI nguyên.</summary>
    public Func<uint, (int Width, int Height)> DoubleClickSizePixels { get; init; } = InputNative.DoubleClickSizePixels;
}
