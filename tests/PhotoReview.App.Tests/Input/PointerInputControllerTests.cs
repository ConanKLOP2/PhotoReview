using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Input;

/// <summary>
/// AR13a: the wheel / pan / click-to-zoom / kinetic-glide state machine moved out of MainWindow, driven through a
/// fake <see cref="IImageSurface"/> (no WPF window, no STA).
/// </summary>
public sealed class PointerInputControllerTests
{
    private readonly FakeSurface _surface = new();
    private readonly ViewerState _viewer = new();
    private readonly AppSettings _settings = new() { MouseWheelAction = MouseWheelAction.Zoom, ClickToZoomEnabled = true, ClickZoomPercent = 200, KineticPanEnabled = true };
    private readonly ViewportOperationVersion _version = new();
    private int _next;
    private int _previous;
    private int _fits;
    private bool _hasImages = true;
    private readonly PointerInputController _controller;

    public PointerInputControllerTests()
    {
        _viewer.ResetFit(800, 600);
        _controller = new PointerInputController(_surface, _viewer, () => _settings, _version, new PointerCommands(
            () => _hasImages,
            () => { _next++; return Task.CompletedTask; },
            () => { _previous++; return Task.CompletedTask; },
            _viewer.ZoomToActualSize,
            () => { _fits++; return Task.CompletedTask; }));
    }

    // ---- wheel ----

    [Fact]
    public async Task Wheel_ZoomMode_ZoomsAndScrollsToKeepThePointUnderTheCursor()
    {
        await _controller.OnWheelAsync(120, ctrl: false, new Point(400, 300));

        Assert.False(_viewer.IsFit);
        Assert.Equal(1, _surface.YieldCount);
        Assert.Single(_surface.Scrolls);
        Assert.Equal(0, _next + _previous);
    }

    [Fact]
    public async Task Wheel_NavigateMode_OneNotchDownGoesToTheNextImage_CtrlZoomsInstead()
    {
        _settings.MouseWheelAction = MouseWheelAction.Navigate;

        await _controller.OnWheelAsync(-120, ctrl: false, new Point(10, 10));
        Assert.Equal(1, _next);
        Assert.True(_viewer.IsFit);

        await _controller.OnWheelAsync(120, ctrl: false, new Point(10, 10));
        Assert.Equal(1, _previous);

        await _controller.OnWheelAsync(-120, ctrl: true, new Point(10, 10));
        Assert.Equal(1, _next);
        Assert.False(_viewer.IsFit);
    }

    [Fact]
    public async Task Wheel_NavigateMode_WithoutImages_DoesNotNavigate()
    {
        _settings.MouseWheelAction = MouseWheelAction.Navigate;
        _hasImages = false;

        await _controller.OnWheelAsync(-120, ctrl: false, new Point(10, 10));

        Assert.Equal(0, _next);
    }

    [Fact]
    public async Task Wheel_StopsARunningGlide()
    {
        StartGlide();
        _settings.MouseWheelAction = MouseWheelAction.Navigate;

        await _controller.OnWheelAsync(30, ctrl: false, new Point(10, 10)); // a partial notch: no outcome at all

        Assert.Equal(1, _surface.Unhooks);
        Assert.Null(_surface.RenderHandler);
    }

    [Fact]
    public async Task ZoomAtPoint_SupersededByALaterOperation_DoesNotScroll()
    {
        _surface.HoldYields = true;
        var first = _controller.OnWheelAsync(120, ctrl: false, new Point(400, 300));
        _version.Next(); // e.g. Fit started while the zoom waited for the render pass
        _surface.ReleaseYields();
        await first;

        Assert.Empty(_surface.Scrolls);
    }

    // ---- pan ----

