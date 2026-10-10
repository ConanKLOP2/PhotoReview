using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PhotoReview.Platform.Windows;

/// <summary>
/// DWMWA_CLOAK: the window stays shown (activation, taskbar button, layout and rendering all proceed) but the
/// compositor does not draw it. Used at startup so the window is never seen before its first rendered frame
/// (an unpainted window surface shows white). Takes a raw handle (no WPF dependency) and never throws: an
/// unsupported attribute or a missing dwmapi returns false and the window simply stays visible.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsDwmCloak
{
    private const int DwmwaCloak = 13;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Cloaks (<paramref name="cloaked"/> true) or uncloaks the window; returns whether DWM accepted it.</summary>
    public static bool TrySetCloaked(IntPtr handle, bool cloaked)
    {
        if (handle == IntPtr.Zero) return false;
        var value = cloaked ? 1 : 0;
        try
        {
            return DwmSetWindowAttribute(handle, DwmwaCloak, ref value, sizeof(int)) == 0; // S_OK
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
}
