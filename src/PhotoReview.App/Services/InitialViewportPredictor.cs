using System.Runtime.InteropServices;
using PhotoReview.App.Windowing;

namespace PhotoReview.App.Services;

/// <summary>
/// P-1 startup: predicts, before the main window exists, the client area it will be shown with (the saved placement,
/// restored before the show by the window placement adapter, <c>WindowPlacementService.RestoreBeforeShow</c>), so the launch file's decode can
/// start right after config.json is read instead of when the window is shown (~450 ms later). The prediction only picks
/// the decode box: when it is wrong the presenter's own request (keyed by the real viewport) decodes again, as before.
/// </summary>
internal static class InitialViewportPredictor
{
    /// <summary>A rectangle in physical pixels (Win32 RECT layout).</summary>
    internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>
    /// Client size (pixels) of a standard-frame window: a maximized one fills <paramref name="workArea"/> with its side
    /// and bottom borders pushed off it, so only the caption is lost; a normal one is its bounds minus the frame.
    /// </summary>
    /// <param name="frameInsets">AdjustWindowRectEx of an empty rect: negative left/top, positive right/bottom.</param>
    /// <returns>The client size, or null when it would be empty.</returns>
    internal static (int Width, int Height)? PredictClientSize(bool maximized, PixelRect normalBounds, PixelRect workArea, PixelRect frameInsets)
    {
        // Maximized: Windows places the window rect at the work area inflated by the sizing border on every side, so
        // the client keeps the work area's width and loses only the caption (top inset minus that border).
        var caption = -frameInsets.Top - frameInsets.Right;
        var (width, height) = maximized
            ? (workArea.Width, workArea.Height - caption)
            : (normalBounds.Width - frameInsets.Width, normalBounds.Height - frameInsets.Height);
        return width > 0 && height > 0 ? (width, height) : null;
    }

    /// <summary>
    /// The decode box the window's first layout will compute (see MainWindow.UpdateTargetDecodeBox) for the saved
    /// <paramref name="placement"/> (null: first start, the XAML default size on the primary monitor).
    /// </summary>
    /// <returns>The predicted box, or null when no prediction is possible.</returns>
    public static DecodeBox? PredictDecodeBox(WindowPlacementData? placement, double defaultWidthDip, double defaultHeightDip, double qualityMultiplier)
    {
        try
        {
            var maximized = placement is not null && WindowPlacementRules.PlanStateBeforeShow(placement.ShowCommand) == WindowShowState.Maximized;
            var normal = placement is null ? (PixelRect?)null : new PixelRect(placement.NormalLeft, placement.NormalTop, placement.NormalRight, placement.NormalBottom);
            var onScreen = normal is { } n && WindowPlacementVisibility.IsVisible(n.Left, n.Top, n.Right, n.Bottom, VisibleAreas());
            // Same rule as RestoreBeforeShow: an off-screen Normal placement is ignored (default size), an off-screen
            // Maximized one still maximizes, on the monitor Windows picks for a new window (the primary one).
            var monitor = onScreen
                ? MonitorFromRect(ToRect(normal!.Value), MonitorDefaultToNearest)
                : MonitorFromPoint(default, MonitorDefaultToPrimary);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
            var dpi = DpiFor(monitor);
            var insetsRect = new Rect();
            if (!AdjustWindowRectExForDpi(ref insetsRect, WsOverlappedWindow, false, 0, dpi)) return null;
            var insets = ToPixelRect(insetsRect);
            var scale = dpi / 96.0;
            var bounds = onScreen && !maximized
                ? normal!.Value
                : new PixelRect(0, 0, (int)Math.Round(defaultWidthDip * scale), (int)Math.Round(defaultHeightDip * scale));
            if (PredictClientSize(maximized, bounds, ToPixelRect(info.Work), insets) is not { } client) return null;
            return AdaptivePreviewPolicy.CalculateTargetDecodeBox(client.Width / scale, client.Height / scale, scale, qualityMultiplier);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>DPI the window is laid out with: its monitor's (per-monitor aware), the system's (system aware) or 96.</summary>
    private static uint DpiFor(IntPtr monitor)
    {
        var awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
        if (awareness == DpiAwarenessPerMonitor && GetDpiForMonitor(monitor, MdtEffectiveDpi, out var x, out _) == 0) return x;
        return awareness == DpiAwarenessUnaware ? 96u : GetDpiForSystem();
    }

    private static List<System.Drawing.Rectangle> VisibleAreas() =>
        [.. Win32MonitorLayout.Instance.WorkAreas().Select(w => System.Drawing.Rectangle.FromLTRB(w.Left, w.Top, w.Right, w.Bottom))];

    private static PixelRect ToPixelRect(Rect r) => new(r.Left, r.Top, r.Right, r.Bottom);
    private static Rect ToRect(PixelRect r) => new() { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };

    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;
    private const int DpiAwarenessUnaware = 0;
    private const int DpiAwarenessPerMonitor = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public int Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(in Rect rect, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustWindowRectExForDpi(ref Rect rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle, uint dpi);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
