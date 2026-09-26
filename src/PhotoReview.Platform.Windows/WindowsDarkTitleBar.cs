using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PhotoReview.Platform.Windows;

/// <summary>
/// DWM (Desktop Window Manager) attributes that make a window's native title bar/border draw dark,
/// matching the app's dark theme. Takes a raw window handle (no WPF dependency, so this can live in
/// Platform.Windows -- see LayerDependencyTests Rule 8) and never throws: on any OS where the
/// attribute is unsupported (pre-Windows 10 20H1, or the Win11-only caption colour), DwmSetWindowAttribute
/// returns a failing HRESULT and this class silently no-ops.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsDarkTitleBar
{
    // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 from Windows 10 20H1 (build 18985) onward; the same value
    // was reserved as 19 in earlier Windows 10 insider builds, so both are tried.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    // DWMWA_CAPTION_COLOR: Windows 11 only.
    private const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

    /// <summary>
    /// Turns on the immersive dark title bar/border for <paramref name="handle"/>. Returns whether the
    /// OS accepted either attribute (informational only -- callers should not fail on false, since an
    /// older/unsupported Windows build is expected to return false here).
    /// </summary>
    public static bool TryEnableDarkMode(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return false;
        var enabled = 1;
        return TrySet(handle, DwmwaUseImmersiveDarkMode, ref enabled) ||
               TrySet(handle, DwmwaUseImmersiveDarkModeLegacy, ref enabled);
    }

    /// <summary>
    /// Sets the native caption background colour (Windows 11 only; a no-op elsewhere).
    /// </summary>
    /// <param name="colorBgr">0x00BBGGRR -- DWM caption colour is COLORREF (BGR), not RGB.</param>
    public static bool TrySetCaptionColor(IntPtr handle, uint colorBgr)
    {
        if (handle == IntPtr.Zero) return false;
        try
        {
            return DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref colorBgr, sizeof(uint)) == 0; // S_OK
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static bool TrySet(IntPtr handle, int attribute, ref int value)
    {
        try
        {
            return DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)) == 0; // S_OK
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
}