    [Fact]
    public void PressMoveRelease_Pans_CapturesAndReleases_AndTheReleaseIsNotAClick()
    {
        ZoomInSoTheImageCanPan();

        Assert.True(_controller.OnImagePress(MouseButton.Left, 1, new Point(100, 100), timestamp: 1000));
        Assert.Equal(1, _surface.Captures);
        Assert.True(_surface.PanCursor);

        Assert.True(_controller.OnImageMove(true, new Point(120, 110), timestamp: 1010));
        Assert.Equal((480.0, 390.0), _surface.Scrolls[^1]); // offsets 500/400 minus the pointer delta

        var zoom = _viewer.Zoom;
        Assert.True(_controller.OnImageRelease(new Point(120, 110), timestamp: 1500)); // rested: no glide
        Assert.Equal(zoom, _viewer.Zoom);
        Assert.Equal(0, _fits);
        Assert.Equal(1, _surface.CaptureReleases);
        Assert.False(_surface.PanCursor);
        Assert.Equal(0, _surface.Hooks);
    }

    [Fact]
    public void Move_BelowTheDragThreshold_IsNotHandled_AndTheReleaseIsAClick()
    {
        ZoomInSoTheImageCanPan();
        _controller.OnImagePress(MouseButton.Left, 1, new Point(100, 100), timestamp: 0);

        Assert.False(_controller.OnImageMove(true, new Point(101, 101), timestamp: 5));
        Assert.True(_controller.OnImageRelease(new Point(101, 101), timestamp: 10));

        Assert.Equal(1, _fits); // a click at the click zoom (200 %) goes back to Fit
        Assert.Equal(0, _surface.YieldCount);
    }

    [Fact]
    public void Move_WithoutATrackedPress_OrWithTheButtonUp_DoesNothing()
    {
        ZoomInSoTheImageCanPan();
        Assert.False(_controller.OnImageMove(true, new Point(200, 200), timestamp: 0));

        _controller.OnImagePress(MouseButton.Left, 1, new Point(100, 100), timestamp: 0);
        Assert.False(_controller.OnImageMove(false, new Point(200, 200), timestamp: 5));
        Assert.Empty(_surface.Scrolls);
    }

    [Fact]
    public void LostCapture_EndsThePan()
    {
        ZoomInSoTheImageCanPan();
        _controller.OnImagePress(MouseButton.Left, 1, new Point(100, 100), timestamp: 0);

        _controller.OnLostCapture();

        Assert.False(_controller.OnImageMove(true, new Point(200, 200), timestamp: 5));
        Assert.False(_surface.PanCursor);
    }

    // ---- click-to-zoom ----

    [Fact]
    public void Click_InFit_WithClickToZoom_ZoomsToTheClickZoomAtTheCursor()
    {
        Assert.True(_controller.OnImagePress(MouseButton.Left, 1, new Point(50, 50), timestamp: 0));
        Assert.False(_surface.PanCursor); // Fit: nothing to pan, the press is tracked only as a click

        Assert.True(_controller.OnImageRelease(new Point(50, 50), timestamp: 10));

        Assert.False(_viewer.IsFit);
        Assert.Equal(2.0, _viewer.Zoom, 6);
        Assert.Single(_surface.Scrolls);
    }

    [Fact]
    public void Click_InFit_WithClickToZoomDisabled_IsNotTracked()
    {
        _settings.ClickToZoomEnabled = false;

        Assert.False(_controller.OnImagePress(MouseButton.Left, 1, new Point(50, 50), timestamp: 0));
        Assert.False(_controller.OnImageRelease(new Point(50, 50), timestamp: 10));

        Assert.True(_viewer.IsFit);
        Assert.Equal(0, _surface.Captures);
        Assert.Equal(0, _surface.YieldCount);
    }

    [Fact]
    public void DoubleClick_AlwaysFits_AndItsReleaseIsNotAClick()
    {
        ZoomInSoTheImageCanPan();

        Assert.True(_controller.OnImagePress(MouseButton.Left, 2, new Point(50, 50), timestamp: 0));
        Assert.Equal(1, _fits);
        Assert.Equal(0, _surface.Captures);

        Assert.False(_controller.OnImageRelease(new Point(50, 50), timestamp: 10));
        Assert.Equal(1, _fits);
    }

