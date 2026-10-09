using System.Windows;
using System.Windows.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Input;

/// <summary>What the pointer gestures do outside the image view (the view-model's commands and Fit).</summary>
internal sealed record PointerCommands(
    Func<bool> HasImages,
    Func<Task> NextAsync,
    Func<Task> PreviousAsync,
    Action ZoomActualSize,
    Func<Task> ApplyFitAsync);

/// <summary>
/// AR13a (moved verbatim from MainWindow, feat/mouse-zoom): wheel (zoom / navigate), zoom at a point, click-to-zoom,
/// drag-pan with kinetic glide, and mouse capture. The decisions live in the pure helpers
/// (<see cref="WheelGestureInterpreter"/>, <see cref="PointerGestures"/>, <see cref="PanVelocityTracker"/>,
/// <see cref="KineticScroller"/>); this is the state machine that joins them to the view through
/// <see cref="IImageSurface"/>. <c>MainWindow</c> only forwards WPF input and sets <c>e.Handled</c> from the returned
/// flags. UI thread only; holds no reference to the window.
/// </summary>
internal sealed class PointerInputController
{
    private readonly IImageSurface _surface;
    private readonly ViewerState _viewer;
    private readonly Func<AppSettings> _settings;
    private readonly ViewportOperationVersion _viewportVersion;
    private readonly PointerCommands _commands;

    private bool _isPanning;
    private bool _panMoved;
    private Point _panStartPoint;
    private Point _panLastPoint;
    private bool _pressCanPan;        // the tracked press scrolls the image (zoomed and larger than the viewport)
    private bool _pressConsumed;      // the tracked press only stopped a glide: its release is never a click
    private bool _pressStoppedGlide;  // set by the window-level tunnel, read by the image's press handler
    private int _pressTimestamp;
    private int _lastSeenIndex = -1;
    private readonly WheelGestureInterpreter _wheelGestures = new();
    private readonly PanVelocityTracker _panVelocity = new();
    private KineticScroller _kinetic;
    private readonly EventHandler _kineticFrameHandler;
    private bool _kineticHooked;
    private GlideFrameClock _glideClock;
    private static readonly double TicksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;

