using PhotoReview.App.Windowing;
using PhotoReview.Shell.Integration.Tests.Startup;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;
using PhotoReview.Shell.Win32.Windowing;

namespace PhotoReview.Shell.Integration.Tests.Windowing;

/// <summary>
/// WP-28b: the real shell window (HWND, ShellStartup, WARP) entering F11 on a FAKE monitor (<see cref="IFullscreenNative"/> seam of
/// WP-25) of 720x480 px and changing DPI there: the layout of the first frame after the change (the Fit box the renderer draws) is
/// the Fit of the fake screen - the same geometry the pixel golden cases <c>f11-*</c> pin against WPF in
/// <c>PixelGoldenExtTests</c> (Shell.Tests, offscreen). Size and DPI come from the fake monitor, never from the runner's.
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed unsafe class ShellFullscreenFrameGeometryTests
{
    private const int ScreenWidth = 720;
    private const int ScreenHeight = 480;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private sealed class FakeScreenNative(ScreenRect screen) : IFullscreenNative
    {
        private readonly Win32FullscreenNative _real = Win32FullscreenNative.Instance;

        public WindowShowState CurrentState(nint hwnd) => _real.CurrentState(hwnd);

        public bool TryGetMonitorRect(nint hwnd, out ScreenRect monitor)
        {
            monitor = screen;
            return true;
        }

        public bool TryGetWindowRect(nint hwnd, out ScreenRect window) => _real.TryGetWindowRect(hwnd, out window);

        public void MoveTo(nint hwnd, ScreenRect rect) => _real.MoveTo(hwnd, rect);

        public void Show(nint hwnd, WindowShowState state) => _real.Show(hwnd, state);
    }

    private static ShellRun StartRun(string label)
    {
        var root = ShellRun.NewRoot(label);
        var screen = new ScreenRect(0, 0, ScreenWidth, ScreenHeight);
        var run = ShellRun.Start(root, [TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"))],
            configure: o => o with { Windowing = new ShellWindowingSeams(Native: new FakeScreenNative(screen)) });
        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong: " + run.Error);
        Assert.True(SpinWait.SpinUntil(() => run.Main!.FirstImagePresented, Bound), "ảnh đầu không được vẽ");
        return run;
    }

    private static void Stop(ShellRun run)
    {
        run.RequestClose();
        Assert.True(run.WaitForExit(Bound), "shell không thoát sau WM_CLOSE");
        Assert.Null(run.Error);
    }

    /// <summary>Fit of a picture in a viewport (DIP): the box ViewerState/ViewportController lay out, centred.</summary>
    private static (double X, double Y, double Width, double Height) ExpectedFit(double viewportWidth, double viewportHeight)
    {
        var scale = Math.Min(viewportWidth / TestJpeg.Width, viewportHeight / TestJpeg.Height);
        var width = TestJpeg.Width * scale;
        var height = TestJpeg.Height * scale;
        return ((viewportWidth - width) / 2, (viewportHeight - height) / 2, width, height);
    }

    private static void AssertFitBox(ShellRun run, double dpi, int pixelWidth, int pixelHeight)
    {
        var main = run.Main!;
        var (client, viewport, origin, width, height) = run.OnUi(() =>
        {
            main.RenderNow(); // layout + the draw of the first frame after the change
            var layout = main.Viewport.Layout;
            return (main.Window.ClientSizePixels, (layout.ViewportWidth, layout.ViewportHeight), main.Viewport.ImageOrigin,
                main.Viewport.ImageActualWidth, main.Viewport.ImageActualHeight);
        });

        Assert.Equal((pixelWidth, pixelHeight), client);
        Assert.Equal(pixelWidth / dpi, viewport.ViewportWidth, 0.6);
        Assert.Equal(pixelHeight / dpi, viewport.ViewportHeight, 0.6);
        var expected = ExpectedFit(pixelWidth / dpi, pixelHeight / dpi);
        Assert.Equal(expected.Width, width, 1.0);
        Assert.Equal(expected.Height, height, 1.0);
        Assert.Equal(expected.X, origin.X, 1.0);
        Assert.Equal(expected.Y, origin.Y, 1.0);
        Assert.True(run.OnUi(() => main.ViewModel.Viewer.IsFit));
    }

    [Fact]
    public void F11OnAFakeScreen_FirstFrame_IsAlreadyTheFitOfThatScreen()
    {
        using var run = StartRun("f11geom");
        var main = run.Main!;
        var dpi = run.OnUi(() => main.Window.DpiScale);

        Assert.True(run.OnUi(() => main.ApplyFullscreen(true)));
        // Entering F11 covers the runner's real monitor; the fake 720x480 screen is then what Windows reports (resolution change in F11,
        // WM_DISPLAYCHANGE): WP25-REFIT puts the window onto it and the next frame must already be its Fit.
        PendingUser32.SendMessage(main.Hwnd, WindowMessages.WmDisplayChange, 32, 0);
        Assert.True(SpinWait.SpinUntil(() => run.OnUi(() => main.Window.ClientSizePixels == (ScreenWidth, ScreenHeight)), Bound),
            "cửa sổ F11 không được ép lên màn hình giả 720x480");

        AssertFitBox(run, dpi, ScreenWidth, ScreenHeight);
        Stop(run);
    }

    [Fact]
    public void DpiChangedInF11_SamePixels_FirstFrameIsTheFitOfTheSameScreenAtTheNewScale()
    {
        using var run = StartRun("f11dpisame");
        var main = run.Main!;
        Assert.True(run.OnUi(() => main.ApplyFullscreen(true)));
        PendingUser32.SendMessage(main.Hwnd, WindowMessages.WmDisplayChange, 32, 0);
        Assert.True(SpinWait.SpinUntil(() => run.OnUi(() => main.Window.ClientSizePixels == (ScreenWidth, ScreenHeight)), Bound));

        // 150 % monitor, same 720x480 px: the viewport in DIP shrinks 1.5x.
        run.OnUi(() =>
        {
            var suggested = new Rect { Left = 0, Top = 0, Right = ScreenWidth, Bottom = ScreenHeight };
            PendingUser32.SendMessage(main.Hwnd, WindowMessages.WmDpiChanged, (nint)(144 | (144 << 16)), (nint)(&suggested));
        });

        AssertFitBox(run, 1.5, ScreenWidth, ScreenHeight);
        Assert.Equal(1.5, run.OnUi(() => main.Window.DpiScale), 0.001);
        Stop(run);
    }
}
