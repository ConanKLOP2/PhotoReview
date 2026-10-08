using System.Runtime.InteropServices;

namespace PhotoReview.App.Coordinators;

/// <summary>A screen rectangle in physical pixels (right/bottom exclusive, like a Win32 RECT).</summary>
internal readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>Seam over the OS monitor queries <see cref="FullscreenWindowPlacer"/> needs on exit, so tests can inject a layout.</summary>
internal interface IMonitorLayout
{
    /// <summary>The monitor nearest to the window (<c>MonitorFromWindow(MONITOR_DEFAULTTONEAREST)</c>); zero when unknown.</summary>
    IntPtr MonitorOf(IntPtr hwnd);

    /// <summary>The work area (monitor minus taskbar) of <paramref name="monitor"/>; false when it no longer exists.</summary>
    bool TryGetWork(IntPtr monitor, out ScreenRect work);

    /// <summary>The work areas of every monitor currently attached.</summary>
    IReadOnlyList<ScreenRect> WorkAreas();
}

internal sealed class Win32MonitorLayout : IMonitorLayout
{
    public static readonly Win32MonitorLayout Instance = new();

    public IntPtr MonitorOf(IntPtr hwnd) => MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/);

    public bool TryGetWork(IntPtr monitor, out ScreenRect work)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            work = new ScreenRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
            return true;
        }
        work = default;
        return false;
    }

    public IReadOnlyList<ScreenRect> WorkAreas()
    {
        var list = new List<ScreenRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref Rc r, IntPtr data) =>
        {
            if (TryGetWork(m, out var work)) list.Add(work);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rc { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rc Monitor; public Rc Work; public int Flags; }
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rc rect, IntPtr data);

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
}
