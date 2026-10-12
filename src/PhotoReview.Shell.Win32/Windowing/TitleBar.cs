using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Windowing;

/// <summary>Seam của <see cref="TitleBar"/> trên <c>DwmSetWindowAttribute</c> (test ghi lại lời gọi, không cần DWM).</summary>
internal interface IDwmWindowAttributes
{
    /// <summary>True khi DWM chấp nhận (S_OK); false trên Windows không hỗ trợ thuộc tính (không phải lỗi).</summary>
    bool Set(nint hwnd, uint attribute, uint value);
}

internal sealed unsafe class DwmWindowAttributes : IDwmWindowAttributes
{
    public static readonly DwmWindowAttributes Instance = new();

    public bool Set(nint hwnd, uint attribute, uint value) => Dwmapi.DwmSetWindowAttribute(hwnd, attribute, &value, sizeof(uint)) == 0;
}

/// <summary>
/// Thanh tiêu đề tối (<c>WindowsDarkTitleBar</c> của bản WPF, WP-25): <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> (20; 19 ở các bản
/// Windows 10 cũ) và màu caption <c>Dark.Window</c> #171717 (<c>DWMWA_CAPTION_COLOR</c>, chỉ Windows 11). Không bao giờ ném:
/// Windows không hỗ trợ thì DWM trả HRESULT lỗi và việc này thành no-op.
/// </summary>
internal static class TitleBar
{
    /// <summary><c>Dark.Window</c> (#171717) dạng COLORREF (0x00BBGGRR).</summary>
    internal const uint CaptionColorBgr = 0x00171717;

    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE ở các bản Windows 10 insider trước build 18985.</summary>
    internal const uint DwmwaUseImmersiveDarkModeLegacy = 19;

    /// <returns>True khi HĐH nhận thuộc tính chế độ tối (thông tin; không phải điều kiện lỗi).</returns>
    public static bool Apply(nint hwnd, IDwmWindowAttributes? dwm = null)
    {
        if (hwnd == 0)
        {
            return false;
        }

        dwm ??= DwmWindowAttributes.Instance;
        try
        {
            var dark = dwm.Set(hwnd, Dwmapi.DwmwaUseImmersiveDarkMode, 1) || dwm.Set(hwnd, DwmwaUseImmersiveDarkModeLegacy, 1);
            dwm.Set(hwnd, Dwmapi.DwmwaCaptionColor, CaptionColorBgr);
            return dark;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
