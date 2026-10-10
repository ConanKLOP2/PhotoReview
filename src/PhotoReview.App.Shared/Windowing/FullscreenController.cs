using System.Runtime.InteropServices;

namespace PhotoReview.App.Windowing;

/// <summary>
/// F11 fullscreen without ever changing the OS window state (C-14, theo HWND; không WPF). The old sequence
/// Maximized -> Normal -> style -> Maximized made Windows restore and re-maximize the window: measured on a real monitor,
/// DWM then composes its restore/maximize animation (a scaled, zoomed crop of the stale content) or, with transitions
/// disabled, the intermediate 1200x800 Normal window itself. Here the window keeps its state (Maximized stays Maximized,
/// Normal stays Normal), only its style bits change and ONE SetWindowPos moves it straight onto the whole monitor
/// (rcMonitor, so it covers the taskbar, RV-A02). Exit restores the style and moves the window straight back (a Maximized
/// window via <c>SW_SHOWMAXIMIZED</c> on the already-maximized window, a Normal one to its exact saved rect).
/// All coordinates are physical pixels. UI thread only.
/// </summary>
/// <remarks>
/// Controller chỉ làm phần Win32. Khung UI (WPF: <c>ResizeMode</c>/<c>WindowStyle</c>/<c>WindowState</c>) do adapter
/// của framework tự đồng bộ theo <see cref="EnterNeedsMaximizedState"/> và <see cref="ExitNeedsStateRestore"/>.
/// </remarks>
public sealed partial class FullscreenController : IFullscreenController
{
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020, SwpNoMove = 0x0002, SwpNoSize = 0x0001;
    private const int SwMaximize = 3;
    private const int SwShowNormal = 1;
    private const int GwlStyle = -16;
    private const long WsChrome = 0x00C00000 /*CAPTION*/ | 0x00040000 /*THICKFRAME*/ | 0x00080000 /*SYSMENU*/ | 0x00020000 /*MINIMIZEBOX*/ | 0x00010000 /*MAXIMIZEBOX*/;
    private const uint MonitorDefaultToNearest = 2;

    private WindowShowState _stateBefore = WindowShowState.Normal;
    private Rect32 _normalBounds;
    private bool _hasNormalBounds;
    private Rect32 _windowRectBefore, _workBefore;
    private long _styleBefore;
    private nint _monitorBefore;
    private readonly IMonitorLayout _layout;

    // Normal-window exit: a saved rect is still usable when this much of its caption band lies inside some work area.
    private const int CaptionBand = 32, MinVisibleWidth = 120, MinVisibleHeight = 16;

    public FullscreenController() : this(Win32MonitorLayout.Instance)
    {
    }

    internal FullscreenController(IMonitorLayout layout) => _layout = layout;

    /// <summary>The state the window had before fullscreen (a minimized window counts as Normal).</summary>
    public WindowShowState StateBefore => _stateBefore;

    /// <summary>Restore bounds (physical px) saved when fullscreen was entered; null while not fullscreen.</summary>
    public (int Left, int Top, int Right, int Bottom)? NormalBounds =>
        _hasNormalBounds ? (_normalBounds.Left, _normalBounds.Top, _normalBounds.Right, _normalBounds.Bottom) : null;

    /// <summary>
    /// After <see cref="Enter"/>: true when the OS window was NOT moved onto the monitor (no HWND yet, or no monitor
    /// info), so the framework must set its own state to Maximized to get a fullscreen-sized window.
    /// </summary>
    public bool EnterNeedsMaximizedState { get; private set; }

    /// <summary>
    /// After <see cref="Exit"/>: true when the controller did not restore the OS window itself (no HWND, no saved bounds,
    /// or no monitor info), so the framework must set its own state back to <see cref="StateBefore"/>.
    /// </summary>
    public bool ExitNeedsStateRestore { get; private set; }

