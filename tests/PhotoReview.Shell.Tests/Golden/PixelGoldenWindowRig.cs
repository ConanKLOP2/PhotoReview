using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Overlay;
using PhotoReview.Shell.Tests.Rendering;
using PhotoReview.Shell.Tests.Viewing;
using PhotoReview.Shell.Win32.Viewing;
using PhotoReview.TestSupport.PixelGolden;

namespace PhotoReview.Shell.Tests.Golden;

/// <summary>
/// WP-28b: one shell "window" that lives across frames - the real <see cref="ViewportController"/> over a <see cref="ViewerState"/>
/// (<see cref="ScrollBarRig"/>), ONE D2D WARP offscreen surface that is <c>Resize</c>d (not recreated) like the window surface, ONE
/// <see cref="GpuImageCache"/> and ONE <see cref="PresentationComposer"/>. <see cref="Transition"/> applies what the window does
/// when F11 or a DPI change gives it a new client rectangle (client DIP size + DPI scale -> <c>ViewerState.DpiScale</c>, viewport
/// layout invalidation, surface resize, as <c>ShellMainWindow.OnClientSizeChanged/OnDpiChanged/Present</c> do), so
/// <see cref="Frame"/> afterwards is the FIRST frame drawn after the change.
/// </summary>
internal sealed class PixelGoldenWindowRig : IDisposable
{
    private readonly PixelGoldenCase _start;
    private readonly ScrollBarRig _rig;
    private PixelBuffer _picture;
    private readonly ManualDispatcher _dispatcher = new();
    private readonly D2DRenderSurface _surface;
    private readonly GpuImageCache _cache;
    private readonly PresentationComposer _composer;
    private long _nowMs;
    private ComposedFrame _lastComposed;
    private double _dpi;
    private SizeD _client;

    internal PixelGoldenWindowRig(PixelGoldenCase start)
    {
        _start = start;
        _dpi = start.Dpi;
        _client = new SizeD(start.ClientWidth, start.ClientHeight);
        _rig = new ScrollBarRig(start.ClientWidth, start.ClientHeight, start.OrientedWidth, start.OrientedHeight, start.Dpi,
            actualSize: false, bitmapWidth: start.BitmapWidth, bitmapHeight: start.BitmapHeight);
        _rig.Viewer.ScalingQuality = start.LinearScaling ? ScalingQuality.Linear : ScalingQuality.HighQuality;
        if (!start.IsFit)
        {
            _rig.Viewer.SetZoom(start.ZoomPercent / 100.0);
            _rig.Drain();
            _rig.Viewport.UpdateLayout();
            var layout = _rig.Viewport.Layout;
            _rig.Viewport.ScrollTo(
                PixelGoldenCatalog.OffsetFromFraction(start.OffsetX, layout.MaxHorizontalOffset),
                PixelGoldenCatalog.OffsetFromFraction(start.OffsetY, layout.MaxVerticalOffset));
            _rig.Drain();
        }

        _picture = PixelGoldenShellRenderer.ToPixelBuffer(PixelGoldenScenes.Create(start.Scene, start.ShownWidth, start.ShownHeight));
        _surface = D2DRenderSurface.CreateOffscreenSurface(start.PixelWidth, start.PixelHeight, start.Dpi);
        _cache = new GpuImageCache(_surface, _dispatcher);
        _composer = new PresentationComposer(_cache, _dispatcher, new FakeFrameClock(), () => { }, ticksPerSecond: 1000, timestamp: () => _nowMs);
        _composer.Set(_picture);
    }

    internal ViewerState Viewer => _rig.Viewer;

    internal ViewportController Viewport => _rig.Viewport;

    /// <summary>Surface size in device pixels (what <c>Present</c> compares with the client size).</summary>
    internal (int Width, int Height) SurfacePixels => (_surface.PixelWidth, _surface.PixelHeight);

    internal double SurfaceDpi => _surface.DpiScale;

