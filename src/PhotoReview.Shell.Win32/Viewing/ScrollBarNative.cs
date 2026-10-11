using System.Runtime.InteropServices;

namespace PhotoReview.Shell.Win32.Viewing;

/// <summary>
/// WP-26: phần user32 mà thanh cuộn cần và WP-13a chưa khai báo (theo dõi chuột rời cửa sổ, nhịp lặp bàn phím của hệ thống).
/// Mọi giá trị đi qua <see cref="ScrollBarOptions"/> để test thay được.
/// </summary>
internal static partial class ScrollBarNative
{
    /// <summary>WM_MOUSELEAVE (do <c>TrackMouseEvent(TME_LEAVE)</c> sinh ra).</summary>
    internal const uint WmMouseLeave = 0x02A3;

    /// <summary>WM_CANCELMODE: hệ thống buộc kết thúc mọi vòng lặp chuột/capture (hộp thoại modal hiện ra...).</summary>
    internal const uint WmCancelMode = 0x001F;

    private const uint TmeLeave = 0x00000002;
    private const uint SpiGetKeyboardSpeed = 0x000A;
    private const uint SpiGetKeyboardDelay = 0x0016;

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public uint Size;
        public uint Flags;
        public nint Hwnd;
        public uint HoverTime;
    }

    [LibraryImport("user32.dll", EntryPoint = "TrackMouseEvent")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TrackMouseEvent(ref TrackMouseEventData data);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, ref int value, uint winIni);

    /// <summary>Xin <c>WM_MOUSELEAVE</c> một lần khi con trỏ rời vùng client của <paramref name="hwnd"/>.</summary>
    public static void TrackMouseLeave(nint hwnd)
    {
        var data = new TrackMouseEventData { Size = (uint)Marshal.SizeOf<TrackMouseEventData>(), Flags = TmeLeave, Hwnd = hwnd };
        _ = TrackMouseEvent(ref data);
    }

    /// <summary>
    /// Độ trễ trước lần lặp đầu và khoảng lặp (ms) của <c>RepeatButton</c> WPF: <c>(KeyboardDelay + 1) * 250</c> và
    /// <c>(31 - KeyboardSpeed) * (400 - 33) / 31 + 33</c> (<c>SystemParameters.KeyboardDelay/KeyboardSpeed</c>).
    /// </summary>
    public static (int DelayMs, int IntervalMs) RepeatTiming()
    {
        var delay = 0;
        var speed = 31;
        if (!SystemParametersInfo(SpiGetKeyboardDelay, 0, ref delay, 0) || delay is < 0 or > 3)
        {
            delay = 0;
        }

        if (!SystemParametersInfo(SpiGetKeyboardSpeed, 0, ref speed, 0) || speed is < 0 or > 31)
        {
            speed = 31;
        }

        return RepeatTimingFor(delay, speed);
    }

    /// <summary>Công thức của <c>RepeatButton</c> từ giá trị <c>SPI_GETKEYBOARDDELAY</c> (0..3) và <c>SPI_GETKEYBOARDSPEED</c> (0..31).</summary>
    internal static (int DelayMs, int IntervalMs) RepeatTimingFor(int keyboardDelay, int keyboardSpeed) =>
        ((keyboardDelay + 1) * 250, ((31 - keyboardSpeed) * (400 - 33) / 31) + 33);
}
