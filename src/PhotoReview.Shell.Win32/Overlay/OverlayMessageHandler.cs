using PhotoReview.App.Input;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>
/// WP-23: nối thông điệp chuột/phím của cửa sổ vào <see cref="ShellOverlay"/>. Phải đứng ĐẦU chuỗi handler: overlay nhận
/// trước ảnh (như các nút WPF nằm trên <c>ScrollViewer</c>). Chuột di chuyển/phím chỉ được QUAN SÁT (trả false: ảnh vẫn nhận);
/// nhấn/nhả chuột trái trên phần tử overlay, bánh xe trên panel và Tab/Space/Enter của toolbar bị NUỐT.
/// </summary>
internal sealed class OverlayMessageHandler(ShellOverlay overlay, IShellWindow window, IOverlayTooltips? tooltips) : IWindowMessageHandler
{
    private bool _trackingLeave;
    private bool _releasing;

    public bool TryHandle(in WindowMessage message, out nint result)
    {
        result = 0;
        switch (message.Msg)
        {
            case WindowMessages.WmMouseMove:
                if (!_trackingLeave)
                {
                    _trackingLeave = true;
                    OverlayNative.RequestMouseLeave(message.Hwnd);
                }

                overlay.PointerMoved(ClientPoint(message.LParam));
                tooltips?.RelayMouse(message.Msg, message.WParam, message.LParam);
                return false;

            case OverlayNative.WmMouseLeave:
                _trackingLeave = false;
                overlay.PointerLeft();
                return false;

            case WindowMessages.WmLButtonDown:
            case WindowMessages.WmLButtonDblClk:
                tooltips?.RelayMouse(message.Msg, message.WParam, message.LParam);
                if (overlay.PointerDown(ClientPoint(message.LParam)))
                {
                    if (overlay.IsPointerDown)
                    {
                        window.CapturePointer();
                    }

                    return true;
                }

                return false;

            case WindowMessages.WmLButtonUp:
                if (!overlay.IsPointerDown)
                {
                    return false;
                }

                tooltips?.RelayMouse(message.Msg, message.WParam, message.LParam);
                if (window.HasPointerCapture)
                {
                    // ReleaseCapture gửi WM_CAPTURECHANGED ngay: không coi đó là bị cướp capture (mất cú nhấn trước khi click).
                    _releasing = true;
                    try
                    {
                        window.ReleasePointer();
                    }
                    finally
                    {
                        _releasing = false;
                    }
                }

                return overlay.PointerUp(ClientPoint(message.LParam));

            case WindowMessages.WmCaptureChanged:
                if (!_releasing && overlay.IsPointerDown && message.LParam != message.Hwnd)
                {
                    overlay.PointerCancel();
                }

                return false;

            case WindowMessages.WmMouseWheel:
            case WindowMessages.WmMouseHWheel:
                // lParam của bánh xe là toạ độ MÀN HÌNH.
                return overlay.Wheel(window.ScreenToClientDip(new PointD(LowWord(message.LParam), HighWord(message.LParam))));

            case WindowMessages.WmKeyDown:
            case WindowMessages.WmSysKeyDown:
                {
                    bool repeat = ((message.LParam >> 30) & 1) != 0;
                    bool shift = OverlayNative.IsKeyDown(OverlayNative.VkShift);
                    return overlay.KeyDown((int)(message.WParam & 0xFFFF), shift, repeat);
                }

            default:
                return false;
        }
    }

    private PointD ClientPoint(nint lParam)
    {
        double dpi = window.DpiScale <= 0 ? 1 : window.DpiScale;
        return new PointD(LowWord(lParam) / dpi, HighWord(lParam) / dpi);
    }

    private static int LowWord(nint value) => (short)(value & 0xFFFF);

    private static int HighWord(nint value) => (short)((value >> 16) & 0xFFFF);
}
