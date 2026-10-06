using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// F11 fullscreen without ever changing the OS window state. The old sequence Maximized -> Normal -> style -> Maximized
/// made Windows restore and re-maximize the window: measured on a real monitor, DWM then composes its restore/maximize
/// animation (a scaled, zoomed crop of the stale content) or, with transitions disabled, the intermediate 1200x800
/// Normal window itself. Here the window keeps its state (Maximized stays Maximized, Normal stays Normal), only its
/// style bits change and ONE SetWindowPos moves it straight onto the whole monitor (rcMonitor, so it covers the
/// taskbar, RV-A02). Exit restores the style and moves the window straight back (a Maximized window via
/// <c>SW_SHOWMAXIMIZED</c> on the already-maximized window, a Normal one to its exact saved rect).
/// All coordinates are physical pixels. UI thread only.
/// </summary>
internal sealed class FullscreenWindowPlacer
{
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;
    private const int SwMaximize = 3;
    private const int GwlStyle = -16;
    private const long WsChrome = 0x00C00000 /*CAPTION*/ | 0x00040000 /*THICKFRAME*/ | 0x00080000 /*SYSMENU*/ | 0x00020000 /*MINIMIZEBOX*/ | 0x00010000 /*MAXIMIZEBOX*/;
    private const uint MonitorDefaultToNearest = 2;

    private WindowState _stateBefore = WindowState.Normal;
    private Rect32 _normalBounds;
    private bool _hasNormalBounds;
    private Rect32 _windowRectBefore, _workBefore;
    private long _styleBefore;
    private IntPtr _monitorBefore;

    /// <summary>The state the window had before fullscreen (a minimized window counts as Normal).</summary>
    public WindowState StateBefore => _stateBefore;

    /// <summary>Restore bounds (physical px) saved when fullscreen was entered; null while not fullscreen.</summary>
    public (int Left, int Top, int Right, int Bottom)? NormalBounds =>
        _hasNormalBounds ? (_normalBounds.Left, _normalBounds.Top, _normalBounds.Right, _normalBounds.Bottom) : null;

    // WPF's WindowStyle/ResizeMode setters apply the new chrome on its own, which raises a WM_SIZE (and WPF's synchronous
    // resize render) at the OLD rect with the new chrome before the move: a second, intermediate frame (measured: the image
    // 24 px too large for one frame). So the chrome bits and the rect change in ONE SetWindowPos here; the WPF properties are
    // set afterwards only to keep them in sync (the bits already match, so no size change follows).
    public void Enter(Window window)
    {
        _stateBefore = window.WindowState == WindowState.Minimized ? WindowState.Normal : window.WindowState;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            // Not shown yet (no OS window to flip): style first, then the state.
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowStyle = WindowStyle.None;
            window.WindowState = WindowState.Maximized;
            _hasNormalBounds = false;
            return;
        }
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; // not on screen: no visible flip

        var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
        if (GetWindowPlacement(hwnd, ref placement)) { _normalBounds = placement.NormalPosition; _hasNormalBounds = true; }
        else _hasNormalBounds = false;
        GetWindowRect(hwnd, out _windowRectBefore);

        _monitorBefore = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (_monitorBefore != IntPtr.Zero && GetMonitorInfo(_monitorBefore, ref info))
        {
            _workBefore = info.Work;
            _styleBefore = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(_styleBefore & ~WsChrome));
            var r = info.Monitor;
            SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowStyle = WindowStyle.None;
        }
        else
        {
            _monitorBefore = IntPtr.Zero;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowStyle = WindowStyle.None;
            if (window.WindowState != WindowState.Maximized) window.WindowState = WindowState.Maximized; // no monitor info: legacy path
        }
    }

    public void Exit(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !_hasNormalBounds)
        {
            window.ResizeMode = ResizeMode.CanResize;
            window.WindowStyle = WindowStyle.SingleBorderWindow;
            window.WindowState = _stateBefore;
            return;
        }
        if (_monitorBefore == IntPtr.Zero)
        {
            window.ResizeMode = ResizeMode.CanResize;
            window.WindowStyle = WindowStyle.SingleBorderWindow;
            window.WindowState = _stateBefore;
        }
        else
        {
            // Same monitor with the same work area: the exact rect the window had goes back in one step (a maximized
            // window's rect already includes its frame overhang). Otherwise (taskbar/monitor changed meanwhile) let
            // Windows compute the maximized rect.
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var sameWork = MonitorFromWindow(hwnd, MonitorDefaultToNearest) == _monitorBefore
                && GetMonitorInfo(_monitorBefore, ref info) && info.Work.Equals(_workBefore);
            SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(_styleBefore));
            if (sameWork || _stateBefore != WindowState.Maximized)
            {
                var b = _windowRectBefore;
                SetWindowPos(hwnd, IntPtr.Zero, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            }
            else
            {
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0002 | 0x0001 | SwpNoZOrder | SwpNoActivate | SwpFrameChanged); // NOMOVE|NOSIZE: refresh the frame
                ShowWindow(hwnd, SwMaximize);
            }
            window.ResizeMode = ResizeMode.CanResize;
            window.WindowStyle = WindowStyle.SingleBorderWindow;
        }
        _hasNormalBounds = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 : IEquatable<Rect32> { public int Left, Top, Right, Bottom; public readonly bool Equals(Rect32 o) => Left == o.Left && Top == o.Top && Right == o.Right && Bottom == o.Bottom; public override readonly bool Equals(object? o) => o is Rect32 r && Equals(r); public override readonly int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom); }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public int Length, Flags, ShowCmd;
        public int MinX, MinY, MaxX, MaxY;
        public Rect32 NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect32 Monitor; public Rect32 Work; public int Flags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowPlacement(IntPtr hwnd, ref Placement placement);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
}
