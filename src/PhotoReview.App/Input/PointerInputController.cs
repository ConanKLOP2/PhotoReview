using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;

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

    /// <summary>ZoomActualSize: 100 % (ADR 0008) keeping the image point at the viewport centre in place.</summary>
    public async Task ZoomActualSizeAsync()
    {
        if (!_commands.HasImages()) return;
        CancelPan();
        StopKinetic();
        var centre = new Point(_surface.ViewportWidth / 2, _surface.ViewportHeight / 2);
        await ZoomAtPointAsync(centre, _commands.ZoomActualSize);
    }

    /// <summary>
    /// Applies a zoom change and then scrolls so the image point that was under <paramref name="mouse"/>
    /// (ImageScroll coordinates) stays under it. Shared by the wheel and click-to-zoom.
    /// </summary>
    private async Task ZoomAtPointAsync(Point mouse, Action applyZoom)
    {
        var anchor = CaptureZoomAnchor(mouse);
        var version = _viewportVersion.Next();
        applyZoom();
        await _surface.YieldToRenderAsync();
        if (version != _viewportVersion.Current || !_surface.IsLoaded) return;
        _surface.UpdateLayout();

        var imageOrigin = _surface.ImageOrigin;
        var offsets = MainWindowHelpers.CalculateZoomToPointOffsets(
            anchor,
            imageOrigin.X,
            imageOrigin.Y,
            _surface.ImageActualWidth,
            _surface.ImageActualHeight,
            mouse.X,
            mouse.Y,
            _surface.HorizontalOffset,
            _surface.VerticalOffset,
            _surface.ExtentWidth,
            _surface.ExtentHeight,
            _surface.ViewportWidth,
            _surface.ViewportHeight);
        _surface.ScrollTo(offsets.Horizontal, offsets.Vertical);
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
            _ = _commands.ApplyFitAsync();
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
                _ = ClickZoomAsync(point);
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
