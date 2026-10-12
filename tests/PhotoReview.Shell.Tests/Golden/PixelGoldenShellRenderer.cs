using PhotoReview.App.Input;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Overlay;
using PhotoReview.Shell.Tests.Rendering;
using PhotoReview.Shell.Tests.Viewing;
using PhotoReview.Shell.Win32.Overlay;
using PhotoReview.Shell.Win32.Viewing;
using PhotoReview.TestSupport.PixelGolden;

namespace PhotoReview.Shell.Tests.Golden;

/// <summary>How the picture reaches the renderer in a shell frame.</summary>
internal enum ShellPictureRoute
{
    /// <summary>As the app does: the decoder has applied EXIF (<c>PixelOps.ApplyOrientation</c>), the composer draws the upright buffer.</summary>
    Decoded,

    /// <summary>The raw buffer drawn by the renderer's orientation matrix (<c>ImageLayer.TryDraw</c> exifOrientation).</summary>
    RawWithMatrix,
}

/// <summary>A shell frame and the layout it was drawn with (device pixels).</summary>
/// <param name="Frame">The pixels read back from the WARP offscreen surface.</param>
/// <param name="Viewport">Viewport (client minus the scroll bars) in pixels.</param>
/// <param name="Image">The picture's box (unclipped, rounded to whole pixels).</param>
internal sealed record ShellFrame(PixelFrame Frame, FrameRect Viewport, FrameRect Image);

/// <summary>
/// WP-28: renders a G-PIX case the way the shell renders a frame - the real <see cref="ViewportController"/> layout over a
/// <see cref="ViewerState"/> (<see cref="ScrollBarRig"/>), the real <see cref="PresentationComposer"/>/<c>ImageLayer</c> on the D2D
/// WARP offscreen surface, then the real <see cref="ShellScrollBars"/> and (HUD cases) the real <see cref="ShellOverlay"/> - and
/// reads the pixels back. Nothing depends on the monitor of the machine: client size and DPI come from the case.
/// </summary>
internal static class PixelGoldenShellRenderer
{
    private static readonly ColorF Canvas = RenderColors.DarkCanvas;

