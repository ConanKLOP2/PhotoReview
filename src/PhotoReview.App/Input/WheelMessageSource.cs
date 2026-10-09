using System.Runtime.InteropServices;
using System.Windows;

namespace PhotoReview.App.Input;

/// <summary>
/// Q-TOUCHPAD-REFRESH: the Win32 side of wheel input that WPF does not expose -- the device the current input message came from
/// (<c>GetCurrentInputMessageSource</c>) and the horizontal wheel message (WM_MOUSEHWHEEL, sent by a sideways touchpad swipe or a
/// tilted wheel; WPF has no MouseHWheel event). Only valid while the message is being dispatched (WPF raises its wheel events
/// synchronously inside that dispatch).
/// </summary>
internal static class WheelMessageSource
{
    /// <summary>WM_MOUSEHWHEEL.</summary>
    public const int WmMouseHWheel = 0x020E;

    private const int ImdtTouchpad = 0x00000010; // INPUT_MESSAGE_DEVICE_TYPE.IMDT_TOUCHPAD

    [StructLayout(LayoutKind.Sequential)]
    private struct InputMessageSource
    {
        public int DeviceType;
        public int OriginId;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCurrentInputMessageSource(out InputMessageSource source);

    /// <summary>The device hint of the message being dispatched; <see cref="WheelDeviceHint.Unknown"/> when Windows does not say.</summary>
    public static WheelDeviceHint Current()
    {
        try
        {
            return GetCurrentInputMessageSource(out var source) && source.DeviceType == ImdtTouchpad
                ? WheelDeviceHint.Touchpad
                : WheelDeviceHint.Unknown;
        }
        catch (EntryPointNotFoundException)
        {
            return WheelDeviceHint.Unknown;
        }
    }

    /// <summary>The signed wheel delta in the high word of a wheel message's wParam.</summary>
    public static int Delta(IntPtr wParam) => unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));

    /// <summary>The cursor position (physical screen pixels) packed into a wheel message's lParam.</summary>
    public static Point ScreenPoint(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        return new Point(unchecked((short)(value & 0xFFFF)), unchecked((short)((value >> 16) & 0xFFFF)));
    }
}
