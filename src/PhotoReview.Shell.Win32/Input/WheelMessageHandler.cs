using PhotoReview.App.Input;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Win32.Input;

/// <summary>
/// WP-22: WM_MOUSEWHEEL (<c>ImageScroll_PreviewMouseWheel</c>) và WM_MOUSEHWHEEL (hook của WPF cho vuốt ngang touchpad).
/// lParam là toạ độ MÀN HÌNH (pixel) nên đổi sang client DIP qua cửa sổ; delta ở HIWORD(wParam) (có dấu), Ctrl ở cờ MK_CONTROL
/// của LOWORD(wParam). Thiết bị (touchpad chính xác) đọc từ <see cref="ShellInputEnvironment.WheelDevice"/> NGAY trong lượt
/// phân phối này (<c>GetCurrentInputMessageSource</c> chỉ đúng lúc đó). Handler không phụ thuộc DefWindowProc: thông điệp wheel
/// được xử lý (kết quả 0) hoặc đi tiếp (overlay, so sánh, ngoài vùng nhìn với wheel ngang).
/// </summary>
internal sealed class WheelMessageHandler(ShellInput input) : IWindowMessageHandler
{
    public bool TryHandle(in WindowMessage message, out nint result)
    {
        result = 0;
        if (message.Msg is not (WindowMessages.WmMouseWheel or WindowMessages.WmMouseHWheel))
        {
            return false;
        }

        var screen = new PointD(InputNative.SignedLowWord(message.LParam), InputNative.SignedHighWord(message.LParam));
        var client = input.ScreenToClientDip(screen);
        var delta = InputNative.WheelDelta(message.WParam);
        var ctrl = ((int)message.WParam & InputNative.MkControl) != 0;
        return message.Msg == WindowMessages.WmMouseWheel
            ? input.HandleVerticalWheel(delta, ctrl, client)
            : input.HandleHorizontalWheel(delta, ctrl, client);
    }
}