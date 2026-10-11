using System.Globalization;
using PhotoReview.App.Input;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Viewport;
using PhotoReview.Shell.Win32.Overlay;
using PhotoReview.TestSupport.Golden;
using Xunit.Abstractions;

namespace PhotoReview.Shell.Tests.Overlay;

/// <summary>
/// WP-17 (G-OVL): bố cục của khung overlay + DirectWrite thật khớp <c>overlay-boxes.v1.json</c> (WP-10 ghi từ MainWindow WPF
/// thật) +/- 2 DIP, cho 5 panel không phải nút: StatusPanel, FolderInfoPanel, ZoomIndicatorPanel, CapturePairBadge,
/// ComparePanel (60 ca = 3 cửa sổ x 2 DPI x 2 cỡ chữ x 5 panel). ToolbarPanel cần nút WPF (chrome theo DarkControls) và chuỗi
/// địa phương hoá - WP-23 kiểm. Cây dựng đúng cấu trúc <c>MainWindow.xaml</c>: Border Margin 8, Padding 6,4, CornerRadius 4;
/// StatusText (FontSize) + ExifInfoText (FontSize - 1, Margin 0,2,0,0), cả hai CharacterEllipsis; FolderInfo MaxWidth 640;
/// CapturePairBadge căn giữa, Margin 8,44,8,8.
/// </summary>
[Trait("Category", "Native")]
public sealed class OverlayGoldenTests(ITestOutputHelper output)
{
    internal const string StatusLine = "IMG_0042.JPG  (42/1280)  6000 x 4000  14,2 MB";
    internal const string ExifLine = "Canon EOS R5  |  85 mm  f/1.8  1/200 s  ISO 100  |  Taken: 2026-10-01 14:23";
    internal const string FolderLine = "D:\\Photos\\2026\\Trip to Da Lat  -  1280 images";
    internal const string ZoomLine = "100 %";
    internal const string CapturePairLine = "RAW + JPG";

    private const double Tolerance = 2.0;

    private static readonly string[] CheckedPanels = ["StatusPanel", "FolderInfoPanel", "ZoomIndicatorPanel", "CapturePairBadge", "ComparePanel"];

    /// <summary>Dựng overlay như MainWindow.xaml với chuỗi cố định của recorder và <paramref name="font"/>.</summary>
    internal static OverlayHost BuildScene(double font)
    {
        TextStyle main = new("Segoe UI", font);
        TextStyle exif = new("Segoe UI", font - 1);
        var host = new OverlayHost();

        host.Add(new PanelElement("ComparePanel") { Margin = new OverlayThickness(8), Background = new ColorF(0, 0, 0, 0) });

        host.Add(new PanelElement("StatusPanel")
        {
            HorizontalAlignment = OverlayHorizontalAlignment.Left,
            VerticalAlignment = OverlayVerticalAlignment.Bottom,
            Margin = new OverlayThickness(8),
            Padding = new OverlayThickness(6, 4),
            CornerRadius = 4,
        }
        .Add(new TextElement("StatusText") { Text = StatusLine, Style = main, Trimming = TextTrimming.CharacterEllipsis })
        .Add(new TextElement("ExifInfoText")
        {
            Text = ExifLine,
            Style = exif,
            Trimming = TextTrimming.CharacterEllipsis,
            Margin = new OverlayThickness(0, 2, 0, 0),
            Color = DarkPalette.TextSecondary,
        }));

        host.Add(new PanelElement("FolderInfoPanel")
        {
            HorizontalAlignment = OverlayHorizontalAlignment.Right,
            VerticalAlignment = OverlayVerticalAlignment.Bottom,
            Margin = new OverlayThickness(8),
            Padding = new OverlayThickness(6, 4),
            CornerRadius = 4,
            MaxWidth = 640,
        }
        .Add(new TextElement("FolderInfoText") { Text = FolderLine, Style = main, Trimming = TextTrimming.CharacterEllipsis }));

        host.Add(new PanelElement("ZoomIndicatorPanel")
        {
            HorizontalAlignment = OverlayHorizontalAlignment.Right,
            VerticalAlignment = OverlayVerticalAlignment.Top,
            Margin = new OverlayThickness(8),
            Padding = new OverlayThickness(6, 4),
            CornerRadius = 4,
        }
        .Add(new TextElement("ZoomIndicatorText") { Text = ZoomLine, Style = main }));

        host.Add(new PanelElement("CapturePairBadge")
        {
            HorizontalAlignment = OverlayHorizontalAlignment.Center,
            VerticalAlignment = OverlayVerticalAlignment.Top,
            Margin = new OverlayThickness(8, 44, 8, 8),
            Padding = new OverlayThickness(6, 4),
            CornerRadius = 4,
        }
        .Add(new TextElement("CapturePairText") { Text = CapturePairLine, Style = main }));

        return host;
    }

