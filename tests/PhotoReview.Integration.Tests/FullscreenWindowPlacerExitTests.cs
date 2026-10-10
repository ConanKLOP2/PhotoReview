using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Windowing;
using PhotoReview.Integration.Tests.Infrastructure;
using Xunit;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Review 2026-10-08 LOW: <see cref="FullscreenWindowPlacer.Exit"/> against a real WPF window with an injected monitor
/// layout. Unchanged layout keeps the exact-restore path; a vanished monitor moves a Normal window onto a remaining one;
/// the Maximized exit path never consults the work areas. What a real unplugged monitor does can only be seen on hardware.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class FullscreenWindowPlacerExitTests
{
    [StructLayout(LayoutKind.Sequential)] private struct Rc { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rc r);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);

    /// <summary>A layout where the window's monitor is gone and only <paramref name="remaining"/> exist.</summary>
    private sealed class VanishedMonitorLayout(params ScreenRect[] remaining) : IMonitorLayout
    {
        public int WorkAreasCalls { get; private set; }
        public IntPtr MonitorOf(IntPtr hwnd) => new(0x7777);
        public bool TryGetWork(IntPtr monitor, out ScreenRect work) { work = default; return false; }
        public IReadOnlyList<ScreenRect> WorkAreas() { WorkAreasCalls++; return remaining; }
    }

    private static Window NewWindow(WindowState state)
    {
        var w = new Window
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 120, Top = 90, Width = 480, Height = 320, ShowInTaskbar = false,
            WindowState = state,
        };
        w.Show();
        return w;
    }

    private static Rc Rect(Window w) { GetWindowRect(new WindowInteropHelper(w).Handle, out var r); return r; }

    private static ScreenRect Work()
    {
        var all = Win32MonitorLayout.Instance.WorkAreas();
        Assert.NotEmpty(all);
        return all[0];
    }

    [Fact]
    public async Task Exit_UnchangedMonitorLayout_RestoresTheExactNormalRectWithoutChangingState()
    {
        await StaTestHost.RunAsync(() =>
        {
            var w = NewWindow(WindowState.Normal);
            try
            {
                var placer = new FullscreenWindowPlacer();
                var start = Rect(w);
                placer.Enter(w);
                Assert.NotEqual(start.R - start.L, Rect(w).R - Rect(w).L); // really moved onto the monitor

                placer.Exit(w);

                var end = Rect(w);
                Assert.Equal((start.L, start.T, start.R, start.B), (end.L, end.T, end.R, end.B));
                Assert.Equal(WindowState.Normal, w.WindowState);
                Assert.Equal(WindowStyle.SingleBorderWindow, w.WindowStyle);
            }
            finally { w.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Exit_MonitorVanishedWhileFullscreen_MovesTheNormalWindowOntoARemainingMonitorKeepingItsSize()
    {
        await StaTestHost.RunAsync(() =>
        {
            var w = NewWindow(WindowState.Normal);
            try
            {
                var work = Work();
                // The only remaining monitor is far to the right of where the window was saved: the saved rect is off-screen.
                var remaining = new ScreenRect(work.Right + 4000, work.Top, work.Right + 4000 + work.Width, work.Bottom);
                var layout = new VanishedMonitorLayout(remaining);
                var placer = new FullscreenWindowPlacer(layout);
                var start = Rect(w);
                placer.Enter(w);

                placer.Exit(w);

                var end = Rect(w);
                Assert.Equal(1, layout.WorkAreasCalls);
                Assert.Equal(start.R - start.L, end.R - end.L);
                Assert.Equal(start.B - start.T, end.B - end.T);
                Assert.True(end.L >= remaining.Left && end.R <= remaining.Right && end.T >= remaining.Top && end.B <= remaining.Bottom,
                    $"window {end.L},{end.T},{end.R},{end.B} not inside {remaining}");
                Assert.Equal(WindowState.Normal, w.WindowState);
            }
            finally { w.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Exit_MonitorLayoutChangedButWindowStillVisible_KeepsTheExactRect()
    {
        await StaTestHost.RunAsync(() =>
        {
            var w = NewWindow(WindowState.Normal);
            try
            {
                var layout = new VanishedMonitorLayout(Work()); // monitor handle differs (layout "changed") but the rect is still on screen
                var placer = new FullscreenWindowPlacer(layout);
                var start = Rect(w);
                placer.Enter(w);

                placer.Exit(w);

                var end = Rect(w);
                Assert.Equal((start.L, start.T, start.R, start.B), (end.L, end.T, end.R, end.B));
            }
            finally { w.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Exit_MaximizedWindowAfterLayoutChange_TakesTheMaximizePathAndNeverReadsTheWorkAreas()
    {
        await StaTestHost.RunAsync(() =>
        {
            var w = NewWindow(WindowState.Maximized);
            try
            {
                var layout = new VanishedMonitorLayout(new ScreenRect(9000, 0, 10000, 700));
                var placer = new FullscreenWindowPlacer(layout);
                placer.Enter(w);

                placer.Exit(w);

                Assert.Equal(0, layout.WorkAreasCalls); // the SW_MAXIMIZE path, not the rect path
                Assert.True(IsZoomed(new WindowInteropHelper(w).Handle));
                Assert.Equal(WindowState.Maximized, w.WindowState);
            }
            finally { w.Close(); }
            return Task.CompletedTask;
        });
    }
}
