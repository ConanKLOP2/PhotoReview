using System.Globalization;

namespace PhotoReview.TestSupport.PixelGolden;

/// <summary>Which bar of NO-WPF-EXEC-PLAN 7.3 (2) a case is held to on the image region.</summary>
public enum PixelGoldenExpectation
{
    /// <summary>1 source pixel = 1 device pixel on the pixel grid (100 %, whole-pixel placement): byte-identical.</summary>
    Identical,

    /// <summary>100 % at a DPI above 1 (whole-pixel placement): WPF's own resampling leaves +/-1 level; max channel delta 1.</summary>
    NearIdentical,

    /// <summary>Fit (the image is rescaled to the viewport): PSNR &gt;= 32 dB.</summary>
    Fit,

    /// <summary>Any other zoom, or 100 % off the pixel grid (filters differ: WPF Fant/linear vs D2D cubic/linear, NE-9): PSNR &gt;= 28 dB.</summary>
    Zoom,

    /// <summary>
    /// Rescaled 1-2 px periodic patterns (the synthetic <c>chart</c> scene): the worst case for a resampler, where WPF Fant (which is
    /// not an exact area average at 2:1) and D2D cubic alias differently. Held to measured floors, not to the 7.3 bars (NE-9).
    /// </summary>
    HighFrequency,

    /// <summary>
    /// 1:1 picture of 1 px patterns placed on a HALF device pixel: WPF resamples (blurs), the shell snaps to the pixel grid (sharp,
    /// within 0.5 px). Held to geometry (&lt;= 1 px) and a low measured floor.
    /// </summary>
    HalfPixel,
}

/// <summary>
/// One G-PIX case: the same picture, client size, DPI, zoom/scroll state and HUD state rendered by the WPF MainWindow (golden PNG,
/// recorded by <c>PixelGoldenRecorder</c>) and by the Win32 shell on a WARP offscreen surface (<c>PixelGoldenTests</c>).
/// </summary>
/// <param name="Name">File name stem of the golden PNG (<c>tests/Fixtures/golden/pixel/{Name}.png</c>).</param>
/// <param name="Scene">A <see cref="PixelGoldenScenes"/> id.</param>
/// <param name="ImageWidth">Stored (pre-orientation) pixel width of the picture.</param>
/// <param name="ImageHeight">Stored (pre-orientation) pixel height of the picture.</param>
/// <param name="ClientWidth">Viewer client area, DIP.</param>
/// <param name="ClientHeight">Viewer client area, DIP.</param>
/// <param name="Dpi">Monitor scale (1.0 = 96 dpi).</param>
/// <param name="ZoomPercent">0 = Fit; otherwise original-relative zoom in percent (one source pixel per device pixel at 100).</param>
/// <param name="OffsetX">Horizontal scroll position as a fraction of the maximum offset (0..1); ignored in Fit.</param>
/// <param name="OffsetY">Vertical scroll position as a fraction of the maximum offset (0..1); ignored in Fit.</param>
/// <param name="LinearScaling">The <c>ScalingQuality.Linear</c> setting (default: HighQuality).</param>
/// <param name="ExifOrientation">1..8: the picture is stored raw and displayed upright.</param>
/// <param name="Hud">Overlay panels (toolbar, status, folder, zoom indicator, capture pair) visible with the fixed strings.</param>
public sealed record PixelGoldenCase(
    string Name,
    string Scene,
    int ImageWidth,
    int ImageHeight,
    int ClientWidth,
    int ClientHeight,
    double Dpi,
    int ZoomPercent,
    double OffsetX,
    double OffsetY,
    bool LinearScaling,
    int ExifOrientation,
    bool Hud,
    PixelGoldenExpectation Expectation)
{
    public bool IsFit => ZoomPercent == 0;

    /// <summary>Upright (post-orientation) picture size, pixels.</summary>
    public int OrientedWidth => ExifOrientation is >= 5 and <= 8 ? ImageHeight : ImageWidth;

    public int OrientedHeight => ExifOrientation is >= 5 and <= 8 ? ImageWidth : ImageHeight;

    /// <summary>Frame size in device pixels (what the RenderTargetBitmap and the D2D offscreen surface produce).</summary>
    public int PixelWidth => (int)Math.Round(ClientWidth * Dpi);

    public int PixelHeight => (int)Math.Round(ClientHeight * Dpi);

    public override string ToString() => Name;
}

/// <summary>The G-PIX case list (single source for the recorder, the conformance test and the shell test).</summary>
public static class PixelGoldenCatalog
{
    /// <summary>Canvas colour of the main window (<c>Dark.Canvas</c>, #101010).</summary>
    public static readonly (byte B, byte G, byte R) Canvas = (0x10, 0x10, 0x10);

