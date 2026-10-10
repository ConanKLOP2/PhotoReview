using System.Runtime.InteropServices;

namespace PhotoReview.App.Windowing;

/// <summary>A screen rectangle in physical pixels (right/bottom exclusive, like a Win32 RECT).</summary>
internal readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>Seam over the OS monitor queries <see cref="FullscreenController"/> needs on exit, so tests can inject a layout.</summary>
internal interface IMonitorLayout
{
    /// <summary>The monitor nearest to the window (<c>MonitorFromWindow(MONITOR_DEFAULTTONEAREST)</c>); zero when unknown.</summary>
    nint MonitorOf(nint hwnd);

    /// <summary>The work area (monitor minus taskbar) of <paramref name="monitor"/>; false when it no longer exists.</summary>
    bool TryGetWork(nint monitor, out ScreenRect work);

    /// <summary>The work areas of every monitor currently attached.</summary>
    IReadOnlyList<ScreenRect> WorkAreas();
}

internal sealed unsafe partial class Win32MonitorLayout : IMonitorLayout
{
    public static readonly Win32MonitorLayout Instance = new();

    public nint MonitorOf(nint hwnd) => MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/);

    public bool TryGetWork(nint monitor, out ScreenRect work) => TryGetWorkArea(monitor, out work);

    public IReadOnlyList<ScreenRect> WorkAreas()
    {
        var list = new List<ScreenRect>();
        var handle = GCHandle.Alloc(list);
        try
        {
            EnumDisplayMonitors(0, 0, &CollectWorkArea, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return list;
    }

    private static bool TryGetWorkArea(nint monitor, out ScreenRect work)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != 0 && GetMonitorInfo(monitor, ref info))
        {
            work = new ScreenRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
            return true;
        }
        work = default;
        return false;
    }

    [UnmanagedCallersOnly]
    private static int CollectWorkArea(nint monitor, nint dc, Rc* rect, nint data)
    {
        try
        {
            if (GCHandle.FromIntPtr(data).Target is List<ScreenRect> list && TryGetWorkArea(monitor, out var work)) list.Add(work);
            return 1;
        }
        catch (InvalidCastException)
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rc { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rc Monitor; public Rc Work; public int Flags; }

    [LibraryImport("user32.dll")] private static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EnumDisplayMonitors(nint dc, nint clip, delegate* unmanaged<nint, nint, Rc*, nint, int> proc, nint data);
}