    // ---- ClickZoom shortcut / context menu (centre-anchored, independent of ClickToZoomEnabled) ----

    [Fact]
    public async Task ToggleClickZoomAsync_InFit_ZoomsToClickZoomPercentAtTheViewportCentre()
    {
        _settings.ClickToZoomEnabled = false; // shortcut must work even when the mouse click feature is off

        await _controller.ToggleClickZoomAsync();

        Assert.False(_viewer.IsFit);
        Assert.Equal(2.0, _viewer.Zoom, 6); // ClickZoomPercent = 200 in the fixture
        Assert.Single(_surface.Scrolls);
        Assert.Equal(0, _fits);
    }

    [Fact]
    public async Task ToggleClickZoomAsync_AtTheClickZoomLevel_ReturnsToFit()
    {
        await _controller.ToggleClickZoomAsync(); // Fit -> click zoom
        await _controller.ToggleClickZoomAsync(); // click zoom -> Fit

        Assert.Equal(1, _fits);
    }

    [Fact]
    public async Task ToggleClickZoomAsync_WithoutImages_DoesNothing()
    {
        _hasImages = false;

        await _controller.ToggleClickZoomAsync();

        Assert.True(_viewer.IsFit);
        Assert.Empty(_surface.Scrolls);
    }

    [Fact]
    public async Task SetClickZoomLevelAsync_ZoomsStraightToTheGivenPercent_NoFitToggle()
    {
        await _controller.SetClickZoomLevelAsync(150);

        Assert.False(_viewer.IsFit);
        Assert.Equal(1.5, _viewer.Zoom, 6);

        // Calling it again at the SAME percent must zoom again (not toggle back to Fit, unlike ToggleClickZoomAsync).
        await _controller.SetClickZoomLevelAsync(150);
        Assert.False(_viewer.IsFit);
        Assert.Equal(1.5, _viewer.Zoom, 6);
        Assert.Equal(0, _fits);
    }

    [Fact]
    public async Task SetClickZoomLevelAsync_WithoutImages_DoesNothing()
    {
        _hasImages = false;

        await _controller.SetClickZoomLevelAsync(150);

        Assert.True(_viewer.IsFit);
        Assert.Empty(_surface.Scrolls);
    }

    // ---- kinetic glide ----

    [Fact]
    public void FastRelease_StartsAGlide_ThatStepsOnNewFramesAndUnhooksWhenItStops()
    {
        StartGlide();
        Assert.Equal(1, _surface.Hooks);
        var handler = Assert.IsType<EventHandler>(_surface.RenderHandler);

        var scrolls = _surface.Scrolls.Count;
        handler(null, new FrameArgs(TimeSpan.FromMilliseconds(0)));   // first frame only records the time
        handler(null, new FrameArgs(TimeSpan.FromMilliseconds(0)));   // same frame again: no step
        Assert.Equal(scrolls, _surface.Scrolls.Count);
        handler(null, new FrameArgs(TimeSpan.FromMilliseconds(16)));
        Assert.Equal(scrolls + 1, _surface.Scrolls.Count);

        for (var t = 32; t < 20_000 && _surface.RenderHandler is not null; t += 16)
            handler(null, new FrameArgs(TimeSpan.FromMilliseconds(t)));

        Assert.Null(_surface.RenderHandler);
        Assert.Equal(1, _surface.Unhooks);
    }

    [Fact]
    public void PressDuringAGlide_StopsIt_AndThatPressIsNeverAClick()
    {
        StartGlide();
        _settings.ClickToZoomEnabled = true;

        _controller.OnWindowPreviewMouseDown();
        Assert.Null(_surface.RenderHandler);
        _controller.OnImagePress(MouseButton.Left, 1, new Point(300, 300), timestamp: 5000);
        _controller.OnImageRelease(new Point(300, 300), timestamp: 5010);

        Assert.Equal(0, _fits);
        Assert.Equal(2.0, _viewer.Zoom, 6);
    }