    /// <summary>
    /// The window gets a new client rectangle (DIP) on a monitor of scale <paramref name="dpi"/>. <paramref name="fullscreen"/> sets
    /// <c>ViewerState.IsFullscreen</c> (WP-22 sets the flag; WP-25 applies it to the HWND, which is what changes the client).
    /// </summary>
    internal void Transition(double clientWidthDip, double clientHeightDip, double dpi, bool? fullscreen = null)
    {
        if (fullscreen is { } fs)
        {
            _rig.Viewer.IsFullscreen = fs;
        }

        _dpi = dpi;
        _client = new SizeD(clientWidthDip, clientHeightDip);
        _rig.Window.DpiScale = dpi;
        _rig.Viewer.DpiScale = dpi; // ShellMainWindow.UpdateTargetDecodeBox (OnDpiChanged)
        _rig.Window.Resize(clientWidthDip, clientHeightDip); // ClientSizeChanged -> InvalidateLayout
        _rig.Viewport.InvalidateLayout();
        _rig.Drain();
    }

    /// <summary>What the composer chose for the last <see cref="Frame"/> (interpolation, and whether it was degraded to Linear while moving).</summary>
    internal ComposedFrame LastComposed => _lastComposed;

    /// <summary>The picture sits still for longer than <c>ScalingPolicy.SettleMs</c>: the next frame is the settled (high quality) one.</summary>
    internal void Settle() => _nowMs += ScalingPolicy.SettleMs + 1;

    /// <summary>The picture of the same scene is replaced by a bitmap of another pixel size (RAW: embedded preview -&gt; full decode).</summary>
    internal void ReplaceBitmap(int width, int height)
    {
        var next = PixelGoldenShellRenderer.ToPixelBuffer(PixelGoldenScenes.Create(_start.Scene, width, height));
        _composer.Set(next);
        _picture.Dispose();
        _picture = next;
        _rig.SetBitmap(width, height);
    }

    /// <summary>The next frame, drawn the way <c>ShellMainWindow.Present</c> draws it (surface <c>Resize</c> first, layout, then draw).</summary>
    internal ShellFrame Frame()
    {
        var width = (int)Math.Round(_client.Width * _dpi);
        var height = (int)Math.Round(_client.Height * _dpi);
        if (_surface.PixelWidth != width || _surface.PixelHeight != height || _surface.DpiScale != _dpi)
        {
            _surface.Resize(width, height, _dpi);
        }

        _rig.Viewport.UpdateLayout();
        var origin = _rig.Viewport.ImageOrigin;
        var destination = new RectD(origin.X, origin.Y, _rig.Viewport.ImageActualWidth, _rig.Viewport.ImageActualHeight);
        var viewLayout = _rig.Viewport.Layout;
        var inputs = new PresentationInputs(_client, destination, _rig.Viewer.EffectiveScalingQuality, _rig.Viewer.ScalingQuality, _dpi,
            false, default);

        using var pixels = RenderTestImages.Render(_surface, dc =>
        {
            dc.Clear(RenderColors.DarkCanvas);
            _lastComposed = _composer.Draw(dc, inputs);
            Assert.True(_lastComposed.MainDrawn);
            _rig.Bars.Draw(dc);
        });

        var viewport = new FrameRect(0, 0, (int)Math.Round(viewLayout.ViewportWidth * _dpi), (int)Math.Round(viewLayout.ViewportHeight * _dpi));
        var left = (int)Math.Round(destination.X * _dpi);
        var top = (int)Math.Round(destination.Y * _dpi);
        var right = (int)Math.Round(destination.Right * _dpi);
        var bottom = (int)Math.Round(destination.Bottom * _dpi);
        return new ShellFrame(PixelGoldenShellRenderer.ToFrame(pixels), viewport, new FrameRect(left, top, right - left, bottom - top));
    }

    public void Dispose()
    {
        _composer.Dispose();
        _cache.Dispose();
        _surface.Dispose();
        _picture.Dispose();
        _rig.Dispose();
    }
}
