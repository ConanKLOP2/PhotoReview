using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PhotoReview.App;
using PhotoReview.App.Services;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// perf/startup-first-image, observed on the real MainWindow from the production graph: the window is shown directly in
/// its saved placement (no 1200x800 window that then jumps), cloaked until its first frame (no white surface), and the
/// launch file's decode is already running when Show() returns.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class StartupFirstShowIntegrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private const int WmWindowPosChanged = 0x0047;
    private const uint SwpShowWindow = 0x0040;

    [Fact(DisplayName = "A window saved Maximized is already maximized and cloaked when it is first shown, and is uncloaked after its first frame")]
    public async Task FirstShow_IsMaximizedAndCloaked_ThenRevealedAfterFirstFrame()
    {
        using var dataRoot = new DataRootFixture();
        var file = dataRoot.Root.Combine("placement.json");
        var work = PrimaryWorkArea();
        WritePlacement(file, 3, new WindowPlacementService.Rectangle { Left = work.Left + 60, Top = work.Top + 60, Right = work.Left + 560, Bottom = work.Top + 460 });
        var calls = new List<bool>();
        var previous = StartupWindowReveal.SetCloaked;
        StartupWindowReveal.SetCloaked = (handle, cloaked) => { calls.Add(cloaked); previous(handle, cloaked); return true; };
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                var window = TestAppHost.CreateMainWindow(null, placementFile: file);
                bool? zoomedAtShow = null, cloakedAtShow = null;
                var rendered = false;
                window.ShowInTaskbar = false;
                window.ShowActivated = false;
                window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!.AddHook(
                    (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                    {
                        if (msg == WmWindowPosChanged && zoomedAtShow is null && ShowsWindow(lParam))
                        {
                            zoomedAtShow = IsZoomed(hwnd);
                            cloakedAtShow = window.IsStartupCloaked;
                        }
                        return IntPtr.Zero;
                    });
                window.ContentRendered += (_, _) => rendered = true;
                try
                {
                    window.Show();
                    Assert.True(await StaTestHost.WaitForAsync(() => rendered, Deadline), "The window never rendered");
                    Assert.True(zoomedAtShow, "The window was first shown in another state, then maximized (the visible jump)");
                    Assert.True(cloakedAtShow, "The window was visible before its first frame (white surface)");
                    Assert.False(window.IsStartupCloaked);
                    Assert.Equal([true, false], calls);
                    Assert.Equal(WindowState.Maximized, window.WindowState);
                }
                finally { window.Close(); }
            });
        }
        finally { StartupWindowReveal.SetCloaked = previous; }
    }

    [Fact(DisplayName = "The window is revealed by its render ticks, not only by ContentRendered (posted at Input priority, which a busy startup dispatcher delays)")]
    public async Task Reveal_HappensOnRenderTicks_WhileContentRenderedIsStillQueued()
    {
        using var dataRoot = new DataRootFixture();
        var previous = StartupWindowReveal.SetCloaked;
        StartupWindowReveal.SetCloaked = (handle, cloaked) => { previous(handle, cloaked); return true; };
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                var window = TestAppHost.CreateMainWindow(null);
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.ShowInTaskbar = false;
                window.ShowActivated = false;
                var contentRendered = false;
                bool? contentRenderedAtReveal = null;
                window.ContentRendered += (_, _) => contentRendered = true;
                // Keep the dispatcher busy at Loaded priority: below Render (render ticks still run), above Input
                // (where WPF queues ContentRendered). Stops as soon as the window is revealed (or at the deadline).
                var deadline = DateTime.UtcNow + Deadline;
                void Busy()
                {
                    if (!window.IsStartupCloaked && window.IsVisible) { contentRenderedAtReveal ??= contentRendered; return; }
                    if (DateTime.UtcNow > deadline) return;
                    _ = window.Dispatcher.BeginInvoke(Busy, System.Windows.Threading.DispatcherPriority.Loaded);
                }
                try
                {
                    window.Show();
                    Assert.True(window.IsStartupCloaked);
                    _ = window.Dispatcher.BeginInvoke(Busy, System.Windows.Threading.DispatcherPriority.Loaded);
                    Assert.True(await StaTestHost.WaitForAsync(() => contentRenderedAtReveal is not null, Deadline), "The window was never revealed");
                    Assert.False(contentRenderedAtReveal, "The window was only revealed by ContentRendered, after the startup work");
                }
                finally { window.Close(); }
            });
        }
        finally { StartupWindowReveal.SetCloaked = previous; }
    }

    [Fact(DisplayName = "The launch file's decode is already running (or done) when Show() returns, under the key the presenter uses")]
    public async Task Show_WithALaunchFile_StartsItsDecodeBeforeTheFolderIsPresented()
    {
        using var dataRoot = new DataRootFixture();
        using var folder = new TempRoot("startup-prewarm");
        var target = Path.Combine(folder.Path, "b.png");
        WriteImage(Path.Combine(folder.Path, "a.png"));
        WriteImage(target);
        await StaTestHost.RunAsync(async () =>
        {
            var presented = new List<string>();
            var window = TestAppHost.CreateMainWindow(target, new TestHostHooks { OnPresented = presented.Add, DisablePreload = true });
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            try
            {
                window.Show();
                Assert.Empty(presented); // the folder load has not presented anything yet (it resumes on this dispatcher)
                var previews = window.ViewModel.PreviewService!;
                var key = previews.GetCurrentCacheKey(Path.GetFullPath(target));
                Assert.True(previews.HasInflightPreview(key) || previews.TryGetCachedPreview(key, out _),
                    "The launch file's decode was not started when the window was shown");

                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, Deadline), "Nothing was presented");
                Assert.Equal(Path.GetFullPath(target), presented[0], ignoreCase: true);
            }
            finally { window.Close(); }
        });
    }

    [Fact(DisplayName = "PrewarmInitialImage skips files the viewer would not decode the same way (unsupported, camera RAW)")]
    public async Task PrewarmInitialImage_SkipsUnsupportedAndRawFiles()
    {
        using var dataRoot = new DataRootFixture();
        using var folder = new TempRoot("startup-prewarm-skip");
        var text = Path.Combine(folder.Path, "notes.txt");
        var raw = Path.Combine(folder.Path, "shot.cr2");
        var png = Path.Combine(folder.Path, "c.png");
        File.WriteAllText(text, "x");
        File.WriteAllBytes(raw, [1, 2, 3]);
        WriteImage(png);
        await StaTestHost.RunAsync(async () =>
        {
            var window = TestAppHost.CreateMainWindow(null, new TestHostHooks { DisablePreload = true });
            try
            {
                Assert.Null(window.ViewModel.PrewarmInitialImage(text));
                Assert.Null(window.ViewModel.PrewarmInitialImage(raw));
                var started = window.ViewModel.PrewarmInitialImage(png);
                Assert.NotNull(started);
                await started!;
                var previews = window.ViewModel.PreviewService!;
                Assert.True(previews.TryGetCachedPreview(previews.GetCurrentCacheKey(Path.GetFullPath(png)), out _));
            }
            finally { window.Close(); }
        });
    }

    private static bool ShowsWindow(IntPtr windowPos) =>
        windowPos != IntPtr.Zero && ((uint)Marshal.ReadInt32(windowPos, (2 * IntPtr.Size) + (4 * sizeof(int))) & SwpShowWindow) != 0;

    private static readonly JsonSerializerOptions PlacementJson = new() { IncludeFields = true, WriteIndented = true };

    private static void WritePlacement(string file, int showCommand, WindowPlacementService.Rectangle bounds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var placement = new WindowPlacementService.WindowPlacement { Length = Marshal.SizeOf<WindowPlacementService.WindowPlacement>(), ShowCommand = showCommand, NormalPosition = bounds };
        File.WriteAllText(file, JsonSerializer.Serialize(placement, PlacementJson));
    }

    private static WindowPlacementService.Rectangle PrimaryWorkArea()
    {
        var monitor = MonitorFromPoint(default, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        Assert.True(GetMonitorInfo(monitor, ref info));
        return info.Work;
    }

    private static void WriteImage(string path)
    {
        var pixels = new byte[16 * 16 * 4];
        Array.Fill(pixels, (byte)0x90);
        var bitmap = BitmapSource.Create(16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 16 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public WindowPlacementService.Rectangle Monitor; public WindowPlacementService.Rectangle Work; public int Flags; }
}
