using System.Windows;
using System.Windows.Interop;
using PhotoReview.Platform.Windows;

namespace PhotoReview.App.Services;

/// <summary>
/// Applies the native dark title bar (DWMWA_USE_IMMERSIVE_DARK_MODE, plus DWMWA_CAPTION_COLOR on
/// Windows 11) to a WPF window, matching Themes/DarkPalette.xaml's near-black chrome. Every app
/// window (MainWindow, Settings, Benchmark, Diagnostics, Recovery, Action profiles, Batch review)
/// calls <see cref="Apply"/> once, in its constructor.
/// </summary>
internal static class DarkTitleBarChrome
{
    // Themes/DarkPalette.xaml Dark.Window (#171717), as a COLORREF (0x00BBGGRR).
    private const uint CaptionColorBgr = 0x00171717;

    /// <summary>
    /// Hooks <see cref="Window.SourceInitialized"/> (the earliest point the window has a native
    /// HWND) to enable the dark title bar. Safe to call unconditionally: on Windows versions that
    /// do not support the attribute, the underlying DWM call fails and is ignored (no exception, no
    /// visible effect).
    /// </summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            WindowsDarkTitleBar.TryEnableDarkMode(handle);
            WindowsDarkTitleBar.TrySetCaptionColor(handle, CaptionColorBgr);
        };
    }
}