    internal static ShellFrame Render(PixelGoldenCase c, ShellPictureRoute route = ShellPictureRoute.Decoded,
        ITextRenderer? hudText = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        var oriented = c.OrientedWidth;
        var orientedHeight = c.OrientedHeight;
        using var rig = new ScrollBarRig(c.ClientWidth, c.ClientHeight, oriented, orientedHeight, c.Dpi, actualSize: false);
        rig.Viewer.ScalingQuality = c.LinearScaling ? ScalingQuality.Linear : ScalingQuality.HighQuality;
        if (!c.IsFit)
        {
            rig.Viewer.SetZoom(c.ZoomPercent / 100.0);
            rig.Drain();
            rig.Viewport.UpdateLayout();
            var layout = rig.Viewport.Layout;
            rig.Viewport.ScrollTo(
                PixelGoldenCatalog.OffsetFromFraction(c.OffsetX, layout.MaxHorizontalOffset),
                PixelGoldenCatalog.OffsetFromFraction(c.OffsetY, layout.MaxVerticalOffset));
            rig.Drain();
        }

        rig.Viewport.UpdateLayout();
        var origin = rig.Viewport.ImageOrigin;
        var destination = new RectD(origin.X, origin.Y, rig.Viewport.ImageActualWidth, rig.Viewport.ImageActualHeight);
        var viewLayout = rig.Viewport.Layout;

        using var raw = ToPixelBuffer(PixelGoldenScenes.Create(c.Scene, c.ImageWidth, c.ImageHeight));
        using var upright = route == ShellPictureRoute.Decoded && c.ExifOrientation != 1
            ? PixelOps.ApplyOrientation(raw, c.ExifOrientation)
            : null;
        var picture = upright ?? raw;

        using var surface = D2DRenderSurface.CreateOffscreenSurface(c.PixelWidth, c.PixelHeight, c.Dpi);
        var dispatcher = new ManualDispatcher();
        using var cache = new GpuImageCache(surface, dispatcher);
        using var composer = new PresentationComposer(cache, dispatcher, new FakeFrameClock(), () => { }, ticksPerSecond: 1000, timestamp: () => 0);
        var client = new SizeD(c.ClientWidth, c.ClientHeight);
        var inputs = new PresentationInputs(client, destination, rig.Viewer.EffectiveScalingQuality, rig.Viewer.ScalingQuality, c.Dpi,
            false, default);
        composer.Set(picture);

        using var pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(Canvas);
            if (route == ShellPictureRoute.RawWithMatrix)
            {
                dc.PushClip(new RectD(0, 0, client.Width, client.Height));
                try
                {
                    var interpolation = ImageInterpolations.FromScalingQuality(inputs.EffectiveQuality);
                    Assert.True(ImageLayer.TryDraw(cache, dc, raw, destination, interpolation, 1f, c.ExifOrientation));
                }
                finally
                {
                    dc.PopClip();
                }
            }
            else
            {
                Assert.True(composer.Draw(dc, inputs).MainDrawn);
            }

            rig.Bars.Draw(dc);
            if (c.Hud)
            {
                DrawHud(dc, client, c, hudText ?? throw new ArgumentNullException(nameof(hudText), "HUD cases need a text renderer."));
            }
        });

        var dpi = c.Dpi;
        var viewport = new FrameRect(0, 0, (int)Math.Round(viewLayout.ViewportWidth * dpi), (int)Math.Round(viewLayout.ViewportHeight * dpi));
        var left = (int)Math.Round(destination.X * dpi);
        var top = (int)Math.Round(destination.Y * dpi);
        var right = (int)Math.Round(destination.Right * dpi);
        var bottom = (int)Math.Round(destination.Bottom * dpi);
        return new ShellFrame(ToFrame(pixels), viewport, new FrameRect(left, top, right - left, bottom - top));
    }

    private static void DrawHud(IDrawContext dc, SizeD client, PixelGoldenCase c, ITextRenderer text)
    {
        using var overlay = new ShellOverlay(new ShellOverlayOptions
        {
            Text = text,
            Clock = new FakeFrameClock(),
            Settings = () => new AppSettings(),
            Commands = new FakeOverlayCommands(),
            RequestRender = static () => { },
            IsWindowActive = static () => true,
        });
        overlay.Update(new OverlayModel
        {
            HasImages = true,
            StatusPanelVisible = true,
            FileInfoVisible = true,
            StatusText = PixelGoldenCatalog.StatusLine,
            FontSize = PixelGoldenCatalog.HudFontSize,
            ExifFontSize = PixelGoldenCatalog.HudFontSize - 1,
            ExifVisible = true,
            ExifText = PixelGoldenCatalog.ExifLine,
            FolderInfoVisible = true,
            FolderInfoText = PixelGoldenCatalog.FolderLine,
            ZoomIndicatorVisible = true,
            ZoomIndicatorText = PixelGoldenCatalog.ZoomLine,
            CapturePairVisible = true,
            CapturePairText = PixelGoldenCatalog.CapturePairLine,
        });
        overlay.Render(dc, client, c.Dpi);
    }

    /// <summary>Opaque BGRA frame -&gt; Pbgra32 buffer (premultiplication is the identity at alpha 255).</summary>
    internal static PixelBuffer ToPixelBuffer(PixelFrame frame)
    {
        var buffer = PixelBuffer.Allocate(frame.Width, frame.Height, PixelLayout.Pbgra32);
        for (var y = 0; y < frame.Height; y++)
        {
            frame.Bgra.AsSpan(y * frame.Width * 4, frame.Width * 4).CopyTo(buffer.GetRow(y));
        }

        return buffer;
    }

    internal static PixelFrame ToFrame(PixelBuffer buffer)
    {
        var data = new byte[buffer.Width * buffer.Height * 4];
        for (var y = 0; y < buffer.Height; y++)
        {
            buffer.GetRow(y).Slice(0, buffer.Width * 4).CopyTo(data.AsSpan(y * buffer.Width * 4));
        }

        for (var i = 3; i < data.Length; i += 4)
        {
            data[i] = 255;
        }

        return new PixelFrame(buffer.Width, buffer.Height, data);
    }
}