    /// <summary>Scroll bar thickness (DIP) of the dark scroll bar style.</summary>
    public const double ScrollBarThickness = 10;

    public const string StatusLine = "IMG_0042.JPG  (42/1280)  6000 x 4000  14,2 MB";
    public const string ExifLine = "Canon EOS R5  |  85 mm  f/1.8  1/200 s  ISO 100  |  Taken: 2026-10-01 14:23";
    public const string FolderLine = "D:\\Photos\\2026\\Trip to Da Lat  -  1280 images";
    public const string ZoomLine = "100 %";
    public const string CapturePairLine = "RAW + JPG";

    /// <summary>Overlay font size used by the HUD cases (the app default).</summary>
    public const double HudFontSize = 12;

    private static readonly Lazy<IReadOnlyList<PixelGoldenCase>> LazyAll = new(Build);

    public static IReadOnlyList<PixelGoldenCase> All => LazyAll.Value;

    public static PixelGoldenCase Get(string name) =>
        All.FirstOrDefault(c => c.Name == name) ?? throw new ArgumentException("No G-PIX case named " + name, nameof(name));

    /// <summary>The same case with the HUD hidden (reference for panel detection).</summary>
    public static PixelGoldenCase WithoutHud(PixelGoldenCase hudCase) =>
        hudCase with { Name = hudCase.Name + "-nohud", Hud = false };

