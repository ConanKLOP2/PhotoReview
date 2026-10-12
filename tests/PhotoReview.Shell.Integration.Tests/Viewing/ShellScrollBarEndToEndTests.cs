using PhotoReview.App.Input;
using PhotoReview.Shell.Integration.Tests.Input;
using PhotoReview.Shell.Integration.Tests.Startup;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Integration.Tests.Viewing;

/// <summary>
/// WP-26: thanh cuộn của <c>ShellMainWindow</c> THẬT (ShellStartup, HWND thật, WARP). Thông điệp WM_LBUTTONDOWN/WM_MOUSEMOVE/
/// WM_LBUTTONUP/WM_MOUSEWHEEL gửi vào cửa sổ đi qua chuỗi handler thật (thanh cuộn trước ảnh), controller viewport thật. Mọi kỳ vọng
/// tính từ hình học mà shell đang có (không phụ thuộc kích thước màn hình runner: cửa sổ có thể bị kẹp vào vùng làm việc nhỏ).
/// Chạy tuần tự cùng collection với startup.
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed partial class ShellScrollBarEndToEndTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static bool WaitUntil(Func<bool> condition) => SpinWait.SpinUntil(condition, Bound);

    private static nint Pack(int x, int y) => (nint)(((uint)(ushort)y << 16) | (ushort)x);

    private static string Diag(ShellRun run) =>
        $"marks=[{string.Join(", ", run.MarkNames)}] error={run.Error} exit={run.ExitCode} firstPresent={run.Main?.FirstPresentDone}";

    private static ShellRun StartWithBigImage(string label, int width = 4000, int height = 3000)
    {
        var root = ShellRun.NewRoot(label);
        var path = Path.Combine(root, "photos", "big.png");
        TestPng.WriteTo(path, width, height);
        var run = ShellRun.Start(root, [path]);
        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong: " + Diag(run));
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented), "ảnh đầu không được vẽ: " + Diag(run));
        return run;
    }

    private static void StopAndAssertClean(ShellRun run)
    {
        run.RequestClose();
        Assert.True(run.WaitForExit(TimeSpan.FromSeconds(90)), "shell không thoát sau WM_CLOSE: " + Diag(run));
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
    }

    /// <summary>100 %: ảnh luôn lớn hơn vùng nhìn của mọi cửa sổ thử; chờ layout xong.</summary>
    private static void ZoomToActualSize(ShellRun run)
    {
        var main = run.Main!;
        run.OnUi(() => main.ViewModel.Viewer.ZoomToActualSize());
        Assert.True(WaitUntil(() => run.OnUi(() => !main.Viewport.IsLayoutPosted && !main.ViewModel.Viewer.IsFit
            && main.ScrollBars.CurrentLayout.Vertical.Visible && main.ScrollBars.CurrentLayout.Horizontal.Visible)),
            "không có cả hai thanh cuộn sau khi zoom 100 %: " + Diag(run));
    }

    private static (ScrollBarLayout Layout, double Dpi) Snapshot(ShellRun run) =>
        run.OnUi(() => (run.Main!.ScrollBars.CurrentLayout, run.Main.Window.DpiScale));

    private static (int X, int Y) ToPixels(double dipX, double dipY, double dpi) =>
        ((int)Math.Round(dipX * dpi), (int)Math.Round(dipY * dpi));

    private static void Send(ShellRun run, uint message, nint wParam, int pixelX, int pixelY) =>
        PendingUser32.SendMessage(run.Main!.Hwnd, message, wParam, Pack(pixelX, pixelY));

    private const int MkLButton = 0x0001;

    [Fact]
    public void DraggingTheVerticalThumb_ThroughRealMessages_ScrollsTheViewportProportionally()
    {
        using var run = StartWithBigImage("sb-drag");
        var main = run.Main!;
        ZoomToActualSize(run);
        var (layout, dpi) = Snapshot(run);
        var box = layout.Vertical;
        var (px, py) = ToPixels(box.Thumb.X + 4, box.Thumb.Y + (box.Thumb.Height / 2), dpi);

        Send(run, WindowMessages.WmLButtonDown, MkLButton, px, py);
        Assert.True(run.OnUi(() => main.ScrollBars.IsDragging), "nhấn con trượt không bắt đầu kéo");
        Assert.True(run.OnUi(() => main.Window.HasPointerCapture), "kéo con trượt phải giữ capture chuột");

        var movedPixels = Math.Max(10, (int)(box.Travel * dpi / 3));
        Send(run, WindowMessages.WmMouseMove, MkLButton, px, py + movedPixels);

        var grab = (py / dpi) - box.Thumb.Y;
        var expected = ScrollBarGeometry.OffsetForThumbStart(box, ((py + movedPixels) / dpi) - grab);
        Assert.True(expected > 0);
        Assert.Equal(expected, run.OnUi(() => main.Viewport.VerticalOffset), 6);
        Assert.Equal(0, run.OnUi(() => main.Viewport.HorizontalOffset), 9);
        // Con trượt đã di chuyển đúng bằng quãng chuột đi (con trỏ không rời con trượt).
        var thumbAfter = run.OnUi(() => main.ScrollBars.CurrentLayout.Vertical.Thumb);
        Assert.Equal(box.Thumb.Y + (movedPixels / dpi), thumbAfter.Y, 6);

        Send(run, WindowMessages.WmLButtonUp, 0, px, py + movedPixels);
        Assert.False(run.OnUi(() => main.ScrollBars.IsDragging));
        Assert.False(run.OnUi(() => main.Window.HasPointerCapture));
        Assert.Equal(expected, run.OnUi(() => main.Viewport.VerticalOffset), 6);
        StopAndAssertClean(run);
    }

    [Fact]
    public void DraggingTheHorizontalThumb_PastBothEnds_ClampsAtTheMaximumOffset()
    {
        using var run = StartWithBigImage("sb-hdrag");
        var main = run.Main!;
        ZoomToActualSize(run);
        var (layout, dpi) = Snapshot(run);
        var box = layout.Horizontal;
        var (px, py) = ToPixels(box.Thumb.X + (box.Thumb.Width / 2), box.Thumb.Y + 4, dpi);

        Send(run, WindowMessages.WmLButtonDown, MkLButton, px, py);
        Send(run, WindowMessages.WmMouseMove, MkLButton, px + 20_000, py);

        Assert.Equal(box.MaxOffset, run.OnUi(() => main.Viewport.HorizontalOffset), 6);
        Send(run, WindowMessages.WmMouseMove, MkLButton, px - 20_000, py);
        Assert.Equal(0, run.OnUi(() => main.Viewport.HorizontalOffset), 9);
        Send(run, WindowMessages.WmLButtonUp, 0, px, py);
        StopAndAssertClean(run);
    }

    [Fact]
    public void ClickingTheTrack_PagesByOneViewport_AndAPressOnTheBarNeverPansTheImage()
    {
        using var run = StartWithBigImage("sb-page");
        var main = run.Main!;
        ZoomToActualSize(run);
        var (layout, dpi) = Snapshot(run);
        var box = layout.Vertical;
        var viewportHeight = run.OnUi(() => main.Viewport.ViewportHeight);

        // Dưới con trượt, trong rãnh: một trang xuống; thả ngay (không lặp).
        var (px, py) = ToPixels(box.Track.X + 4, box.Thumb.Bottom + 10, dpi);
        Send(run, WindowMessages.WmLButtonDown, MkLButton, px, py);
        Send(run, WindowMessages.WmLButtonUp, 0, px, py);
        Assert.Equal(Math.Min(box.MaxOffset, viewportHeight), run.OnUi(() => main.Viewport.VerticalOffset), 6);

        // Nhấn trên lề của thanh (không phải rãnh), nơi hộp ảnh 4000x3000 nằm phía dưới: không kéo ảnh, không giữ capture.
        var offsetBefore = run.OnUi(() => (main.Viewport.HorizontalOffset, main.Viewport.VerticalOffset));
        var (bx, by) = ToPixels(box.Bar.X + 0.4, box.Bar.Y + (box.Bar.Height / 2), dpi);
        Send(run, WindowMessages.WmLButtonDown, MkLButton, bx, by);
        Assert.False(run.OnUi(() => main.Window.HasPointerCapture));
        Send(run, WindowMessages.WmMouseMove, MkLButton, bx - 50, by - 50);
        Send(run, WindowMessages.WmLButtonUp, 0, bx - 50, by - 50);
        Assert.Equal(offsetBefore, run.OnUi(() => (main.Viewport.HorizontalOffset, main.Viewport.VerticalOffset)));
        StopAndAssertClean(run);
    }

    [Fact]
    public void BarsAreAbsent_InFitMode_AndAppearAfterZoom_ReflowingTheViewport()
    {
        using var run = StartWithBigImage("sb-fit");
        var main = run.Main!;
        Assert.True(run.OnUi(() => main.ViewModel.Viewer.IsFit));
        Assert.False(run.OnUi(() => main.ScrollBars.CurrentLayout.AnyVisible));
        var fitViewportWidth = run.OnUi(() => main.Viewport.ViewportWidth);
        var client = run.OnUi(() => main.Window.ClientSizeDip);
        Assert.Equal(client.Width, fitViewportWidth, 6); // không thanh nào chiếm chỗ

        ZoomToActualSize(run);
        var width = run.OnUi(() => main.Viewport.ViewportWidth);
        Assert.Equal(client.Width - 10, width, 6); // thanh dọc 10 DIP chiếm chỗ như WPF
        var box = run.OnUi(() => main.ScrollBars.CurrentLayout.Vertical);
        Assert.Equal(width, box.Bar.X, 6);
        StopAndAssertClean(run);
    }

    [Fact]
    public void ResizingTheWindow_ReflowsTheBars_AndTheThumbStaysInsideTheTrack()
    {
        using var run = StartWithBigImage("sb-resize");
        var main = run.Main!;
        ZoomToActualSize(run);
        var before = run.OnUi(() => main.Window.ClientSizeDip);

        // Cửa sổ nhỏ hơn (an toàn trên mọi màn hình runner): đo lại theo client THẬT sau khi đổi.
        Assert.True(User32.SetWindowPos(main.Hwnd, 0, 0, 0, 760, 560, WindowMessages.SwpNoMove | WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate));
        Assert.True(WaitUntil(() => run.OnUi(() => main.Window.ClientSizeDip != before && !main.Viewport.IsLayoutPosted)),
            "kích thước client không đổi: " + Diag(run));

        var (client, layout) = run.OnUi(() => (main.Window.ClientSizeDip, main.ScrollBars.CurrentLayout));
        Assert.True(layout.Vertical.Visible && layout.Horizontal.Visible);
        Assert.Equal(client.Width - 10, layout.Vertical.Bar.X, 6);
        Assert.Equal(client.Height - 10, layout.Horizontal.Bar.Y, 6);
        Assert.Equal(client.Height - 10, layout.Vertical.Bar.Height, 6);
        foreach (var box in new[] { layout.Vertical, layout.Horizontal })
        {
            Assert.True(box.ThumbVisible);
            Assert.True(box.Thumb.X >= box.Track.X - 1e-9 && box.Thumb.Right <= box.Track.Right + 1e-9, $"{box.Axis}: con trượt ngoài rãnh");
            Assert.True(box.Thumb.Y >= box.Track.Y - 1e-9 && box.Thumb.Bottom <= box.Track.Bottom + 1e-9, $"{box.Axis}: con trượt ngoài rãnh");
        }

        // Kéo con trượt vẫn đúng với hình học mới.
        var dpi = run.OnUi(() => main.Window.DpiScale);
        var box2 = layout.Vertical;
        var (px, py) = ToPixels(box2.Thumb.X + 4, box2.Thumb.Y + (box2.Thumb.Height / 2), dpi);
        Send(run, WindowMessages.WmLButtonDown, MkLButton, px, py);
        Send(run, WindowMessages.WmMouseMove, MkLButton, px, py + 30);
        Send(run, WindowMessages.WmLButtonUp, 0, px, py + 30);
        var grab = (py / dpi) - box2.Thumb.Y;
        Assert.Equal(ScrollBarGeometry.OffsetForThumbStart(box2, ((py + 30) / dpi) - grab), run.OnUi(() => main.Viewport.VerticalOffset), 6);
        StopAndAssertClean(run);
    }

    [Fact]
    public void Zooming_ThroughTheWheel_ResizesTheThumb_AndTheFrameStillPresents()
    {
        using var run = StartWithBigImage("sb-zoom");
        var main = run.Main!;
        ZoomToActualSize(run);
        var before = run.OnUi(() => main.ScrollBars.CurrentLayout.Vertical);
        var (cx, cy) = run.OnUi(() => ((int)(main.Viewport.ViewportWidth / 2 * main.Window.DpiScale), (int)(main.Viewport.ViewportHeight / 2 * main.Window.DpiScale)));

        // Ctrl + wheel lên: zoom vào; toạ độ wheel là toạ độ MÀN HÌNH.
        var hwnd = main.Hwnd;
        var screen = new ScreenPoint { X = cx, Y = cy };
        Assert.True(ClientToScreen(hwnd, ref screen));
        var wParam = (nint)(((uint)(ushort)(short)120 << 16) | 0x0008u);
        PendingUser32.SendMessage(hwnd, WindowMessages.WmMouseWheel, wParam, Pack(screen.X, screen.Y));

        Assert.True(WaitUntil(() => run.OnUi(() => main.ViewModel.Viewer.Zoom > 1.0 && !main.Viewport.IsLayoutPosted)), "wheel không zoom");
        var after = run.OnUi(() => main.ScrollBars.CurrentLayout.Vertical);
        Assert.True(after.Thumb.Height < before.Thumb.Height, $"con trượt phải ngắn đi: {before.Thumb.Height} -> {after.Thumb.Height}");
        var layout = run.OnUi(() => main.Viewport.Layout);
        Assert.Equal(after.TrackLength * layout.ViewportHeight / layout.ExtentHeight, after.Thumb.Height, 4);

        // Khung có thanh cuộn vẫn vẽ + Present được trên bề mặt thật (lỗi vẽ bị bắt và không tăng bộ đếm).
        var total = run.OnUi(() => main.Stats.Total);
        var drawn = run.OnUi(() => main.ScrollBars.DrawnFrames);
        run.OnUi(() => main.RenderNow());
        Assert.True(run.OnUi(() => main.Stats.Total) > total, "khung có thanh cuộn không Present được");
        Assert.True(run.OnUi(() => main.ScrollBars.DrawnFrames) > drawn, "khung thật không vẽ thanh cuộn (DrawScrollBars chưa được gọi)");
        StopAndAssertClean(run);
    }

    [Fact]
    public void ThumbDragLatency_IsMeasuredAtTheHandler_WhichAppliesTheOffsetSynchronously()
    {
        using var run = StartWithBigImage("sb-latency");
        var main = run.Main!;
        ZoomToActualSize(run);
        var (layout, dpi) = Snapshot(run);
        var box = layout.Vertical;
        var (px, py) = ToPixels(box.Thumb.X + 4, box.Thumb.Y + (box.Thumb.Height / 2), dpi);
        var travelPixels = Math.Max(2, (int)(box.Travel * dpi) - 2);

        Send(run, WindowMessages.WmLButtonDown, MkLButton, px, py);
        var samples = new List<double>();
        for (var i = 0; i < 200; i++)
        {
            var target = py + (int)((i % 2 == 0 ? 1 : 0.2) * travelPixels / 2);
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            Send(run, WindowMessages.WmMouseMove, MkLButton, px, target);
            samples.Add((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        Send(run, WindowMessages.WmLButtonUp, 0, px, py);
        samples.Sort();
        var p50 = samples[samples.Count / 2];
        var p95 = samples[(int)(samples.Count * 0.95)];
        var max = samples[^1];
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pr-wp26-drag-latency.txt"),
            $"WM_MOUSEMOVE khi keo con truot (SendMessage -> offset da ap): p50 {p50:F3} ms, p95 {p95:F3} ms, max {max:F3} ms ({samples.Count} mau)");
        Assert.True(p95 < 50, $"p95 {p95:F2} ms");
        StopAndAssertClean(run);
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint hwnd, ref ScreenPoint point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }
}