    [GoldenFact("overlay-boxes.v1.json")]
    public void FiveTextPanelsMatchTheWpfGoldenWithinTwoDips()
    {
        string path = GoldenFixture.Find("overlay-boxes.v1.json")!;
        IReadOnlyList<GoldenOverlayBox> golden = GoldenFixture.ReadCases<GoldenOverlayBox>(File.ReadAllText(path));
        Assert.Equal(72, golden.Count);

        using var renderer = new DWriteTextRenderer("vi-VN", 256);
        var failures = new List<string>();
        int checkedCases = 0;
        double worst = 0;
        foreach (var group in golden.GroupBy(b => (b.WindowWidth, b.WindowHeight, b.DpiScale, b.OverlayFontSize)))
        {
            using OverlayHost host = BuildScene(group.Key.OverlayFontSize);
            host.Arrange(new OverlayLayoutContext(new SizeD(group.Key.WindowWidth, group.Key.WindowHeight), group.Key.DpiScale, renderer));
            foreach (GoldenOverlayBox box in group)
            {
                if (!CheckedPanels.Contains(box.Panel))
                {
                    continue;
                }

                checkedCases++;
                RectD actual = host.Find(box.Panel)!.Bounds;
                double d = Math.Max(
                    Math.Max(Math.Abs(actual.X - box.X), Math.Abs(actual.Y - box.Y)),
                    Math.Max(Math.Abs(actual.Width - box.Width), Math.Abs(actual.Height - box.Height)));
                worst = Math.Max(worst, d);
                if (d > Tolerance)
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{box.Name}: golden ({box.X:F2},{box.Y:F2},{box.Width:F2},{box.Height:F2}) got ({actual.X:F2},{actual.Y:F2},{actual.Width:F2},{actual.Height:F2}) delta {d:F2}"));
                }
            }
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"G-OVL: {checkedCases} cases, worst delta {worst:F3} DIP (tolerance {Tolerance})"));
        Assert.Equal(60, checkedCases);
        Assert.True(failures.Count == 0, $"worst delta {worst:F2} DIP; " + string.Join("\n", failures.Take(12)));
    }

    [GoldenFact("overlay-boxes.v1.json")]
    public void TheSamePanelsDoNotMoveWhenOnlyTheDpiChanges()
    {
        string path = GoldenFixture.Find("overlay-boxes.v1.json")!;
        IReadOnlyList<GoldenOverlayBox> golden = GoldenFixture.ReadCases<GoldenOverlayBox>(File.ReadAllText(path));
        // Golden: cùng (cửa sổ, cỡ chữ) ở 2 DPI cho cùng hộp DIP (phát hiện 2 của WP-10) - bố cục của ta cũng phải bất biến.
        using var renderer = new DWriteTextRenderer("vi-VN", 256);
        foreach (var size in golden.Select(b => (b.WindowWidth, b.WindowHeight, b.OverlayFontSize)).Distinct())
        {
            var rects = new List<RectD[]>();
            foreach (double dpi in new[] { 1.0, 1.5 })
            {
                using OverlayHost host = BuildScene(size.OverlayFontSize);
                host.Arrange(new OverlayLayoutContext(new SizeD(size.WindowWidth, size.WindowHeight), dpi, renderer));
                rects.Add(CheckedPanels.Select(p => host.Find(p)!.Bounds).ToArray());
            }

            Assert.Equal(rects[0], rects[1]);
        }
    }
}

