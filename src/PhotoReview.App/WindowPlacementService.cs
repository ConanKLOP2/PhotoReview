using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PhotoReview.App.Windowing;

namespace PhotoReview.App;

/// <summary>
/// Persists the Win32 window placement so monitor, restored bounds and maximized
/// state survive application restarts without DIP/pixel conversion errors.
/// WPF adapter (NO-WPF WP-08, C-14): the file format, prefetch, visibility rule and show-command rules live in
/// <see cref="JsonWindowPlacementStore"/> / <see cref="WindowPlacementRules"/> (App.Shared, no WPF); this class only
/// talks to the WPF <see cref="Window"/> and keeps the Get/SetWindowPlacement marshaling.
/// </summary>
internal static class WindowPlacementService
{
    private const int ShowHide = 0;

    private static readonly JsonWindowPlacementStore Store = new();

    /// <param name="placementPath">IAppPaths.WindowPlacementFile (R7-11: never a hard-coded %LOCALAPPDATA% path).</param>
    public static void Restore(Window window, string placementPath)
    {
        try
        {
            var placement = Read(placementPath);
            if (placement is null || !IsVisible(placement.NormalPosition)) return;

            placement.Length = Marshal.SizeOf<WindowPlacement>();
            placement.ShowCommand = NormalizeShowCommand(placement.ShowCommand);
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) SetWindowPlacement(handle, placement);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not restore window placement", ex);
        }
    }

    /// <summary>
    /// Startup (perf/startup-first-image): applies the saved placement while the window has an HWND but is NOT shown
    /// yet (call from SourceInitialized). The normal bounds are set with SW_HIDE, so nothing becomes visible here, and
    /// the saved state goes into <see cref="Window.WindowState"/>, so WPF's own Show() shows the window directly where
    /// and how it was closed. Before, the window was first shown at its XAML size (1200x800) and only moved/maximized in
    /// Loaded: a visible half-size window plus a second full layout pass on the startup critical path.
    /// Same validation as <see cref="Restore"/> (missing/damaged file: nothing is applied), except that off-screen bounds
    /// of a window saved Maximized still restore the Maximized state.
    /// </summary>
    /// <returns>True when a placement was applied.</returns>
    public static bool RestoreBeforeShow(Window window, string placementPath)
    {
        try
        {
            var data = Store.TakePrefetched(placementPath) ?? JsonWindowPlacementStore.ReadRaw(placementPath);
            if (data is null) return false;
            var placement = FromData(data);
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;

            var state = PlanStateBeforeShow(placement.ShowCommand);
            if (!IsVisible(placement.NormalPosition))
            {
                // The monitor it was closed on is gone (e.g. an undocked laptop): its bounds are unusable, but a window
                // closed maximized still reopens maximized (on the monitor Windows picks for a new window) instead of
                // as a 1200x800 window; a Normal one keeps the default size, as before.
                if (state != WindowState.Maximized) return false;
                window.WindowState = WindowState.Maximized;
                return true;
            }
            placement.Length = Marshal.SizeOf<WindowPlacement>();
            placement.ShowCommand = ShowHide;
            if (!SetWindowPlacement(handle, placement)) return false;
            window.WindowState = state;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not restore window placement before show", ex);
            return false;
        }
    }

    /// <summary>The saved placement, or null when the file is missing or empty (a damaged file throws).</summary>
    internal static WindowPlacement? Read(string placementPath) =>
        JsonWindowPlacementStore.ReadRaw(placementPath) is { } data ? FromData(data) : null;

    /// <summary>
    /// P-1 startup: reads the placement file on the thread pool now (the first JSON read costs ~25 ms of reflection
    /// metadata, measured on the UI thread inside Show()). <see cref="RestoreBeforeShow"/> takes the result once; the
    /// launch decode uses it to predict the viewport (<see cref="Services.InitialViewportPredictor"/>).
    /// </summary>
    internal static Task<WindowPlacementData?> Prefetch(string placementPath) => Store.Prefetch(placementPath);

    /// <summary>The WPF state a saved show command reopens in (same rule as <see cref="NormalizeShowCommand"/>).</summary>
    internal static WindowState PlanStateBeforeShow(int savedShowCommand) =>
        ToWpf(WindowPlacementRules.PlanStateBeforeShow(savedShowCommand));

    /// <param name="placementPath">IAppPaths.WindowPlacementFile.</param>
    /// <param name="fullscreenRestoreState">
    /// R7-10: the state to reopen in when closing from fullscreen (fullscreen itself is a borderless Maximized), or null.
    /// </param>
    /// <param name="fullscreenNormalBounds">
    /// The restore rect (physical px) saved when fullscreen was entered. Fullscreen moves a Normal window onto the whole
    /// monitor without changing its state, so GetWindowPlacement would report that monitor rect as the restore bounds.
    /// </param>
    public static void Save(Window window, string placementPath, WindowState? fullscreenRestoreState = null,
        (int Left, int Top, int Right, int Bottom)? fullscreenNormalBounds = null)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            if (!GetWindowPlacement(handle, placement)) { AppLog.Error("Could not read window placement"); return; }

            // Never reopen minimized. Closing from the taskbar should restore normally.
            placement.ShowCommand = ResolveShowCommand(placement.ShowCommand, fullscreenRestoreState);
            if (fullscreenRestoreState is not null && fullscreenNormalBounds is { } b)
                placement.NormalPosition = new Rectangle { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
            Store.Save(placementPath, ToData(placement));
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save window placement", ex);
        }
    }

    /// <summary>
    /// Writes via a uniquely named temp file so several windows saving the same placement file (per-folder mode)
    /// never collide on a shared "*.tmp" name (IOException / one window overwriting another's half-written file).
    /// </summary>
    internal static void WriteAtomically(string path, string content, Action<string, string>? writeText = null) =>
        JsonWindowPlacementStore.WriteAtomically(path, content, writeText);

    /// <summary>R2-F-26: only "normal" and "maximized" are meaningful restore states (see <see cref="WindowPlacementRules"/>).</summary>
    internal static int NormalizeShowCommand(int showCommand) => WindowPlacementRules.NormalizeShowCommand(showCommand);

    /// <summary>R7-10: closing in fullscreen reads back as Maximized; save the state the window had before fullscreen instead.</summary>
    internal static int ResolveShowCommand(int showCommand, WindowState? fullscreenRestoreState) =>
        WindowPlacementRules.ResolveShowCommand(showCommand, fullscreenRestoreState is { } s ? FromWpf(s) : null);

    internal static bool IsVisible(Rectangle bounds) =>
        WindowPlacementVisibility.IsVisible(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, GetMonitorWorkAreas());

    /// <summary>Work area (excluding the taskbar) of every attached monitor, in physical pixels.</summary>
    private static List<System.Drawing.Rectangle> GetMonitorWorkAreas() =>
        [.. Win32MonitorLayout.Instance.WorkAreas().Select(w => System.Drawing.Rectangle.FromLTRB(w.Left, w.Top, w.Right, w.Bottom))];

    internal static WindowState ToWpf(WindowShowState state) => state switch
    {
        WindowShowState.Maximized => WindowState.Maximized,
        WindowShowState.Minimized => WindowState.Minimized,
        _ => WindowState.Normal,
    };

    internal static WindowShowState FromWpf(WindowState state) => state switch
    {
        WindowState.Maximized => WindowShowState.Maximized,
        WindowState.Minimized => WindowShowState.Minimized,
        _ => WindowShowState.Normal,
    };

    internal static WindowPlacementData ToData(WindowPlacement p) => new(
        p.Length, p.Flags, p.ShowCommand, p.MinPosition.X, p.MinPosition.Y, p.MaxPosition.X, p.MaxPosition.Y,
        p.NormalPosition.Left, p.NormalPosition.Top, p.NormalPosition.Right, p.NormalPosition.Bottom);

    internal static WindowPlacement FromData(WindowPlacementData d) => new()
    {
        Length = d.Length,
        Flags = d.Flags,
        ShowCommand = d.ShowCommand,
        MinPosition = new Point { X = d.MinX, Y = d.MinY },
        MaxPosition = new Point { X = d.MaxX, Y = d.MaxY },
        NormalPosition = new Rectangle { Left = d.NormalLeft, Top = d.NormalTop, Right = d.NormalRight, Bottom = d.NormalBottom },
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr window, [In, Out] WindowPlacement placement);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr window, [In] WindowPlacement placement);

    /// <summary>Win32 WINDOWPLACEMENT marshaling shape (also the legacy file schema; tests deserialize it directly).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal sealed class WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand = 3; // SW_SHOWMAXIMIZED
        public Point MinPosition;
        public Point MaxPosition;
        public Rectangle NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