    [Fact]
    public void Navigation_StopsAGlide_ButTheSameIndexDoesNot()
    {
        _controller.OnCurrentIndexChanged(3);
        StartGlide();

        _controller.OnCurrentIndexChanged(3);
        Assert.NotNull(_surface.RenderHandler);

        _controller.OnCurrentIndexChanged(4);
        Assert.Null(_surface.RenderHandler);
    }

    [Fact]
    public void WindowClosed_UnhooksARunningGlide()
    {
        StartGlide();

        _controller.OnWindowClosed();

        Assert.Null(_surface.RenderHandler);
        Assert.Equal(1, _surface.Unhooks);
    }

    [Fact]
    public void Frame_AfterTheWindowUnloaded_StopsTheGlide()
    {
        StartGlide();
        _surface.IsLoaded = false;

        _surface.RenderHandler!(null, new FrameArgs(TimeSpan.Zero));

        Assert.Null(_surface.RenderHandler);
    }

    [Fact]
    public void Release_AfterADrag_WithKineticPanDisabled_DoesNotGlide()
    {
        _settings.KineticPanEnabled = false;
        ZoomInSoTheImageCanPan();
        Drag();

        Assert.Equal(0, _surface.Hooks);
    }

    private void ZoomInSoTheImageCanPan()
    {
        _viewer.SetZoom(2.0);
        _surface.ExtentWidth = 4000;
        _surface.ExtentHeight = 3000;
        _surface.HorizontalOffset = 500;
        _surface.VerticalOffset = 400;
    }

    private void StartGlide()
    {
        ZoomInSoTheImageCanPan();
        Drag();
        Assert.NotNull(_surface.RenderHandler);
    }

    /// <summary>A fast horizontal drag (2 DIP/ms) released while still moving.</summary>
    private void Drag()
    {
        _controller.OnImagePress(MouseButton.Left, 1, new Point(400, 300), timestamp: 1000);
        for (var t = 10; t <= 60; t += 10) _controller.OnImageMove(true, new Point(400 - 2 * t, 300), timestamp: 1000 + t);
        Assert.True(_controller.OnImageRelease(new Point(270, 300), timestamp: 1065));
    }

    private sealed class FrameArgs(TimeSpan time) : EventArgs
    {
        public TimeSpan Time { get; } = time;
    }

    // ---- arrow-key panning of a zoomed image ----

    [Fact]
    public void Arrow_ZoomedImage_PansTenPercentOfTheViewport_WithoutNavigating()
    {
        _surface.ExtentWidth = 2000;
        _surface.ExtentHeight = 1500;
        _surface.HorizontalOffset = 100;
        _surface.VerticalOffset = 100;

        Assert.True(_controller.TryPanByArrow(Key.Right, isRepeat: false));
        Assert.Equal((180, 100), _surface.Scrolls[^1]);
        Assert.True(_controller.TryPanByArrow(Key.Down, isRepeat: false));
        Assert.Equal((180, 160), _surface.Scrolls[^1]);
        Assert.True(_controller.TryPanByArrow(Key.Left, isRepeat: true));
        Assert.Equal((100, 160), _surface.Scrolls[^1]);
        Assert.True(_controller.TryPanByArrow(Key.Up, isRepeat: true));
        Assert.Equal((100, 100), _surface.Scrolls[^1]);
        Assert.Equal(0, _next + _previous);
    }

    [Fact]
    public void Arrow_StepIsClampedToTheEdge()
    {
        _surface.ExtentWidth = 2000;
        _surface.HorizontalOffset = 1170; // max 1200

        Assert.True(_controller.TryPanByArrow(Key.Right, isRepeat: false));
        Assert.Equal((1200, 0), _surface.Scrolls[^1]);
    }

    [Fact]
    public void Arrow_AtFit_IsNotUsed_SoLeftRightStillNavigate()
    {
        Assert.False(_controller.TryPanByArrow(Key.Right, isRepeat: false));
        Assert.False(_controller.TryPanByArrow(Key.Down, isRepeat: true));
        Assert.Empty(_surface.Scrolls);
    }

