using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.TestSupport;
using PhotoReview.TestSupport.PixelGolden;

namespace PhotoReview.Integration.Tests.Golden;

/// <summary>
/// WP-28 (G-PIX): renders the real WPF <c>MainWindow</c> content (never shown, laid out at the case's client size and DPI, the
/// production <c>ScrollViewer</c> + <c>Image</c> + dark scroll bar template + HUD panels) into a <see cref="RenderTargetBitmap"/>
/// and returns its pixels. The picture is the case's procedural scene, put on the <c>Image</c> as a real 96 dpi bitmap (EXIF
/// orientation applied by WPF's own <see cref="TransformedBitmap"/>, an oracle independent of <c>PixelOps</c>); zoom, Fit and the
/// scroll offset are driven through the same <c>ViewerState</c>/<c>ScrollViewer</c> calls as the layout golden (WP-10).
/// </summary>
internal static class PixelGoldenRecorder
{
    /// <summary>Every frame that has a golden PNG: the catalog plus the HUD-off twin of each HUD case.</summary>
    internal static IReadOnlyList<PixelGoldenCase> RecordedCases { get; } =
        [.. PixelGoldenCatalog.All, .. PixelGoldenCatalog.All.Where(c => c.Hud).Select(PixelGoldenCatalog.WithoutHud)];

    internal static string PngPath(PixelGoldenCase c) => PixelGoldenFiles.PathOf(c.Name);

    /// <summary>Renders every case on fresh windows (one STA body), keyed by case name.</summary>
    internal static async Task<IReadOnlyDictionary<string, PixelFrame>> RenderAllAsync(IReadOnlyList<PixelGoldenCase> cases)
    {
        var frames = new Dictionary<string, PixelFrame>(StringComparer.Ordinal);
        using var vietnamese = TestLocalization.Use(TestLocalization.Vietnamese); // toolbar strings, as the overlay golden
        await GoldenWpfHost.RunEachAsync(cases, async (view, c) => frames[c.Name] = await RenderAsync(view, c));
        return frames;
    }

    internal static async Task<PixelFrame> RenderAsync(GoldenWpfView view, PixelGoldenCase c)
    {
        await PrepareAsync(view, c);
        return Capture(view, c);
    }

    /// <summary>Everything of <see cref="RenderAsync"/> except the capture (the compare recorder lays its panel over this state).</summary>
    internal static async Task PrepareAsync(GoldenWpfView view, PixelGoldenCase c)
    {
        view.ApplySettings(c.LinearScaling ? "{\"scalingQuality\":\"Linear\"}" : "{}");
        view.SetClient(c.ClientWidth, c.ClientHeight);
        view.SetDpi(c.Dpi);

        // The picture: tiny placeholder first (as every golden), then the real bitmap on the same Image element.
        await view.ShowImageAsync(c.OrientedWidth, c.OrientedHeight);
        view.Image.Source = CreateBitmap(c);
        view.Relayout();

        if (c.IsFit)
        {
            await view.ApplyFitAsync();
        }
        else
        {
            view.Viewer.SetZoom(c.ZoomPercent / 100.0);
            view.Relayout();
            await view.SettleAsync();
            var scroll = view.Scroll;
            scroll.ScrollToHorizontalOffset(PixelGoldenCatalog.OffsetFromFraction(c.OffsetX, scroll.ScrollableWidth));
            scroll.ScrollToVerticalOffset(PixelGoldenCatalog.OffsetFromFraction(c.OffsetY, scroll.ScrollableHeight));
            view.Relayout();
            await view.SettleAsync();
        }

        ArrangeHud(view, c);
    }

    internal static BitmapSource CreateBitmap(PixelGoldenCase c)
    {
        // WP-28b: a RAW preview is a smaller bitmap than the declared size (ShownWidth/ShownHeight); the layout is the original's.
        var scene = PixelGoldenScenes.Create(c.Scene, c.ShownWidth, c.ShownHeight);
        var raw = BitmapSource.Create(scene.Width, scene.Height, 96, 96, PixelFormats.Pbgra32, null, scene.Bgra, scene.Width * 4);
        raw.Freeze();
        if (c.ExifOrientation == 1)
        {
            return raw;
        }

        var transformed = new TransformedBitmap(raw, TransformFor(c.ExifOrientation));
        var width = transformed.PixelWidth;
        var height = transformed.PixelHeight;
        var bytes = new byte[width * height * 4];
        transformed.CopyPixels(bytes, width * 4, 0);
        var upright = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, bytes, width * 4);
        upright.Freeze();
        return upright;
    }

    // EXIF orientation -> the transform that makes the stored picture upright (TIFF/EXIF tag 0x0112).
    private static Transform TransformFor(int exif) => exif switch
    {
        2 => new ScaleTransform(-1, 1),
        3 => new RotateTransform(180),
        4 => new ScaleTransform(1, -1),
        5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
        6 => new RotateTransform(90),
        7 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(1, -1) } },
        8 => new RotateTransform(270),
        _ => throw new ArgumentOutOfRangeException(nameof(exif), exif, null),
    };

    private static void ArrangeHud(GoldenWpfView view, PixelGoldenCase c)
    {
        var window = view.Window;
        var panels = new Border[]
        {
            window.ToolbarPanel, window.StatusPanel, window.FolderInfoPanel, window.ZoomIndicatorPanel, window.CapturePairBadge,
        };
        if (!c.Hud)
        {
            foreach (var panel in panels)
            {
                panel.Visibility = Visibility.Collapsed;
            }

            view.Relayout();
            return;
        }

        window.Settings.InfoOverlayFontSize = PixelGoldenCatalog.HudFontSize;
        window.ViewModel.InfoOverlay.Refresh();
        window.StatusText.Text = PixelGoldenCatalog.StatusLine;
        window.ExifInfoText.Text = PixelGoldenCatalog.ExifLine;
        window.FolderInfoText.Text = PixelGoldenCatalog.FolderLine;
        window.ZoomIndicatorText.Text = PixelGoldenCatalog.ZoomLine;
        ((TextBlock)window.CapturePairBadge.Child).Text = PixelGoldenCatalog.CapturePairLine;
        window.ExifInfoText.Visibility = Visibility.Visible;
        foreach (var panel in panels)
        {
            panel.Visibility = Visibility.Visible;
        }

        view.Relayout();
    }

    internal static PixelFrame Capture(GoldenWpfView view, PixelGoldenCase c)
    {
        var root = (FrameworkElement)view.Window.Content;
        var width = c.PixelWidth;
        var height = c.PixelHeight;
        var target = new RenderTargetBitmap(width, height, 96 * c.Dpi, 96 * c.Dpi, PixelFormats.Pbgra32);
        // The brush round trip renders the element at the origin of the target whatever its offset in the window.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, c.ClientWidth, c.ClientHeight));
        }

        target.Render(visual);
        var bytes = new byte[width * height * 4];
        target.CopyPixels(bytes, width * 4, 0);
        for (var i = 3; i < bytes.Length; i += 4)
        {
            bytes[i] = 255;
        }

        return new PixelFrame(width, height, bytes);
    }
}
