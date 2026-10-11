using PhotoReview.App.Input;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Win32.Input;

/// <summary>
/// WP-22: WM_KEYDOWN/WM_SYSKEYDOWN/WM_KEYUP/WM_SYSKEYUP/WM_CHAR/WM_SYSCHAR -> <see cref="ShellInput.HandleKeyDown"/>.
/// Dịch: mã phím ảo + bit mở rộng -> <see cref="KeyId"/> qua <see cref="KeyIdMapping"/> (G-KEY, khớp <c>KeyInterop</c> của WPF),
/// Shift phải nhận ra bằng scan code (WM_KEYDOWN luôn mang VK_SHIFT), bit 30 của lParam = auto-repeat (<c>e.IsRepeat</c>).
/// Phím đã xử lý thì WM_KEYUP và WM_CHAR/WM_SYSCHAR đi kèm cũng bị nuốt (không bíp, không ký tự lạc vào DefWindowProc);
/// phím không xử lý đi tiếp tới DefWindowProc (Alt+F4 đóng cửa sổ, Alt+Space mở menu hệ thống).
/// </summary>
internal sealed class KeyboardMessageHandler(ShellInput input) : IWindowMessageHandler
{
    private bool _lastKeyHandled;
    private int _handledVirtualKey = -1;

    public bool TryHandle(in WindowMessage message, out nint result)
    {
        result = 0;
        switch (message.Msg)
        {
            case WindowMessages.WmKeyDown:
            case WindowMessages.WmSysKeyDown:
                var virtualKey = ResolveVirtualKey((int)message.WParam, message.LParam);
                var key = KeyIdMapping.FromVirtualKey(virtualKey, (message.LParam & InputNative.KeyExtendedBit) != 0);
                var isRepeat = (message.LParam & InputNative.KeyRepeatBit) != 0;
                _lastKeyHandled = input.HandleKeyDown(key, isSystemKey: message.Msg == WindowMessages.WmSysKeyDown, isRepeat,
                    input.Environment.MessageTime());
                _handledVirtualKey = _lastKeyHandled ? (int)message.WParam : -1;
                return _lastKeyHandled;
            case WindowMessages.WmKeyUp:
            case WindowMessages.WmSysKeyUp:
                if (_handledVirtualKey == (int)message.WParam)
                {
                    _handledVirtualKey = -1;
                    return true;
                }

                return false;
            case WindowMessages.WmChar:
            case WindowMessages.WmSysChar:
                return _lastKeyHandled;
            default:
                return false;
        }
    }

    /// <summary>VK_SHIFT chung cho hai phím Shift: scan code 0x36 là Shift phải (VK_RSHIFT).</summary>
    internal static int ResolveVirtualKey(int virtualKey, nint lParam)
    {
        if (virtualKey != InputNative.VkShift)
        {
            return virtualKey;
        }

        var scanCode = (int)(((ulong)lParam >> 16) & 0xFF);
        return scanCode == InputNative.ScanCodeRightShift ? InputNative.VkRShift : InputNative.VkLShift;
    }
}