    [Fact]
    public void Arrow_AtTheEdge_RepeatIsSwallowed_FreshPressFallsThroughToNavigation()
    {
        _surface.ExtentWidth = 2000;
        _surface.HorizontalOffset = 1200;

        Assert.True(_controller.TryPanByArrow(Key.Right, isRepeat: true));
        Assert.False(_controller.TryPanByArrow(Key.Right, isRepeat: false));
        Assert.Empty(_surface.Scrolls);
        // The other direction still pans.
        Assert.True(_controller.TryPanByArrow(Key.Left, isRepeat: false));
        Assert.Equal((1120, 0), _surface.Scrolls[^1]);
    }

    [Fact]
    public void Arrow_OnlyScrollableAxisPans_OtherKeysAndNoImageAreIgnored()
    {
        _surface.ExtentWidth = 2000; // wide image: vertical fits

        Assert.False(_controller.TryPanByArrow(Key.Up, isRepeat: false));
        Assert.False(_controller.TryPanByArrow(Key.A, isRepeat: false));
        _hasImages = false;
        Assert.False(_controller.TryPanByArrow(Key.Right, isRepeat: false));
        Assert.Empty(_surface.Scrolls);
    }

    private sealed class FakeSurface : IImageSurface
    {
        private readonly List<TaskCompletionSource> _heldYields = [];

        public bool IsLoaded { get; set; } = true;
        public double HorizontalOffset { get; set; }
        public double VerticalOffset { get; set; }
        public double ViewportWidth { get; set; } = 800;
        public double ViewportHeight { get; set; } = 600;
        public double ExtentWidth { get; set; } = 800;
        public double ExtentHeight { get; set; } = 600;
        public (double Horizontal, double Vertical) DragThreshold => (4, 4);
        public List<(double H, double V)> Scrolls { get; } = [];
        public int YieldCount { get; private set; }
        public bool HoldYields { get; set; }
        public int Captures { get; private set; }
        public int CaptureReleases { get; private set; }
        public bool PanCursor { get; private set; }
        public int Hooks { get; private set; }
        public int Unhooks { get; private set; }
        public EventHandler? RenderHandler { get; private set; }
        private bool _captured;

        public void ScrollTo(double horizontal, double vertical)
        {
            HorizontalOffset = horizontal;
            VerticalOffset = vertical;
            Scrolls.Add((horizontal, vertical));
        }

        public void UpdateLayout() { }

        public Task YieldToRenderAsync()
        {
            YieldCount++;
            if (!HoldYields) return Task.CompletedTask;
            var pending = new TaskCompletionSource();
            _heldYields.Add(pending);
            return pending.Task;
        }

        public void ReleaseYields()
        {
            foreach (var pending in _heldYields) pending.SetResult();
            _heldYields.Clear();
        }

        public Point ImageOrigin => new(0, 0);
        public Point ToImageElement(Point surfacePoint) => new(surfacePoint.X + HorizontalOffset, surfacePoint.Y + VerticalOffset);
        public double ImageActualWidth => ExtentWidth;
        public double ImageActualHeight => ExtentHeight;
        public (double Width, double Height)? SourceSize => (ExtentWidth, ExtentHeight);

        public void CaptureMouse()
        {
            Captures++;
            _captured = true;
        }

        public void ReleaseMouseCapture()
        {
            if (!_captured) return;
            _captured = false;
            CaptureReleases++;
        }

        public void SetPanCursor(bool panning) => PanCursor = panning;

        public void HookRenderFrame(EventHandler handler)
        {
            Assert.Null(RenderHandler); // never hooked twice
            Hooks++;
            RenderHandler = handler;
        }

        public void UnhookRenderFrame(EventHandler handler)
        {
            Assert.Same(RenderHandler, handler); // the same delegate instance as the hook
            Unhooks++;
            RenderHandler = null;
        }

        public TimeSpan? RenderingTime(EventArgs e) => e is FrameArgs frame ? frame.Time : null;
    }
}
