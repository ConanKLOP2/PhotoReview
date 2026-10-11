using PhotoReview.App.Input;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Win32.Input;

/// <summary>
/// WP-22: WM_*BUTTONDOWN/DBLCLK, WM_MOUSEMOVE, WM_LBUTTONUP, WM_CAPTURECHANGED -> <see cref="ShellInput"/>. Toạ độ client là
/// số nguyên có dấu ở LOWORD/HIWORD của lParam (âm khi ảnh đang giữ capture và con trỏ ra ngoài cửa sổ); số lần nhấn do
/// <see cref="ClickCounter"/> đếm (lớp cửa sổ không có CS_DBLCLKS). Nút phải và nút phụ chỉ làm dừng đà (cửa sổ-tunnel của WPF);
/// menu ngữ cảnh là việc của WP-24. Nút giữa trong vùng nhìn bị nuốt (chạy MiddleClickAction).
/// </summary>
internal sealed class PointerMessageHandler(ShellInput input) : IWindowMessageHandler
{
    private const int XButton1 = 1;

    public bool TryHandle(in WindowMessage message, out nint result)
    {
        result = 0;
        var x = InputNative.SignedLowWord(message.LParam);
        var y = InputNative.SignedHighWord(message.LParam);
        switch (message.Msg)
        {
            case WindowMessages.WmLButtonDown:
            case WindowMessages.WmLButtonDblClk:
                return input.HandlePointerDown(PointerButton.Left, x, y);
            case WindowMessages.WmMButtonDown:
            case WindowMessages.WmMButtonDblClk:
                return input.HandlePointerDown(PointerButton.Middle, x, y);
            case WindowMessages.WmRButtonDown:
            case WindowMessages.WmRButtonDblClk:
                return input.HandlePointerDown(PointerButton.Right, x, y);
            case WindowMessages.WmXButtonDown:
            case WindowMessages.WmXButtonDblClk:
                var which = InputNative.SignedHighWord(message.WParam);
                input.HandlePointerDown(which == XButton1 ? PointerButton.XButton1 : PointerButton.XButton2, x, y);
                return false;
            case WindowMessages.WmMouseMove:
                input.HandlePointerMove(x, y, ((int)message.WParam & InputNative.MkLButton) != 0);
                return false;
            case WindowMessages.WmLButtonUp:
                input.HandleLeftButtonUp(x, y);
                return false;
            case WindowMessages.WmCaptureChanged:
                input.HandleCaptureLost();
                return false;
            default:
                return false;
        }
    }
}