    private static List<PixelGoldenCase> Build()
    {
        var cases = new List<PixelGoldenCase>();

        void Add(string name, string scene, int iw, int ih, int cw, int ch, double dpi, int zoom, double ox, double oy,
            PixelGoldenExpectation expectation, bool linear = false, int exif = 1, bool hud = false) =>
            cases.Add(new PixelGoldenCase(name, scene, iw, ih, cw, ch, dpi, zoom, ox, oy, linear, exif, hud, expectation));

        // --- Fit: the picture is rescaled to the viewport (preview decoded to the box in the app; here the full picture) ---
        Add("fit-photo-landscape", PixelGoldenScenes.Photo, 1200, 800, 480, 320, 1.0, 0, 0, 0, PixelGoldenExpectation.Fit);
        Add("fit-photo-portrait-letterbox", PixelGoldenScenes.Photo, 800, 1200, 480, 320, 1.0, 0, 0, 0, PixelGoldenExpectation.Fit);
        Add("fit-photo-upscale", PixelGoldenScenes.Photo, 240, 160, 480, 320, 1.0, 0, 0, 0, PixelGoldenExpectation.Fit);
        Add("fit-chart-downscale", PixelGoldenScenes.Chart, 1920, 1280, 480, 320, 1.0, 0, 0, 0, PixelGoldenExpectation.HighFrequency);
        Add("fit-photo-panorama", PixelGoldenScenes.Photo, 2400, 400, 480, 320, 1.0, 0, 0, 0, PixelGoldenExpectation.Fit);
        Add("fit-photo-linear", PixelGoldenScenes.Photo, 1200, 800, 480, 320, 1.0, 0, 0, 0, PixelGoldenExpectation.Fit, linear: true);
        Add("fit-photo-dpi150", PixelGoldenScenes.Photo, 1200, 800, 480, 320, 1.5, 0, 0, 0, PixelGoldenExpectation.Fit);
        Add("fit-chart-dpi125", PixelGoldenScenes.Chart, 1920, 1280, 480, 320, 1.25, 0, 0, 0, PixelGoldenExpectation.HighFrequency);

        // --- 100 %: one source pixel per device pixel, whole-pixel placement => identical ---
        Add("z100-photo-origin", PixelGoldenScenes.Photo, 1200, 800, 480, 320, 1.0, 100, 0, 0, PixelGoldenExpectation.Identical);
        Add("z100-photo-middle", PixelGoldenScenes.Photo, 1200, 800, 480, 320, 1.0, 100, 0.5, 0.5, PixelGoldenExpectation.Identical);
        Add("z100-photo-end", PixelGoldenScenes.Photo, 1200, 800, 480, 320, 1.0, 100, 1, 1, PixelGoldenExpectation.Identical);
        Add("z100-chart-dpi150-end", PixelGoldenScenes.Chart, 1200, 800, 480, 320, 1.5, 100, 1, 1, PixelGoldenExpectation.NearIdentical);
        Add("z100-chart-dpi150-half-pixel", PixelGoldenScenes.Chart, 1200, 800, 480, 320, 1.5, 100, 0.5, 0.5, PixelGoldenExpectation.HalfPixel);
        Add("z100-photo-small-centered", PixelGoldenScenes.Photo, 240, 160, 480, 320, 1.0, 100, 0, 0, PixelGoldenExpectation.Identical);
        // Off the pixel grid: odd client => the centred picture sits on a half pixel; a fractional scroll offset. Not identical.
        Add("z100-photo-half-pixel-centered", PixelGoldenScenes.Photo, 240, 160, 481, 321, 1.0, 100, 0, 0, PixelGoldenExpectation.Zoom);
        Add("z100-photo-fractional-offset", PixelGoldenScenes.Photo, 1200, 800, 480, 320, 1.0, 100, 0.3337, 0.4113, PixelGoldenExpectation.Zoom);

        // --- other zooms: filters differ (NE-9) ---
        Add("z25-chart", PixelGoldenScenes.Chart, 1920, 1280, 480, 320, 1.0, 25, 0, 0, PixelGoldenExpectation.HighFrequency);
        Add("z50-photo-middle", PixelGoldenScenes.Photo, 1200, 800, 480, 320, 1.0, 50, 0.5, 0.5, PixelGoldenExpectation.Zoom);
        Add("z50-chart-middle", PixelGoldenScenes.Chart, 1200, 800, 480, 320, 1.0, 50, 0.5, 0.5, PixelGoldenExpectation.HighFrequency);
        Add("z50-chart-linear", PixelGoldenScenes.Chart, 1200, 800, 480, 320, 1.0, 50, 0.5, 0.5, PixelGoldenExpectation.Zoom, linear: true);
        Add("z75-photo-middle", PixelGoldenScenes.Photo, 1200, 800, 480, 320, 1.0, 75, 0.5, 0.5, PixelGoldenExpectation.Zoom);
        Add("z150-photo-middle", PixelGoldenScenes.Photo, 640, 480, 480, 320, 1.0, 150, 0.5, 0.5, PixelGoldenExpectation.Zoom);
        Add("z200-photo-middle", PixelGoldenScenes.Photo, 640, 480, 480, 320, 1.0, 200, 0.5, 0.5, PixelGoldenExpectation.Zoom);
        Add("z200-chart-middle", PixelGoldenScenes.Chart, 640, 480, 480, 320, 1.0, 200, 0.5, 0.5, PixelGoldenExpectation.HighFrequency);
        Add("z400-photo-origin", PixelGoldenScenes.Photo, 320, 240, 480, 320, 1.0, 400, 0, 0, PixelGoldenExpectation.Zoom);
        Add("z200-photo-linear", PixelGoldenScenes.Photo, 640, 480, 480, 320, 1.0, 200, 0.5, 0.5, PixelGoldenExpectation.Zoom, linear: true);

        // --- orientation: raw picture + EXIF 1..8 shown upright (100 %, no scroll bars) ---
        for (var exif = 1; exif <= 8; exif++)
        {
            Add($"orient-{exif}", PixelGoldenScenes.Orient, 240, 160, 320, 260, 1.0, 100, 0, 0, PixelGoldenExpectation.Identical, exif: exif);
        }

        // --- HUD on/off (panels over the picture; compared by panel box, text pixels are not compared) ---
        Add("hud-fit", PixelGoldenScenes.Photo, 1200, 800, 800, 480, 1.0, 0, 0, 0, PixelGoldenExpectation.Fit, hud: true);
        Add("hud-z100", PixelGoldenScenes.Photo, 1200, 800, 800, 480, 1.0, 100, 0.5, 0.5, PixelGoldenExpectation.Identical, hud: true);
        Add("hud-fit-dpi150", PixelGoldenScenes.Photo, 1200, 800, 800, 480, 1.5, 0, 0, 0, PixelGoldenExpectation.Fit, hud: true);
        return cases;
    }

    /// <summary>The offset (DIP) of a scroll position given as a fraction of the maximum offset, as both sides compute it.</summary>
    public static double OffsetFromFraction(double fraction, double maxOffset) =>
        Math.Round(Math.Clamp(fraction, 0, 1) * maxOffset, 4, MidpointRounding.AwayFromZero);

    public static string Describe(PixelGoldenCase c)
    {
        var view = c.IsFit
            ? "Fit"
            : string.Create(CultureInfo.InvariantCulture, $"{c.ZoomPercent}% off ({c.OffsetX:0.####}, {c.OffsetY:0.####})");
        var head = string.Create(CultureInfo.InvariantCulture,
            $"{c.Name}: {c.Scene} {c.ImageWidth}x{c.ImageHeight} exif {c.ExifOrientation}, client {c.ClientWidth}x{c.ClientHeight} @{c.Dpi:0.##}, ");
        return head + view + (c.LinearScaling ? ", Linear" : string.Empty) + (c.Hud ? ", HUD" : string.Empty);
    }
}
