using System.Globalization;
using System.Windows;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Model;
using PhotoReview.TestSupport.Golden;

namespace PhotoReview.Integration.Tests.Golden;

/// <summary>
/// WP-10 G-VIEW (<c>viewport-layout.v1.json</c>): drives the REAL WPF <c>ScrollViewer</c> + <c>Image</c> of the main window through the
/// same view-model state (<see cref="ViewerState"/>) and the same controllers the app uses, and records the resulting layout. The
/// Win32 engine (<c>ViewportLayoutEngine</c>, WP-16) must reproduce every <see cref="GoldenViewportCase.Expected"/> from its
/// <see cref="GoldenViewportCase.Input"/> within 0.5 DIP.
/// </summary>
internal static class ViewportGoldenRecorder
{
    internal static readonly (int Width, int Height)[] Clients = [(800, 600), (1280, 720), (1920, 1080), (1366, 728), (3840, 2160)];
    internal static readonly double[] DpiScales = [1.0, 1.25, 1.5, 2.0];
    internal static readonly (int Width, int Height)[] Images = [(6000, 4000), (4000, 6000), (800, 600), (12000, 1000), (1000, 12000)];
    internal static readonly int[] ZoomPercents = [25, 50, 75, 100, 150, 200, 300, 400];

    internal const string Notes =
        "Recorded from the WPF MainWindow (ImageScroll ScrollViewer HorizontalScrollBarVisibility=Auto/VerticalScrollBarVisibility=Auto, " +
        "Image Stretch bound to ViewerState). Client = ImageScroll size in DIP (window content area, no border). Image bitmap natural size " +
        "= source pixels at 96 dpi unless the mode is fit-preview (bitmap = the downscaled preview, 1.15 x the device-pixel box). " +
        "ImageX/ImageY are in extent coordinates (TranslatePoint of the Image + scroll offset). ScrollBarThickness is measured " +
        "(clientWidth - ViewportWidth with a vertical bar): the dark theme ScrollBar is 10 DIP wide, not SystemParameters. " +
        "Modes: fit (FitViewController convergence), fitwidth/fitheight (PointerInputController.FitWidthAsync/FitHeightAsync incl. the " +
        "side-scrollbar correction pass, Centre anchor), z{N} (ViewerState.SetZoom(N/100), scroll offset 0). DPI = ViewerState.DpiScale " +
        "with the root visual DPI set to the same value. Name = mode|image|client|dpi.";

    public static async Task<IReadOnlyList<GoldenViewportCase>> RecordViewportAsync()
    {
        var cases = new List<GoldenViewportCase>();
        await GoldenWpfHost.RunAsync(async view =>
        {
            view.ApplySettings("{}");
            var thickness = await MeasureScrollBarThicknessAsync(view);
            foreach (var (clientWidth, clientHeight) in Clients)
            {
                foreach (var dpi in DpiScales)
                {
                    view.SetClient(clientWidth, clientHeight);
                    view.SetDpi(dpi);
                    foreach (var (imageWidth, imageHeight) in Images)
                    {
                        await RecordImageAsync(view, cases, thickness, imageWidth, imageHeight);
                        await RecordFitPreviewAsync(view, cases, thickness, imageWidth, imageHeight);
                    }
                }
            }
        });
        return cases;
    }

    private static async Task<double> MeasureScrollBarThicknessAsync(GoldenWpfView view)
    {
        view.SetClient(800, 600);
        view.SetDpi(1.0);
        await view.ShowImageAsync(800, 600);
        view.Viewer.SetZoom(4.0);
        view.Relayout();
        await view.SettleAsync();
        if (view.Scroll.ComputedVerticalScrollBarVisibility != Visibility.Visible)
            throw new InvalidOperationException("Calibration expected a vertical scroll bar at 400 %.");
        return view.Client.Width - view.Scroll.ViewportWidth;
    }

