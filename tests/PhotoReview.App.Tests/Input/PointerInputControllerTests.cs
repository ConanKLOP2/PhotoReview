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
    private readonly AppSettings _settings = new() { MouseWheelAction = MouseWheelAction.Zoom, ClickToZoomEnabled = true, ClickZoomPercent = 200, KineticPanEnabled = true, KeyboardZoomAnchor = KeyboardZoomAnchor.Pointer };
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

    // ---- PR-B: Fit width / Fit height / keep zoom across images ----

    [Fact]
    public async Task FitWidthAsync_NoMouse_UsesTheConfiguredAnchor_DefaultCentre()
    {
        _viewer.SetSourceSize(2000, 4000);
        _viewer.DpiScale = 1.0;

        await _controller.FitWidthAsync(mouseOverViewport: null);

        Assert.False(_viewer.IsFit);
        // SetZoom (inside ZoomToFitWidth) resets MaxImageWidth to Infinity, so FitWidthZoom reads back 0 afterwards --
        // assert the concrete expected zoom (800 DIP viewport / 2000 px source) instead of re-reading the property.
        Assert.Equal(0.4, _viewer.Zoom, 6);
        Assert.Single(_surface.Scrolls);
    }

    [Fact]
    public async Task FitWidthAsync_NoMouse_TopThirdAnchor_ScrollsLessFarThanCentre()
    {
        // A scrollable range is needed for the two anchors to land on different offsets (with none, both clamp to 0).
        _surface.ExtentHeight = 1200;
        _viewer.SetSourceSize(2000, 4000);
        _viewer.DpiScale = 1.0;
        _settings.FitWidthAnchor = FitWidthAnchor.Centre;

        await _controller.FitWidthAsync(null);
        var centreVertical = _surface.Scrolls[^1].V;

        _surface.Scrolls.Clear();
        _surface.VerticalOffset = 0; // the fake keeps its offset across calls; reset so both anchors start from the same place
        _viewer.SetZoom(1.0); // undo the previous FitWidth so the second call starts from the same place
        _settings.FitWidthAnchor = FitWidthAnchor.TopThird;

        await _controller.FitWidthAsync(null);
        var topThirdVertical = _surface.Scrolls[^1].V;

        // Top-third keeps a point further UP the image at the viewport centre, so it needs LESS downward scroll.
        Assert.True(topThirdVertical < centreVertical);
    }

    [Fact]
    public async Task FitWidthAsync_MouseOverViewport_AnchorsAtTheMousePointInstead()
    {
        _viewer.SetSourceSize(2000, 4000);
        _viewer.DpiScale = 1.0;

        await _controller.FitWidthAsync(new Point(400, 100)); // near the top of the (pre-zoom Fit) viewport

        Assert.False(_viewer.IsFit);
        Assert.Single(_surface.Scrolls);
    }

    [Fact]
    public async Task FitHeightAsync_AlwaysAnchorsAtImageCentre()
    {
        _viewer.SetSourceSize(4000, 2000);
        _viewer.DpiScale = 1.0;

        await _controller.FitHeightAsync();

        Assert.False(_viewer.IsFit);
        // 600 DIP viewport height / 2000 px source height (see the FitWidthZoom comment above for why this can't
        // re-read FitHeightZoom after the fact).
        Assert.Equal(0.3, _viewer.Zoom, 6);
        Assert.Single(_surface.Scrolls);
    }

    [Fact]
    public async Task FitWidthAsync_WithoutImages_DoesNothing()
    {
        _hasImages = false;

        await _controller.FitWidthAsync(null);

        Assert.True(_viewer.IsFit);
        Assert.Empty(_surface.Scrolls);
    }

    [Fact]
    public async Task ApplyInitialViewAsync_Fit_ResetsFitWithNoScrollPlacementPass()
    {
        _viewer.SetZoom(3.0);

        await _controller.ApplyInitialViewAsync(InitialViewMode.Fit, clickZoomPercent: 100);

        Assert.True(_viewer.IsFit);
        Assert.Empty(_surface.Scrolls); // Fit's own convergence pass is FitViewController's job, not this one's
    }

    [Fact]
    public async Task ApplyInitialViewAsync_FitWidth_AppliesZoomThenPlacesScrollAtTheConfiguredAnchor()
    {
        _viewer.SetSourceSize(2000, 4000);
        _viewer.DpiScale = 1.0;

        await _controller.ApplyInitialViewAsync(InitialViewMode.FitWidth, clickZoomPercent: 100);

        Assert.Equal(0.4, _viewer.Zoom, 6); // 800 DIP viewport width / 2000 px source width
        Assert.Single(_surface.Scrolls);
    }

    [Fact]
    public async Task ApplyInitialViewAsync_ClickZoomLevel_ZoomsButDoesNotScroll()
    {
        await _controller.ApplyInitialViewAsync(InitialViewMode.ClickZoomLevel, clickZoomPercent: 150);

        Assert.Equal(1.5, _viewer.Zoom, 6);
        Assert.Empty(_surface.Scrolls);
    }

    [Fact]
    public async Task ApplyInitialViewAsync_KeepZoomAcrossImages_IsANoOp()
    {
        _viewer.SetZoom(3.0);
        _settings.KeepZoomAcrossImages = true;

        await _controller.ApplyInitialViewAsync(InitialViewMode.Fit, clickZoomPercent: 100);

        Assert.Equal(3.0, _viewer.Zoom);
        Assert.False(_viewer.IsFit);
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

    // ---- keyboard/menu zoom anchor (feat/zoom-key-anchor) ----

    [Fact]
    public async Task ZoomInAsync_WithoutImages_DoesNothing()
    {
        _hasImages = false;

        await _controller.ZoomInAsync();

        Assert.True(_viewer.IsFit);
        Assert.Empty(_surface.Scrolls);
    }

    [Fact]
    public async Task ZoomOutAsync_WithoutImages_DoesNothing()
    {
        _hasImages = false;

        await _controller.ZoomOutAsync();

        Assert.True(_viewer.IsFit);
        Assert.Empty(_surface.Scrolls);
    }

    [Fact]
    public async Task ZoomInAsync_IncreasesZoom_ZoomOutAsync_DecreasesItBack()
    {
        ZoomInSoTheImageCanPan(); // not Fit, so the step is symmetric around the current zoom
        var before = _viewer.Zoom;

        await _controller.ZoomInAsync();
        Assert.True(_viewer.Zoom > before);

        var afterIn = _viewer.Zoom;
        await _controller.ZoomOutAsync();
        Assert.True(_viewer.Zoom < afterIn);
    }

    [Fact]
    public async Task ZoomInAsync_StopsARunningGlide()
    {
        StartGlide();

        await _controller.ZoomInAsync();

        Assert.Null(_surface.RenderHandler);
    }

    [Fact]
    public async Task ZoomInAsync_PointerOverTheViewport_ZoomsAtThePointer_NotTheCentre()
    {
        _surface.PointerPosition = new Point(100, 50);

        await _controller.ZoomInAsync();

        Assert.Equal(new Point(100, 50), _surface.ToImageElementCalls[^1]);
    }

    [Fact]
    public async Task ZoomInAsync_PointerOutsideTheViewport_FallsBackToTheCentre()
    {
        _surface.PointerPosition = null;

        await _controller.ZoomInAsync();

        Assert.Equal(new Point(_surface.ViewportWidth / 2, _surface.ViewportHeight / 2), _surface.ToImageElementCalls[^1]);
    }

    [Fact]
    public async Task ZoomInAsync_ViewportCentreSetting_IgnoresThePointer_EvenWhenItIsOverTheViewport()
    {
        _settings.KeyboardZoomAnchor = KeyboardZoomAnchor.ViewportCentre;
        _surface.PointerPosition = new Point(100, 50);

        await _controller.ZoomInAsync();

        Assert.Equal(new Point(_surface.ViewportWidth / 2, _surface.ViewportHeight / 2), _surface.ToImageElementCalls[^1]);
    }

    [Fact]
    public async Task ZoomOutAsync_PointerOverTheViewport_ZoomsAtThePointer_NotTheCentre()
    {
        _surface.PointerPosition = new Point(650, 500);

        await _controller.ZoomOutAsync();

        Assert.Equal(new Point(650, 500), _surface.ToImageElementCalls[^1]);
    }

    [Fact]
    public async Task ZoomActualSizeAsync_PointerOverTheViewport_ZoomsAtThePointer_NotTheCentre()
    {
        _surface.PointerPosition = new Point(120, 80);

        await _controller.ZoomActualSizeAsync();

        Assert.Equal(new Point(120, 80), _surface.ToImageElementCalls[^1]);
    }

    [Fact]
    public async Task ToggleClickZoomAsync_PointerOverTheViewport_ZoomsAtThePointer_NotTheCentre()
    {
        _surface.PointerPosition = new Point(120, 80);

        await _controller.ToggleClickZoomAsync();

        Assert.Equal(new Point(120, 80), _surface.ToImageElementCalls[^1]);
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

    /// <summary>
    /// R10 (2026-09-27 code review): a press that stops a running glide but never reaches <c>OnImagePress</c> (e.g. a
    /// toolbar button -- only the window-level tunnel fires for it) sets <c>_pressStoppedGlide</c>. Traced against WPF's
    /// routed-event order: <c>Window_PreviewMouseDown</c> is subscribed directly on the Window (the root of the visual
    /// tree the image lives in), and the tunnel for a SINGLE physical press visits every ancestor, including the Window,
    /// before it can reach the image's own <c>PreviewMouseLeftButtonDown</c> handler -- so a LATER, separate press that
    /// does land on the image always re-triggers <c>OnWindowPreviewMouseDown</c> for ITSELF first, overwriting the stale
    /// flag with that press's own (correct) <c>StopKinetic()</c> result before <c>OnImagePress</c> ever reads it. There is
    /// no code path in this app where <c>OnImagePress</c> fires without an immediately preceding <c>OnWindowPreviewMouseDown</c>
    /// call for the very same press (popups/dialogs that skip the window tunnel also never reach the image, since the
    /// image lives only in the main window's own visual tree). Conclusion: R10 does not reproduce; this test pins the
    /// correct current behavior instead of a code change, so a future refactor that breaks this ordering assumption fails
    /// the test first.
    /// </summary>
    [Fact]
    public void GlideStoppedByAnUnrelatedPress_ThenASeparateImageClick_StillTriggersClickToZoom()
    {
        StartGlide();

        // Press A: e.g. a toolbar button. Only the window-level tunnel fires -- OnImagePress is never called for it.
        _controller.OnWindowPreviewMouseDown();
        Assert.Null(_surface.RenderHandler); // press A's own action stopped the glide

        // Press B: a later, unrelated press+release on the image with no glide running any more. WPF tunnels
        // OnWindowPreviewMouseDown for THIS press too, immediately before OnImagePress, for the same physical click.
        _controller.OnWindowPreviewMouseDown();
        Assert.True(_controller.OnImagePress(MouseButton.Left, 1, new Point(300, 300), timestamp: 6000));
        Assert.True(_controller.OnImageRelease(new Point(300, 300), timestamp: 6010)); // no drag: a click

        Assert.Equal(1, _fits); // click-to-zoom fired (already at ClickZoomPercent -> back to Fit), not swallowed
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

    // ---- glide smoothing (KineticGlideSmoothing) ----

    /// <summary>
    /// Predict steps to the monitor refresh each callback is expected on, whatever RenderingTime says: a glide driven by
    /// irregular callbacks (several per refresh, missed refreshes) with a useless RenderingTime must land exactly where
    /// an Off glide lands when its RenderingTime is the refresh time.
    /// </summary>
    [Theory]
    [InlineData(60.0)]
    [InlineData(75.0)]
    [InlineData(144.0)]
    [InlineData(240.0)]
    public void Predict_StepsToTheMonitorRefresh_LikeOffWithRefreshAlignedFrameTimes(double hz)
    {
        var period = System.Diagnostics.Stopwatch.Frequency / hz;
        var periodMs = 1000 / hz;
        // Callback times in refreshes: several per refresh, a missed one, one just before a refresh.
        double[] callbacks = [0.1, 0.3, 0.6, 1.2, 1.5, 3.4, 3.5, 4.05, 4.9, 6.2, 7.7, 7.8, 9.1, 12.4, 13.3];

        _settings.KineticGlideSmoothing = KineticGlideSmoothing.Predict;
        _surface.DisplayTiming = new PhotoReview.Core.Abstractions.DisplayTiming(LastVBlank: 0, RefreshPeriod: (long)Math.Round(period));
        StartGlide();
        var predicted = new List<(double, double)>();
        for (var i = 0; i < callbacks.Length && _surface.RenderHandler is not null; i++)
        {
            _surface.Timestamp = (long)(callbacks[i] * period);
            _surface.RenderHandler(null, new FrameArgs(TimeSpan.FromMilliseconds(i + 1))); // RenderingTime says 1 ms per callback
            predicted.Add((_surface.HorizontalOffset, _surface.VerticalOffset));
        }

        var reference = new PointerInputControllerTests();
        reference._settings.KineticGlideSmoothing = KineticGlideSmoothing.Off;
        reference.StartGlide();
        var off = new List<(double, double)>();
        for (var i = 0; i < callbacks.Length && reference._surface.RenderHandler is not null; i++)
        {
            // The refresh each callback is shown on: the first vblank at least the present lead after it.
            var lead = GlideFrameClock.PresentLeadMs / periodMs;
            var refresh = Math.Ceiling(callbacks[i] + lead);
            reference._surface.RenderHandler(null, new FrameArgs(TimeSpan.FromMilliseconds(refresh * periodMs)));
            off.Add((reference._surface.HorizontalOffset, reference._surface.VerticalOffset));
        }

        Assert.Equal(off.Count, predicted.Count);
        for (var i = 0; i < off.Count; i++)
        {
            // 0.01 DIP: TimeSpan keeps 100 ns, so the Off reference's frame times are rounded slightly.
            Assert.Equal(off[i].Item1, predicted[i].Item1, tolerance: 0.01);
            Assert.Equal(off[i].Item2, predicted[i].Item2, tolerance: 0.01);
        }
        Assert.NotEqual(500, _surface.HorizontalOffset); // it did glide
    }

    [Fact]
    public void Predict_ReadsTheMonitorTimingDuringTheDrag_OffNeverDoes()
    {
        _settings.KineticGlideSmoothing = KineticGlideSmoothing.Predict;
        ZoomInSoTheImageCanPan();
        _controller.OnImagePress(MouseButton.Left, 1, new Point(400, 300), timestamp: 1000);
        Assert.True(_surface.DisplayTimingReads > 0, "The press did not start the monitor clock.");
        _controller.CancelPan();

        var off = new PointerInputControllerTests();
        off._settings.KineticGlideSmoothing = KineticGlideSmoothing.Off;
        off._surface.DisplayTiming = new PhotoReview.Core.Abstractions.DisplayTiming(0, 1000);
        off.StartGlide();
        for (var t = 0; t < 2000 && off._surface.RenderHandler is not null; t += 16)
            off._surface.RenderHandler(null, new FrameArgs(TimeSpan.FromMilliseconds(t)));
        Assert.Equal(0, off._surface.DisplayTimingReads);
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
        _settings.KineticPanEnabled = false; // this test asserts the instant step, not the kinetic glide
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
    public void Arrow_StepFollowsTheArrowPanStepPercentSetting()
    {
        _settings.KineticPanEnabled = false; // this test asserts the instant step, not the kinetic glide
        _settings.ArrowPanStepPercent = 25;
        _surface.ExtentWidth = 2000;
        _surface.ExtentHeight = 1500;
        _surface.HorizontalOffset = 100;
        _surface.VerticalOffset = 100;

        Assert.True(_controller.TryPanByArrow(Key.Right, isRepeat: false));
        Assert.Equal((300, 100), _surface.Scrolls[^1]); // 25 % of the 800-wide viewport
        Assert.True(_controller.TryPanByArrow(Key.Down, isRepeat: false));
        Assert.Equal((300, 250), _surface.Scrolls[^1]); // 25 % of the 600-high viewport
    }

    [Fact]
    public void Arrow_StepIsClampedToTheEdge()
    {
        _settings.KineticPanEnabled = false; // this test asserts the instant step, not the kinetic glide
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
    public void Arrow_LegacySetting_AtTheEdge_RepeatIsSwallowed_FreshPressFallsThroughToNavigation()
    {
        _settings.KineticPanEnabled = false; // this test asserts the instant step, not the kinetic glide
        _settings.ArrowKeyNavigatesAtZoomEdge = true;
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
    public void Arrow_LegacySetting_OnlyScrollableAxisPans_OtherKeysAndNoImageAreIgnored()
    {
        _settings.ArrowKeyNavigatesAtZoomEdge = true;
        _surface.ExtentWidth = 2000; // wide image: vertical fits

        Assert.False(_controller.TryPanByArrow(Key.Up, isRepeat: false));
        Assert.False(_controller.TryPanByArrow(Key.A, isRepeat: false));
        _hasImages = false;
        Assert.False(_controller.TryPanByArrow(Key.Right, isRepeat: false));
        Assert.Empty(_surface.Scrolls);
    }

    [Fact]
    public void Arrow_Default_OtherKeysAndNoImageAreIgnored()
    {
        _surface.ExtentWidth = 2000;
        Assert.False(_controller.TryPanByArrow(Key.A, isRepeat: false));
        _hasImages = false;
        Assert.False(_controller.TryPanByArrow(Key.Right, isRepeat: false));
        Assert.Empty(_surface.Scrolls);
    }

    [Fact]
    public void Arrow_Default_FreshPressAtTheEdge_IsConsumed_NoScrollNoNavigation()
    {
        Assert.False(_settings.ArrowKeyNavigatesAtZoomEdge);
        _surface.ExtentWidth = 2000;
        _surface.ExtentHeight = 1500;
        _surface.HorizontalOffset = 1200;
        _surface.VerticalOffset = 900;

        Assert.True(_controller.TryPanByArrow(Key.Right, isRepeat: false));
        Assert.True(_controller.TryPanByArrow(Key.Right, isRepeat: true));
        Assert.True(_controller.TryPanByArrow(Key.Down, isRepeat: false));
        _surface.HorizontalOffset = 0;
        _surface.VerticalOffset = 0;
        Assert.True(_controller.TryPanByArrow(Key.Left, isRepeat: false));
        Assert.True(_controller.TryPanByArrow(Key.Up, isRepeat: false));
        Assert.Empty(_surface.Scrolls);
        Assert.Equal(0, _next + _previous);
    }

    [Fact]
    public void Arrow_Default_OnAnAxisThatCannotScroll_IsConsumedAsANoOp()
    {
        _surface.ExtentHeight = 1500; // tall image zoomed only vertically
        _surface.VerticalOffset = 100;

        Assert.True(_controller.TryPanByArrow(Key.Left, isRepeat: false));
        Assert.True(_controller.TryPanByArrow(Key.Right, isRepeat: false));
        Assert.Empty(_surface.Scrolls);
        Assert.Equal(0, _next + _previous);
    }

    [Fact]
    public void Arrow_Default_AtFit_FallsThrough()
    {
        Assert.False(_controller.TryPanByArrow(Key.Left, isRepeat: false));
        Assert.False(_controller.TryPanByArrow(Key.Right, isRepeat: false));
        Assert.False(_controller.TryPanByArrow(Key.Up, isRepeat: false));
        Assert.Empty(_surface.Scrolls);
    }

    // ---- arrow-key kinetic impulse (feat/zoom-key-anchor: KineticPanEnabled true, the default in this fixture) ----

    [Fact]
    public void Arrow_KineticPanEnabled_Panned_StartsAGlide_InsteadOfAnInstantScrollTo()
    {
        _surface.ExtentWidth = 2000;
        _surface.ExtentHeight = 1500;
        _surface.HorizontalOffset = 100;
        _surface.VerticalOffset = 100;

        Assert.True(_controller.TryPanByArrow(Key.Right, isRepeat: false));

        Assert.Equal(1, _surface.Hooks);
        Assert.NotNull(_surface.RenderHandler);
        Assert.Empty(_surface.Scrolls); // no instant ScrollTo -- only the glide's own frame steps move it
        Assert.Equal(0, _next + _previous);
    }

    [Fact]
    public void Arrow_KineticPanEnabled_DrivenToRest_TravelsAboutTheConfiguredStep()
    {
        _surface.ExtentWidth = 2000;
        _surface.ExtentHeight = 1500;
        _surface.HorizontalOffset = 100;
        _surface.VerticalOffset = 100;
        var before = _surface.HorizontalOffset;

        _controller.TryPanByArrow(Key.Right, isRepeat: false);
        var handler = _surface.RenderHandler!;
        for (var t = 0; t < 20_000 && _surface.RenderHandler is not null; t += 16)
            handler(null, new FrameArgs(TimeSpan.FromMilliseconds(t)));

        Assert.Null(_surface.RenderHandler); // the glide stopped on its own
        // The step at ArrowPanStepPercent=10 % of an 800-wide viewport is 80 DIP (matches the instant-mode test).
        Assert.Equal(before + 80, _surface.HorizontalOffset, tolerance: 1.0);
    }

    [Fact]
    public void Arrow_KineticPanEnabled_AutoRepeat_AddsToTheRunningGlide_WithoutRehooking()
    {
        _surface.ExtentWidth = 2000;
        _surface.ExtentHeight = 1500;
        _surface.HorizontalOffset = 100;

        Assert.True(_controller.TryPanByArrow(Key.Right, isRepeat: false));
        Assert.Equal(1, _surface.Hooks);
        var handlerAfterFirst = _surface.RenderHandler;

        Assert.True(_controller.TryPanByArrow(Key.Right, isRepeat: true));

        Assert.Equal(1, _surface.Hooks); // still hooked once, not re-hooked
        Assert.Same(handlerAfterFirst, _surface.RenderHandler);
    }

    [Fact]
    public void Arrow_KineticPanEnabled_AtTheEdge_ConsumedAsANoOp_DoesNotStartAGlide()
    {
        _surface.ExtentWidth = 2000;
        _surface.ExtentHeight = 1500;
        _surface.HorizontalOffset = 1200; // already at the max

        Assert.True(_controller.TryPanByArrow(Key.Right, isRepeat: false));

        Assert.Equal(0, _surface.Hooks);
        Assert.Null(_surface.RenderHandler);
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

        /// <summary>Every mouse/anchor point <see cref="PointerInputController"/> zoomed at (test seam for the keyboard-zoom-anchor tests).</summary>
        public List<Point> ToImageElementCalls { get; } = [];

        public Point ToImageElement(Point surfacePoint)
        {
            ToImageElementCalls.Add(surfacePoint);
            return new(surfacePoint.X + HorizontalOffset, surfacePoint.Y + VerticalOffset);
        }
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
        public long Timestamp { get; set; }
        public int DisplayTimingReads { get; private set; }
        private PhotoReview.Core.Abstractions.DisplayTiming? _displayTiming;

        public PhotoReview.Core.Abstractions.DisplayTiming? DisplayTiming
        {
            get
            {
                DisplayTimingReads++;
                return _displayTiming;
            }
            set => _displayTiming = value;
        }

        public Point? PointerPosition { get; set; }
    }
}
