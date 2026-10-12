using PhotoReview.App.Input;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Input;

namespace PhotoReview.Shell.Win32.Menus;

/// <summary>
/// WP-24: WM_CONTEXTMENU -> <see cref="ContextMenuController"/>. <c>DefWindowProc</c> phát WM_CONTEXTMENU khi nhả nút phải
/// (lParam = toạ độ MÀN HÌNH, pixel, có dấu) và khi bấm phím Menu / Shift+F10 (lParam = -1; WPF mở tại giữa phần tử). Chỉ nhận
/// điểm trong vùng client: nhấn chuột phải trên thanh tiêu đề/viền (hệ thống tự gửi WM_CONTEXTMENU kèm toạ độ non-client) để
/// <c>DefWindowProc</c> hiện menu hệ thống. Toạ độ đổi sang DIP theo DPI cửa sổ; host menu nhân lại theo DPI khi hiện.
/// </summary>
internal sealed class ContextMenuMessageHandler(IShellWindow window, ContextMenuController controller) : IWindowMessageHandler
{
    public bool TryHandle(in WindowMessage message, out nint result)
    {
        result = 0;
        if (message.Msg != WindowMessages.WmContextMenu)
        {
            return false;
        }

        var x = InputNative.SignedLowWord(message.LParam);
        var y = InputNative.SignedHighWord(message.LParam);
        PointD screenPixel;
        if (x == -1 && y == -1)
        {
            screenPixel = ClientCenterScreenPixel();
        }
        else
        {
            screenPixel = new PointD(x, y);
            var client = window.ScreenToClientDip(screenPixel);
            var size = window.ClientSizeDip;
            if (client.X < 0 || client.Y < 0 || client.X >= size.Width || client.Y >= size.Height)
            {
                return false; // vùng non-client: menu hệ thống của cửa sổ
            }
        }

        var scale = window.DpiScale > 0 ? window.DpiScale : 1.0;
        _ = controller.ShowAsync(new PointD(screenPixel.X / scale, screenPixel.Y / scale));
        return true;
    }

    /// <summary>Giữa vùng client theo pixel màn hình (gốc client suy từ <see cref="IShellWindow.ScreenToClientDip"/>).</summary>
    private PointD ClientCenterScreenPixel()
    {
        var scale = window.DpiScale > 0 ? window.DpiScale : 1.0;
        var size = window.ClientSizeDip;
        var origin = window.ScreenToClientDip(new PointD(0, 0)); // client DIP của điểm màn hình (0,0) = -gốc/scale
        var originX = -origin.X * scale;
        var originY = -origin.Y * scale;
        return new PointD(originX + (size.Width * scale / 2), originY + (size.Height * scale / 2));
    }
}
