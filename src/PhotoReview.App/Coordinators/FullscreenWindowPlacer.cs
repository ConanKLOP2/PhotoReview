using System.Windows;
using System.Windows.Interop;
using PhotoReview.App.Windowing;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// F11 fullscreen without ever changing the OS window state (WPF adapter, NO-WPF WP-08 / C-14). The Win32 work (style bits,
/// ONE SetWindowPos onto the whole monitor, exact restore on exit, layout-change fallback) lives in
/// <see cref="FullscreenController"/> (App.Shared, by HWND, no WPF); see its remarks for the rationale. This class only
/// keeps the WPF <see cref="Window.ResizeMode"/> / <see cref="Window.WindowStyle"/> / <see cref="Window.WindowState"/>
/// in sync, in the same order as before.
/// </summary>
/// <remarks>
/// WPF's WindowStyle/ResizeMode setters apply the new chrome on their own, which raises a WM_SIZE (and WPF's synchronous
/// resize render) at the OLD rect with the new chrome before the move: a second, intermediate frame (measured: the image
/// 24 px too large for one frame). So the chrome bits and the rect change in ONE SetWindowPos in the controller; the WPF
/// properties are set afterwards only to keep them in sync (the bits already match, so no size change follows).
/// </remarks>
internal sealed class FullscreenWindowPlacer
{
    private readonly FullscreenController _controller;

    public FullscreenWindowPlacer() : this(Win32MonitorLayout.Instance) { }

    internal FullscreenWindowPlacer(IMonitorLayout layout) => _controller = new FullscreenController(layout);

    /// <summary>The state the window had before fullscreen (a minimized window counts as Normal).</summary>
    public WindowState StateBefore => WindowPlacementService.ToWpf(_controller.StateBefore);

    /// <summary>Restore bounds (physical px) saved when fullscreen was entered; null while not fullscreen.</summary>
    public (int Left, int Top, int Right, int Bottom)? NormalBounds => _controller.NormalBounds;

    public void Enter(Window window)
    {
        var current = WindowPlacementService.FromWpf(window.WindowState);
        var hwnd = new WindowInteropHelper(window).Handle;
        // A minimized window is not on screen: restore it to Normal through WPF (no visible flip) before the OS-level move.
        if (hwnd != IntPtr.Zero && window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

        _controller.Enter(hwnd, current);

        window.ResizeMode = ResizeMode.NoResize;
        window.WindowStyle = WindowStyle.None;
        // Not shown yet (style first, then the state) or no monitor info (legacy path): the window itself becomes Maximized.
        if (_controller.EnterNeedsMaximizedState && (hwnd == IntPtr.Zero || window.WindowState != WindowState.Maximized))
            window.WindowState = WindowState.Maximized;
    }

    public void Exit(Window window)
    {
        _controller.Exit(new WindowInteropHelper(window).Handle);

        window.ResizeMode = ResizeMode.CanResize;
        window.WindowStyle = WindowStyle.SingleBorderWindow;
        // No OS window / no saved bounds / no monitor info: nothing was moved natively, so the state goes back through WPF.
        if (_controller.ExitNeedsStateRestore) window.WindowState = StateBefore;
    }

    /// <summary>Pure rect policy of the exit path (see <see cref="FullscreenController"/>).</summary>
    internal static ScreenRect ResolveNormalExitRect(ScreenRect saved, IReadOnlyList<ScreenRect> works) =>
        FullscreenController.ResolveNormalExitRect(saved, works);
}