    /// <summary>
    /// <paramref name="current"/>: the window's state now (<see cref="WindowShowState.Minimized"/> is restored to Normal first
    /// when it has an HWND, as the WPF adapter already did). <paramref name="hwnd"/> zero: not shown yet, nothing to flip.
    /// </summary>
    public void Enter(nint hwnd, WindowShowState current)
    {
        _stateBefore = current == WindowShowState.Minimized ? WindowShowState.Normal : current;
        EnterNeedsMaximizedState = true;
        if (hwnd == 0)
        {
            // Not shown yet (no OS window to flip): the framework applies style first, then the state.
            _hasNormalBounds = false;
            return;
        }
        if (IsIconic(hwnd)) ShowWindow(hwnd, SwShowNormal); // not on screen: no visible flip (adapter normally restored it already)

        var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
        if (GetWindowPlacement(hwnd, ref placement)) { _normalBounds = placement.NormalPosition; _hasNormalBounds = true; }
        else _hasNormalBounds = false;
        GetWindowRect(hwnd, out _windowRectBefore);

        _monitorBefore = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (_monitorBefore != 0 && GetMonitorInfo(_monitorBefore, ref info))
        {
            _workBefore = info.Work;
            _styleBefore = GetWindowLongPtr(hwnd, GwlStyle);
            SetWindowLongPtr(hwnd, GwlStyle, (nint)(_styleBefore & ~WsChrome));
            var r = info.Monitor;
            SetWindowPos(hwnd, 0, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            EnterNeedsMaximizedState = false;
        }
        else
        {
            _monitorBefore = 0; // no monitor info: legacy path (framework maximizes)
        }
    }

    public void Exit(nint hwnd)
    {
        if (hwnd == 0 || !_hasNormalBounds)
        {
            ExitNeedsStateRestore = true;
            return;
        }
        if (_monitorBefore == 0)
        {
            ExitNeedsStateRestore = true;
        }
        else
        {
            ExitNeedsStateRestore = false;
            // Same monitor with the same work area: the exact rect the window had goes back in one step (a maximized
            // window's rect already includes its frame overhang). Otherwise (taskbar/monitor changed meanwhile) let
            // Windows compute the maximized rect.
            var sameWork = _layout.MonitorOf(hwnd) == _monitorBefore
                && _layout.TryGetWork(_monitorBefore, out var workNow) && workNow == ToScreen(_workBefore);
            SetWindowLongPtr(hwnd, GwlStyle, (nint)_styleBefore);
            if (sameWork || _stateBefore != WindowShowState.Maximized)
            {
                var b = _windowRectBefore;
                if (!sameWork)
                {
                    // Monitor layout changed while in fullscreen (dock/second monitor unplugged): the saved rect may now be
                    // off-screen. Same layout -> the exact rect above, untouched (measured 0 bad frames).
                    var fit = ResolveNormalExitRect(ToScreen(b), _layout.WorkAreas());
                    b = new Rect32 { Left = fit.Left, Top = fit.Top, Right = fit.Right, Bottom = fit.Bottom };
                }
                SetWindowPos(hwnd, 0, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            }
            else
            {
                SetWindowPos(hwnd, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged); // refresh the frame
                ShowWindow(hwnd, SwMaximize);
            }
        }
        _hasNormalBounds = false;
    }

    private static ScreenRect ToScreen(Rect32 r) => new(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>
    /// Where a Normal window's saved rect goes when the monitor layout changed. Kept as is when the caption band (top
    /// <see cref="CaptionBand"/> px) still shows at least <see cref="MinVisibleWidth"/> x <see cref="MinVisibleHeight"/> px
    /// inside some work area (partially visible but draggable). Otherwise moved onto the nearest work area, keeping its size
    /// (shrunk only if larger than that work area) and the closest position. No monitor info: unchanged.
    /// </summary>
    internal static ScreenRect ResolveNormalExitRect(ScreenRect saved, IReadOnlyList<ScreenRect> works)
    {
        if (works.Count == 0) return saved;
        var bandBottom = Math.Min(saved.Bottom, saved.Top + CaptionBand);
        foreach (var w in works)
        {
            var iw = Math.Min(saved.Right, w.Right) - Math.Max(saved.Left, w.Left);
            var ih = Math.Min(bandBottom, w.Bottom) - Math.Max(saved.Top, w.Top);
            if (iw >= MinVisibleWidth && ih >= MinVisibleHeight) return saved;
        }
        var cx = (saved.Left + saved.Right) / 2;
        var cy = (saved.Top + saved.Bottom) / 2;
        var best = works[0];
        var bestDist = long.MaxValue;
        foreach (var w in works)
        {
            long dx = Math.Max(Math.Max(w.Left - cx, cx - w.Right), 0), dy = Math.Max(Math.Max(w.Top - cy, cy - w.Bottom), 0);
            var d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = w; }
        }
        var width = Math.Min(saved.Width, best.Width);
        var height = Math.Min(saved.Height, best.Height);
        var left = Math.Clamp(saved.Left, best.Left, best.Right - width);
        var top = Math.Clamp(saved.Top, best.Top, best.Bottom - height);
        return new ScreenRect(left, top, left + width, top + height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 : IEquatable<Rect32>
    {
        public int Left, Top, Right, Bottom;

        public readonly bool Equals(Rect32 o) => Left == o.Left && Top == o.Top && Right == o.Right && Bottom == o.Bottom;

        public override readonly bool Equals(object? o) => o is Rect32 r && Equals(r);

        public override readonly int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public int Length, Flags, ShowCmd;
        public int MinX, MinY, MaxX, MaxY;
        public Rect32 NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect32 Monitor; public Rect32 Work; public int Flags; }

    [LibraryImport("user32.dll")] private static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowPlacement(nint hwnd, ref Placement placement);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(nint hwnd, out Rect32 rect);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static partial nint GetWindowLongPtr(nint hwnd, int index);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(nint hwnd, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsIconic(nint hwnd);
}