    public PointerInputController(
        IImageSurface surface,
        ViewerState viewer,
        Func<AppSettings> settings,
        ViewportOperationVersion viewportVersion,
        PointerCommands commands)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _viewer = viewer ?? throw new ArgumentNullException(nameof(viewer));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _viewportVersion = viewportVersion ?? throw new ArgumentNullException(nameof(viewportVersion));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _kineticFrameHandler = OnKineticFrame; // one delegate for += / -= (no allocation per glide)
        _viewer.SourceSizeSwapping += OnSourceSizeSwapping;
    }

    /// <summary>Navigation stops a glide (a full-resolution swap of the same image does not).</summary>
    public void OnCurrentIndexChanged(int currentIndex)
    {
        if (currentIndex != _lastSeenIndex)
        {
            _lastSeenIndex = currentIndex;
            StopKinetic();
            _wheelGestures.Reset();
        }
    }

    /// <summary>ImageScroll PreviewMouseWheel (the caller has already set e.Handled).</summary>
    public async Task OnWheelAsync(int delta, bool ctrl, Point position)
    {
        StopKinetic();
        switch (_wheelGestures.Handle(delta, ctrl, _settings().MouseWheelAction))
        {
            case WheelOutcomeKind.Zoom:
                await ZoomAtPointAsync(position, () => _viewer.WheelZoom(delta));
                break;
            // Same path as the Next/Previous keys, so preload pacing and the navigation token apply unchanged.
            case WheelOutcomeKind.Next:
                if (_commands.HasImages()) await _commands.NextAsync();
                break;
            case WheelOutcomeKind.Previous:
                if (_commands.HasImages()) await _commands.PreviousAsync();
                break;
        }
    }

    /// <summary>
    /// ZoomActualSize: 100 % (ADR 0008) keeping the anchored image point in place -- the cursor when
    /// <see cref="Core.Settings.AppSettings.KeyboardZoomAnchor"/> is <see cref="KeyboardZoomAnchor.Pointer"/> and it
    /// is over the viewport, the viewport centre otherwise (see <see cref="ResolveKeyboardAnchor"/>).
    /// </summary>
    public async Task ZoomActualSizeAsync()
    {
        if (!_commands.HasImages()) return;
        CancelPan();
        StopKinetic();
        await ZoomAtPointAsync(ResolveKeyboardAnchor(), _commands.ZoomActualSize);
    }

    /// <summary>
    /// The anchor point (ImageScroll coordinates) for a keyboard/menu zoom: +/- (<see cref="ZoomInAsync"/>/
    /// <see cref="ZoomOutAsync"/>), <see cref="ZoomActualSizeAsync"/> and <see cref="ToggleClickZoomAsync"/>.
    /// Mouse wheel and click-to-zoom always anchor at the cursor and do not go through this helper.
    /// </summary>
    private Point ResolveKeyboardAnchor() =>
        PointerGestures.ResolveKeyboardZoomAnchor(_settings().KeyboardZoomAnchor, _surface.PointerPosition, _surface.ViewportWidth, _surface.ViewportHeight);

    /// <summary>+/- keyboard zoom in: anchored like <see cref="ZoomActualSizeAsync"/> instead of leaving the raw scroll offsets in place.</summary>
    public async Task ZoomInAsync()
    {
        if (!_commands.HasImages()) return;
        CancelPan();
        StopKinetic();
        await ZoomAtPointAsync(ResolveKeyboardAnchor(), () => _viewer.ZoomIn());
    }

    /// <summary>+/- keyboard zoom out: anchored like <see cref="ZoomActualSizeAsync"/> instead of leaving the raw scroll offsets in place.</summary>
    public async Task ZoomOutAsync()
    {
        if (!_commands.HasImages()) return;
        CancelPan();
        StopKinetic();
        await ZoomAtPointAsync(ResolveKeyboardAnchor(), () => _viewer.ZoomOut());
    }

    /// <summary>
    /// Applies a zoom change and then scrolls so the image point that was under <paramref name="mouse"/>
    /// (ImageScroll coordinates) stays under it. Shared by the wheel and click-to-zoom.
    /// </summary>
    private Task<bool> ZoomAtPointAsync(Point mouse, Action applyZoom) => ZoomToImagePointAsync(CaptureZoomAnchor(mouse), mouse, applyZoom);

    /// <summary>
    /// General form of <see cref="ZoomAtPointAsync"/> (PR-B, Fit width/Fit height): applies <paramref name="applyZoom"/>
    /// and then scrolls so the image-fraction point <paramref name="anchor"/> (see
    /// <see cref="MainWindowHelpers.ZoomImagePoint"/>) ends up under <paramref name="viewportPoint"/> (ImageScroll
    /// coordinates) instead of always the original cursor position -- e.g. the viewport centre for Fit width/height.
    /// The scroll is placed at once, in the same dispatcher operation as the zoom; after the render pass it is placed
    /// again only if the layout changed meanwhile, and a navigation that starts while the render pass is awaited
    /// (<see cref="ViewportOperationVersion"/>) drops that second placement. Returns false when the pass was superseded (or the surface unloaded),
    /// so a caller that chains a follow-up pass (the Fit scrollbar correction) must not run it.
    /// </summary>
    private async Task<bool> ZoomToImagePointAsync(MainWindowHelpers.ZoomImagePoint anchor, Point viewportPoint, Action applyZoom)
    {
        var version = _viewportVersion.Next();
        _zoomsAwaitingLayout++;
        try
        {
            applyZoom();
            // Place the scroll in the SAME dispatcher operation as the zoom. Scrolling only after the render pass let
            // that pass commit a frame with the new size at the OLD offsets (the image jumped towards its top-left for
            // one frame before settling -- the reported intermittent "ghosting" on keyboard zoom; measured by the
            // committed-frame probe in the PR). ScrollAnchorTo runs the layout itself, so the geometry is current.
            LayoutMetrics? placed = null;
            if (_surface.IsLoaded)
            {
                ScrollAnchorTo(anchor, viewportPoint);
                placed = CaptureLayoutMetrics();
            }
            await _surface.YieldToRenderAsync();
            if (version != _viewportVersion.Current || !_surface.IsLoaded) return false;
            // Re-anchor only if the render pass changed the layout the scroll was computed against (e.g. a bitmap swap).
            if (placed != CaptureLayoutMetrics()) ScrollAnchorTo(anchor, viewportPoint);
            return true;
        }
        finally { _zoomsAwaitingLayout--; }
    }

    private readonly record struct LayoutMetrics(double ImageWidth, double ImageHeight, double ExtentWidth, double ExtentHeight, double ViewportWidth, double ViewportHeight);

    private LayoutMetrics CaptureLayoutMetrics() => new(
        _surface.ImageActualWidth, _surface.ImageActualHeight, _surface.ExtentWidth, _surface.ExtentHeight, _surface.ViewportWidth, _surface.ViewportHeight);

    // Zoom gestures between applying the zoom and placing the scroll (the layout is stale meanwhile).
    private int _zoomsAwaitingLayout;

    /// <summary>
    /// ADR 0008 amendment (R4): the displayed bitmap of the same image was swapped for one of a slightly different size
    /// (RAW full decode), so the element is about to be resized. Keep the image point at the viewport centre there,
    /// so the content moves by at most a pixel or two instead of by (offset x size change), which is tens of pixels
    /// at a deep zoom. Skipped while a zoom gesture is awaiting its own layout (it anchors after the final layout).
    /// A newer viewport operation (zoom, navigation) supersedes the correction.
    /// </summary>
    private void OnSourceSizeSwapping(object? sender, EventArgs e)
    {
        if (_zoomsAwaitingLayout > 0 || !_surface.IsLoaded || _surface.ImageActualWidth <= 0 || _surface.ImageActualHeight <= 0) return;
        var centre = ViewportCentre;
        var anchor = CaptureZoomAnchor(centre); // layout still has the old size here
        AnchorAfterSwapAsync(anchor, centre, _viewportVersion.Current).FireAndLog("Re-anchor after bitmap swap failed");
    }

    private async Task AnchorAfterSwapAsync(MainWindowHelpers.ZoomImagePoint anchor, Point viewportPoint, long version)
    {
        await _surface.YieldToRenderAsync();
        if (version != _viewportVersion.Current || !_surface.IsLoaded) return;
        ScrollAnchorTo(anchor, viewportPoint);
    }

    /// <summary>After the layout settled: scrolls so image-fraction point <paramref name="anchor"/> sits at <paramref name="viewportPoint"/>.</summary>
    private void ScrollAnchorTo(MainWindowHelpers.ZoomImagePoint anchor, Point viewportPoint)
    {
        _surface.UpdateLayout();

        var imageOrigin = _surface.ImageOrigin;
        var offsets = MainWindowHelpers.CalculateZoomToPointOffsets(
            anchor,
            imageOrigin.X,
            imageOrigin.Y,
            _surface.ImageActualWidth,
            _surface.ImageActualHeight,
            viewportPoint.X,
            viewportPoint.Y,
            _surface.HorizontalOffset,
            _surface.VerticalOffset,
            _surface.ExtentWidth,
            _surface.ExtentHeight,
            _surface.ViewportWidth,
            _surface.ViewportHeight);
        _surface.ScrollTo(offsets.Horizontal, offsets.Vertical);
    }

    private Point ViewportCentre => new(_surface.ViewportWidth / 2, _surface.ViewportHeight / 2);

    /// <summary>
    /// FitWidth shortcut (PR-B): fills the viewport width. The vertical anchor always follows
    /// <see cref="AppSettings.FitWidthAnchor"/> (centre by default), regardless of mouse position -- same as the
    /// initial view (image change), see <see cref="ApplyInitialViewAsync"/>.
    /// </summary>
    public async Task FitWidthAsync()
    {
        if (!_commands.HasImages()) return;
        CancelPan();
        StopKinetic();
        var anchor = MainWindowHelpers.CalculateFitWidthAnchorPoint(_settings().FitWidthAnchor);
        if (!await ZoomToImagePointAsync(anchor, ViewportCentre, ApplyFitWidth)) return;
        await CorrectForSideScrollbarAsync(anchor, ApplyFitWidth, widthOnly: true);
    }

    /// <summary>FitHeight shortcut (PR-B): fills the viewport height, always centred (image point (0.5, 0.5)).</summary>
    public async Task FitHeightAsync()
    {
        if (!_commands.HasImages()) return;
        CancelPan();
        StopKinetic();
        var anchor = new MainWindowHelpers.ZoomImagePoint(0.5, 0.5);
        if (!await ZoomToImagePointAsync(anchor, ViewportCentre, ApplyFitHeight)) return;
        await CorrectForSideScrollbarAsync(anchor, ApplyFitHeight, widthOnly: false);
    }

    /// <summary>
    /// Applies the configured initial view on an image change (PR-B): the callback wired into
    /// <c>WpfPresentationSink.ApplyInitialViewMode</c> through <c>ImagePresenter.Sink</c>. <see cref="ViewerState.ApplyInitialViewMode"/>
    /// does the zoom/mode change (and is a no-op when <see cref="AppSettings.KeepZoomAcrossImages"/> is on); Fit
    /// width/Fit height then get their scroll placement here (per <see cref="AppSettings.FitWidthAnchor"/>,
    /// or always centre for Fit height) because the pointer controller owns the surface.
    /// </summary>
    public async Task ApplyInitialViewAsync(InitialViewMode mode, int clickZoomPercent)
    {
        if (!_commands.HasImages()) return;
        var settings = _settings();
        var applied = _viewer.ApplyInitialViewMode(mode, _surface.ViewportWidth, _surface.ViewportHeight, clickZoomPercent, settings.KeepZoomAcrossImages);
        if (!applied || mode is not (InitialViewMode.FitWidth or InitialViewMode.FitHeight)) return;

        var isFitWidth = mode == InitialViewMode.FitWidth;
        var anchor = isFitWidth
            ? MainWindowHelpers.CalculateFitWidthAnchorPoint(settings.FitWidthAnchor)
            : new MainWindowHelpers.ZoomImagePoint(0.5, 0.5);
        // The zoom was already applied by ViewerState above; this pass only places the scroll offsets.
        if (!await ZoomToImagePointAsync(anchor, ViewportCentre, static () => { })) return;
        await CorrectForSideScrollbarAsync(anchor, isFitWidth ? ApplyFitWidth : ApplyFitHeight, widthOnly: isFitWidth);
    }

    private void ApplyFitWidth()
    {
        _viewer.UpdateViewport(_surface.ViewportWidth, _surface.ViewportHeight, force: true);
        _viewer.ZoomToFitWidth();
    }

    private void ApplyFitHeight()
    {
        _viewer.UpdateViewport(_surface.ViewportWidth, _surface.ViewportHeight, force: true);
        _viewer.ZoomToFitHeight();
    }

    /// <summary>
    /// Fit width/height computes its zoom from the viewport size measured before the zoom is applied. Filling that
    /// dimension exactly can make the image overflow the OTHER dimension, and WPF then shows the scrollbar for it
    /// (by design -- that is how you reach the rest of the image); but that scrollbar eats into the viewport size
    /// the fit was computed against, so the fitted dimension no longer exactly fills it either, leaving a thin,
    /// unwanted scrollbar there too. Re-applying once against the now-current (narrower) viewport settles it.
    /// Callers run this only when the first pass was not superseded: a newer viewport operation (zoom, navigation)
    /// must not be overridden by a fresh-versioned re-application of the obsolete Fit (R04).
    /// </summary>
    private async Task CorrectForSideScrollbarAsync(MainWindowHelpers.ZoomImagePoint anchor, Action reapply, bool widthOnly)
    {
        var overflowed = widthOnly
            ? _surface.ExtentWidth > _surface.ViewportWidth + 0.5
            : _surface.ExtentHeight > _surface.ViewportHeight + 0.5;
        if (overflowed) await ZoomToImagePointAsync(anchor, ViewportCentre, reapply);
    }

    /// <summary>The image point under <paramref name="mouse"/> as a fraction of the displayed image.</summary>
    private MainWindowHelpers.ZoomImagePoint CaptureZoomAnchor(Point mouse)
    {
        var elementPoint = _surface.ToImageElement(mouse);
        // feat(zoom): the element is sized from original dims x zoom (no LayoutTransform), so the
        // anchor is carried across the zoom step as a fraction of the displayed image.
        if (_viewer.IsFit && _surface.SourceSize is { Width: > 0, Height: > 0 } source)
        {
            var sourcePoint = MainWindowHelpers.CalculateUniformImagePoint(
                _surface.ImageActualWidth,
                _surface.ImageActualHeight,
                source.Width,
                source.Height,
                elementPoint.X,
                elementPoint.Y);
            return MainWindowHelpers.NormalizeImagePoint(sourcePoint.X, sourcePoint.Y, source.Width, source.Height);
        }
        return MainWindowHelpers.NormalizeImagePoint(elementPoint.X, elementPoint.Y, _surface.ImageActualWidth, _surface.ImageActualHeight);
    }

    /// <summary>Window PreviewMouseDown: tunnels before the image's handler; any press stops a glide (remembered so that press is not a click).</summary>
    public void OnWindowPreviewMouseDown()
    {
        _pressStoppedGlide = StopKinetic();
    }

    /// <summary>MainImage PreviewMouseLeftButtonDown; returns the value for e.Handled.</summary>
    public bool OnImagePress(MouseButton changedButton, int clickCount, Point position, int timestamp)
    {
        var stoppedGlide = _pressStoppedGlide | StopKinetic();
        _pressStoppedGlide = false;

        // DF03: the second press of a double-click is always Fit (before the pan check since CanPan=false in
        // Fit). Its mouse-up finds no tracked press, so it never toggles click-to-zoom.
        if (changedButton == MouseButton.Left && PointerGestures.IsFitDoubleClick(clickCount))
        {
            CancelPan();
            _commands.ApplyFitAsync().FireAndLog("Fit on double-click failed");
            return true;
        }

        var canPan = CanPan();
        if (!canPan && !_settings().ClickToZoomEnabled) return false;

        _isPanning = true;
        _pressCanPan = canPan;
        _pressConsumed = stoppedGlide;
        _panMoved = false;
        _panStartPoint = position;
        _panLastPoint = _panStartPoint;
        _pressTimestamp = timestamp;
        _panVelocity.Reset();
        _panVelocity.Add(0, _panStartPoint.X, _panStartPoint.Y);
        if (canPan) _surface.SetPanCursor(true);
        // Start the monitor's vblank clock during the drag so its timing is known when a glide starts.
        if (canPan && _settings() is { KineticPanEnabled: true, KineticGlideSmoothing: not KineticGlideSmoothing.Off }) _ = _surface.DisplayTiming;
        _surface.CaptureMouse();
        return true;
    }

    /// <summary>MainImage PreviewMouseMove; returns the value for e.Handled.</summary>
    public bool OnImageMove(bool leftButtonPressed, Point position, int timestamp)
    {
        if (!_isPanning || !leftButtonPressed) return false;

        var point = position;
        var deltaX = point.X - _panLastPoint.X;
        var deltaY = point.Y - _panLastPoint.Y;
        _panLastPoint = point;
        _panVelocity.Add(unchecked(timestamp - _pressTimestamp), point.X, point.Y);
        // OC15: once the drag threshold is crossed it stays crossed until the pan ends, so only
        // test it while still below; delta/scroll below always run.
        if (!_panMoved && MainWindowHelpers.IsBeyondDragThreshold(
                point.X - _panStartPoint.X,
                point.Y - _panStartPoint.Y,
                _surface.DragThreshold.Horizontal,
                _surface.DragThreshold.Vertical))
        {
            _panMoved = true;
        }

        if (_pressCanPan)
        {
            var offsets = MainWindowHelpers.CalculatePanOffsets(
                _surface.HorizontalOffset,
                _surface.VerticalOffset,
                deltaX,
                deltaY,
                _surface.ExtentWidth,
                _surface.ExtentHeight,
                _surface.ViewportWidth,
                _surface.ViewportHeight);
            _surface.ScrollTo(offsets.Horizontal, offsets.Vertical);
        }
        return _panMoved;
    }

    /// <summary>MainImage PreviewMouseLeftButtonUp; returns the value for e.Handled.</summary>
    public bool OnImageRelease(Point position, int timestamp)
    {
        if (!_isPanning)
        {
            CancelPan();
            return false;
        }
        var handled = false;
        var point = position;
        _panVelocity.Add(unchecked(timestamp - _pressTimestamp), point.X, point.Y);
        var settings = _settings();
        var action = PointerGestures.ClassifyRelease(
            dragged: _panMoved,
            panned: _pressCanPan,
            pressWasConsumed: _pressConsumed,
            clickToZoomEnabled: settings.ClickToZoomEnabled,
            kineticPanEnabled: settings.KineticPanEnabled);
        var moved = _panMoved;
        CancelPan();

        switch (action)
        {
            case PointerReleaseAction.ClickZoom:
                ClickZoomAsync(point).FireAndLog("Click-to-zoom failed");
                handled = true;
                break;
            case PointerReleaseAction.StartKinetic:
                var (velocityX, velocityY) = _panVelocity.GetVelocity();
                StartKinetic(velocityX, velocityY);
                break;
        }
        if (moved) handled = true;
        return handled;
    }

    /// <summary>Click-to-zoom: Fit or another zoom -> ClickZoomPercent at the cursor; at ClickZoomPercent -> Fit.</summary>
    private Task ClickZoomAsync(Point mouse)
    {
        var viewer = _viewer;
        var target = PointerGestures.ClickZoomFactor(_settings().ClickZoomPercent);
        return PointerGestures.DecideClickZoom(viewer.IsFit, viewer.Zoom, target) == ClickZoomTarget.Fit
            ? _commands.ApplyFitAsync()
            : ZoomAtPointAsync(mouse, () => viewer.SetZoom(target));
    }

    /// <summary>
    /// ClickZoom shortcut: zooms to ClickZoomPercent like a mouse click-to-zoom (<see cref="ClickZoomAsync"/>), anchored
    /// like <see cref="ZoomActualSizeAsync"/> (the cursor over the viewport with <see cref="KeyboardZoomAnchor.Pointer"/>,
    /// the viewport centre otherwise) instead of always the cursor, and independent of <c>ClickToZoomEnabled</c> (which
    /// only governs the mouse click). Already at ClickZoomPercent it does nothing, unless
    /// <see cref="AppSettings.ClickZoomKeyTogglesFit"/> restores the mouse's Fit toggle.
    /// </summary>
    public Task ToggleClickZoomAsync()
    {
        if (!_commands.HasImages()) return Task.CompletedTask;
        CancelPan();
        StopKinetic();
        var settings = _settings();
        if (!settings.ClickZoomKeyTogglesFit && PointerGestures.DecideClickZoom(
                _viewer.IsFit, _viewer.Zoom, PointerGestures.ClickZoomFactor(settings.ClickZoomPercent)) == ClickZoomTarget.Fit)
            return Task.CompletedTask; // already there: the key means "go to the click zoom", never "back to Fit"
        return ClickZoomAsync(ResolveKeyboardAnchor());
    }

    /// <summary>
    /// Context menu "Click zoom level": zooms straight to <paramref name="percent"/> (no Fit toggle, unlike
    /// <see cref="ToggleClickZoomAsync"/>), anchored at the viewport centre.
    /// </summary>
    public Task SetClickZoomLevelAsync(int percent)
    {
        if (!_commands.HasImages()) return Task.CompletedTask;
        CancelPan();
        StopKinetic();
        var centre = new Point(_surface.ViewportWidth / 2, _surface.ViewportHeight / 2);
        var target = PointerGestures.ClickZoomFactor(percent);
        return ZoomAtPointAsync(centre, () => _viewer.SetZoom(target));
    }

    /// <summary>MainImage LostMouseCapture.</summary>
    public void OnLostCapture() => CancelPan();

    private bool CanPan() => !_viewer.IsFit &&
        (_surface.ExtentWidth > _surface.ViewportWidth + 0.5 || _surface.ExtentHeight > _surface.ViewportHeight + 0.5);

    public void CancelPan()
    {
        _isPanning = false;
        _panMoved = false;
        _pressCanPan = false;
        _pressConsumed = false;
        _surface.ReleaseMouseCapture();
        _surface.SetPanCursor(false);
    }

    // ---- kinetic glide: the render-frame callback on the UI thread, hooked only while gliding ----

    private void StartKinetic(double pointerVelocityX, double pointerVelocityY)
    {
        if (!_kinetic.Start(pointerVelocityX, pointerVelocityY)) return;
        _glideClock.Start(_settings().KineticGlideSmoothing, TicksPerMs);
        if (_kineticHooked) return;
        _surface.HookRenderFrame(_kineticFrameHandler);
        _kineticHooked = true;
    }

    /// <summary>
    /// feat/zoom-key-anchor: adds a SCROLL-offset velocity impulse (<see cref="KineticScroller.AddImpulse"/>) to the
    /// glide, starting one if idle; an auto-repeating key therefore keeps adding to the existing velocity (capped at
    /// <see cref="KineticScroller.MaxVelocity"/>) instead of restarting the glide from scratch. The glide clock is
    /// (re)started only for a fresh glide -- an impulse added mid-glide keeps its existing timing/anchoring.
    /// </summary>
    private void StartKineticImpulse(double velocityX, double velocityY)
    {
        var wasActive = _kinetic.IsActive;
        _kinetic.AddImpulse(velocityX, velocityY);
        if (!wasActive) _glideClock.Start(_settings().KineticGlideSmoothing, TicksPerMs);
        if (_kineticHooked) return;
        _surface.HookRenderFrame(_kineticFrameHandler);
        _kineticHooked = true;
    }

    /// <summary>
    /// Arrow keys on a zoomed image move the view by <see cref="KeyboardPan.StepFraction"/> of the viewport instead
    /// of navigating. Returns true when the key was used. By default (ArrowKeyNavigatesAtZoomEdge off) every arrow key is
    /// used while the image is zoomed (pan, or nothing at an edge / on a non-scrollable axis); false only at Fit, so
    /// Left/Right navigate. With the setting on, a fresh press at the edge (or on a non-scrollable axis) is not used
    /// and navigates; an auto-repeat at the edge is swallowed. See <see cref="KeyboardPan.ConsumesKey"/>.
    /// </summary>
    /// <remarks>
    /// With <see cref="Core.Settings.AppSettings.KineticPanEnabled"/> a pan starts (or adds to) a kinetic glide with
    /// the same friction as a mouse flick (<see cref="StartKineticImpulse"/>) instead of jumping straight to the
    /// target offset, so holding the key accelerates smoothly and releasing it lets the glide decelerate on its own.
    /// The edge/consume decision still reads the CURRENT (already-scrolled) offset via <see cref="KeyboardPan.Step"/>;
    /// only the instant <c>ScrollTo</c> is skipped in kinetic mode.
    /// </remarks>
    public bool TryPanByArrow(Key key, bool isRepeat)
    {
        var (dx, dy) = key switch
        {
            Key.Left => (-1, 0),
            Key.Right => (1, 0),
            Key.Up => (0, -1),
            Key.Down => (0, 1),
            _ => (0, 0),
        };
        if ((dx, dy) == (0, 0) || !_surface.IsLoaded || !_commands.HasImages()) return false;
        var bounds = new ScrollBounds(_surface.ExtentWidth, _surface.ExtentHeight, _surface.ViewportWidth, _surface.ViewportHeight);
        var (result, horizontal, vertical) = KeyboardPan.Step(dx, dy, _surface.HorizontalOffset, _surface.VerticalOffset, bounds,
            _settings().ArrowPanStepPercent / 100.0);
        switch (result)
        {
            case KeyboardPanResult.Panned:
                if (_settings().KineticPanEnabled)
                {
                    var (velocityX, velocityY) = KeyboardPan.ImpulseVelocity(horizontal - _surface.HorizontalOffset, vertical - _surface.VerticalOffset);
                    StartKineticImpulse(velocityX, velocityY);
                }
                else
                {
                    StopKinetic();
                    _surface.ScrollTo(horizontal, vertical);
                }
                return true;
            default:
                return KeyboardPan.ConsumesKey(result, isRepeat, _settings().ArrowKeyNavigatesAtZoomEdge, bounds);
        }
    }

    /// <summary>Stops a running glide at once; returns true if one was running.</summary>
    public bool StopKinetic()
    {
        var wasActive = _kinetic.IsActive;
        _kinetic.Stop();
        if (_kineticHooked)
        {
            _surface.UnhookRenderFrame(_kineticFrameHandler);
            _kineticHooked = false;
        }
        return wasActive;
    }

    private void OnKineticFrame(object? sender, EventArgs e)
    {
        if (!_kinetic.IsActive || !_surface.IsLoaded)
        {
            StopKinetic();
            return;
        }
        if (_surface.RenderingTime(e) is not { } renderingTime) return;
        // GlideFrameClock: 0 = no step (first frame, a repeated RenderingTime, or inside the current vblank/cadence slot).
        var elapsed = _glideClock.Advance(
            renderingTime.TotalMilliseconds,
            _surface.Timestamp,
            _glideClock.NeedsDisplayTiming ? _surface.DisplayTiming : null);
        if (elapsed <= 0) return;

        var (horizontal, vertical) = _kinetic.Step(
            elapsed,
            _surface.HorizontalOffset,
            _surface.VerticalOffset,
            new ScrollBounds(_surface.ExtentWidth, _surface.ExtentHeight, _surface.ViewportWidth, _surface.ViewportHeight));
        _surface.ScrollTo(horizontal, vertical);
        if (!_kinetic.IsActive) StopKinetic();
    }

    /// <summary>Window closed: unhooks the render-frame callback (a static event that would keep the window alive) and ends any pan.</summary>
    public void OnWindowClosed()
    {
        StopKinetic();
        CancelPan();
    }
}