    private static string CaseName(string mode, int imageWidth, int imageHeight, GoldenWpfView view) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{mode}|{imageWidth}x{imageHeight}|{view.Client.Width:0}x{view.Client.Height:0}|dpi{view.DpiScale:0.##}");

    private static async Task RecordImageAsync(GoldenWpfView view, List<GoldenViewportCase> cases, double thickness, int imageWidth, int imageHeight)
    {
        await view.ShowImageAsync(imageWidth, imageHeight);
        await view.ApplyFitAsync();
        cases.Add(Capture("fit", view, thickness, imageWidth, imageHeight));

        // Fit width / Fit height from Fit, through the real controller (including the scroll bar correction pass).
        await view.Pointer.FitWidthAsync(FitWidthAnchor.Centre);
        await view.SettleAsync();
        cases.Add(Capture("fitwidth", view, thickness, imageWidth, imageHeight));
        await view.ApplyFitAsync();
        await view.Pointer.FitHeightAsync();
        await view.SettleAsync();
        cases.Add(Capture("fitheight", view, thickness, imageWidth, imageHeight));

        foreach (var percent in ZoomPercents)
        {
            await view.ApplyFitAsync();
            view.Viewer.SetZoom(percent / 100.0);
            view.Relayout();
            await view.SettleAsync();
            view.Scroll.ScrollToHorizontalOffset(0);
            view.Scroll.ScrollToVerticalOffset(0);
            view.Relayout();
            cases.Add(Capture(string.Create(CultureInfo.InvariantCulture, $"z{percent}"), view, thickness, imageWidth, imageHeight));
        }
    }

    private static async Task RecordFitPreviewAsync(GoldenWpfView view, List<GoldenViewportCase> cases, double thickness, int imageWidth, int imageHeight)
    {
        // The preview the app shows in Fit is decoded for the device-pixel viewport box x 1.15 (PreviewQualityMultiplier), never larger
        // than the source, aspect preserved: its natural DIP size is what Image Stretch=Uniform measures.
        var boxWidth = view.Client.Width * view.DpiScale * 1.15;
        var boxHeight = view.Client.Height * view.DpiScale * 1.15;
        var scale = Math.Min(1.0, Math.Min(boxWidth / imageWidth, boxHeight / imageHeight));
        var bitmapWidth = Math.Max(1, Math.Round(imageWidth * scale));
        var bitmapHeight = Math.Max(1, Math.Round(imageHeight * scale));
        await view.ShowImageAsync(imageWidth, imageHeight, bitmapWidth, bitmapHeight);
        await view.ApplyFitAsync();
        cases.Add(Capture("fit-preview", view, thickness, imageWidth, imageHeight));
    }

    private static GoldenViewportCase Capture(string mode, GoldenWpfView view, double thickness, int imageWidth, int imageHeight)
    {
        view.Relayout();
        var viewer = view.Viewer;
        var scroll = view.Scroll;
        var image = view.Image;
        var origin = image.TranslatePoint(new Point(0, 0), scroll);
        var bitmap = image.Source ?? throw new InvalidOperationException("No bitmap.");
        var input = new ViewportInputDto(
            ClientWidth: view.Client.Width, ClientHeight: view.Client.Height,
            ScrollBarThickness: thickness,
            ScrollBars: "Auto",
            Stretch: viewer.Stretch.ToString(),
            ImageWidth: viewer.ImageWidth, ImageHeight: viewer.ImageHeight,
            MaxImageWidth: viewer.MaxImageWidth, MaxImageHeight: viewer.MaxImageHeight,
            BitmapWidth: bitmap.Width, BitmapHeight: bitmap.Height);
        var layout = new ViewportLayoutDto(
            ViewportWidth: scroll.ViewportWidth, ViewportHeight: scroll.ViewportHeight,
            ExtentWidth: scroll.ExtentWidth, ExtentHeight: scroll.ExtentHeight,
            HorizontalBarVisible: scroll.ComputedHorizontalScrollBarVisibility == Visibility.Visible,
            VerticalBarVisible: scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible,
            ImageX: origin.X + scroll.HorizontalOffset, ImageY: origin.Y + scroll.VerticalOffset,
            ImageWidth: image.ActualWidth, ImageHeight: image.ActualHeight,
            MaxHorizontalOffset: scroll.ScrollableWidth, MaxVerticalOffset: scroll.ScrollableHeight);
        return new GoldenViewportCase(CaseName(mode, imageWidth, imageHeight, view), input, layout);
    }

    internal static GoldenDocument<GoldenViewportCase> Document(IReadOnlyList<GoldenViewportCase> cases) =>
        new("viewport-layout", 1, "PhotoReview.Integration.Tests.Golden.ViewportGoldenRecorder (WPF MainWindow)", Notes, cases);
}
