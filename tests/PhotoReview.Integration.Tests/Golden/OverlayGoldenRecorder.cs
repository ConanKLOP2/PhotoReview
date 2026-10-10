using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PhotoReview.TestSupport.Golden;

namespace PhotoReview.Integration.Tests.Golden;

/// <summary>
/// WP-10 G-OVL (<c>overlay-boxes.v1.json</c>): bounding box (TranslatePoint of the panel origin + ActualWidth/ActualHeight, DIP, relative
/// to the window content) of each overlay panel of the main window, with fixed strings, at 3 window sizes x 2 DPI x 2 overlay font
/// sizes. The Win32 overlay layout (WP-23) must match within 2 DIP (text pixels are not compared: ClearType differs).
/// </summary>
internal static class OverlayGoldenRecorder
{
    internal static readonly (int Width, int Height)[] WindowSizes = [(800, 600), (1366, 728), (1920, 1080)];
    internal static readonly double[] DpiScales = [1.0, 1.5];
    internal static readonly double[] FontSizes = [12, 18];

    internal static readonly string[] PanelNames =
        ["ToolbarPanel", "StatusPanel", "FolderInfoPanel", "ZoomIndicatorPanel", "CapturePairBadge", "ComparePanel"];

    internal const string StatusLine = "IMG_0042.JPG  (42/1280)  6000 x 4000  14,2 MB";
    internal const string ExifLine = "Canon EOS R5  |  85 mm  f/1.8  1/200 s  ISO 100  |  Taken: 2026-10-01 14:23";
    internal const string FolderLine = "D:\\Photos\\2026\\Trip to Da Lat  -  1280 images";
    internal const string ZoomLine = "100 %";
    internal const string CapturePairLine = "RAW + JPG";

    internal const string Notes =
        "Recorded from the WPF MainWindow with fixed strings (status/exif/folder/zoom/capture-pair, see GoldenOverlayBox). Box = " +
        "panel.TranslatePoint(0,0) relative to the window content + ActualWidth/ActualHeight, DIP; every panel forced Visible, text " +
        "TextBlocks set to the fixed strings, InfoOverlayFontSize = the case's font (EXIF line = font - 1). Language: Vietnamese " +
        "(toolbar buttons/tooltips). Fonts: Segoe UI / Segoe UI Emoji of the recording machine; compare with +/- 2 DIP.";

    public static async Task<IReadOnlyList<GoldenOverlayBox>> RecordAsync()
    {
        var boxes = new List<GoldenOverlayBox>();
        await GoldenWpfHost.RunAsync(view =>
        {
            view.ApplySettings("{}");
            foreach (var (width, height) in WindowSizes)
            {
                foreach (var dpi in DpiScales)
                {
                    foreach (var font in FontSizes)
                    {
                        view.SetClient(width, height);
                        view.SetDpi(dpi);
                        Arrange(view, font);
                        foreach (var panel in PanelNames)
                        {
                            var element = PanelOf(view, panel);
                            var origin = element.TranslatePoint(new Point(0, 0), (UIElement)view.Window.Content);
                            var name = string.Create(CultureInfo.InvariantCulture, $"w{width}x{height}-dpi{dpi:0.##}-font{font:0}-{panel}");
                            boxes.Add(new GoldenOverlayBox(name, width, height, dpi, font, panel, origin.X, origin.Y, element.ActualWidth, element.ActualHeight));
                        }
                    }
                }
            }
            return Task.CompletedTask;
        });
        return boxes;
    }

    private static Border PanelOf(GoldenWpfView view, string panel) => panel switch
    {
        "ToolbarPanel" => view.Window.ToolbarPanel,
        "StatusPanel" => view.Window.StatusPanel,
        "FolderInfoPanel" => view.Window.FolderInfoPanel,
        "ZoomIndicatorPanel" => view.Window.ZoomIndicatorPanel,
        "CapturePairBadge" => view.Window.CapturePairBadge,
        "ComparePanel" => view.Window.ComparePanel,
        _ => throw new ArgumentOutOfRangeException(nameof(panel), panel, null),
    };

    private static void Arrange(GoldenWpfView view, double font)
    {
        var window = view.Window;
        view.Window.Settings.InfoOverlayFontSize = font;
        window.ViewModel.InfoOverlay.Refresh();

        window.StatusText.Text = StatusLine;
        window.ExifInfoText.Text = ExifLine;
        window.FolderInfoText.Text = FolderLine;
        window.ZoomIndicatorText.Text = ZoomLine;
        ((TextBlock)window.CapturePairBadge.Child).Text = CapturePairLine;
        window.ExifInfoText.Visibility = Visibility.Visible;
        foreach (var panel in PanelNames) PanelOf(view, panel).Visibility = Visibility.Visible;
        view.Relayout();
    }

    internal static GoldenDocument<GoldenOverlayBox> Document(IReadOnlyList<GoldenOverlayBox> boxes) =>
        new("overlay-boxes", 1, "PhotoReview.Integration.Tests.Golden.OverlayGoldenRecorder (WPF MainWindow)", Notes, boxes);
}
