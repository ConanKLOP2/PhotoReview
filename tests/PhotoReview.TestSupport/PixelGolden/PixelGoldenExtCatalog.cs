using System.Globalization;

namespace PhotoReview.TestSupport.PixelGolden;

/// <summary>Which cell of the compare view carries the green selection border.</summary>
public enum PixelGoldenCompareSelection
{
    None,
    Left,
    Right,
}

/// <summary>One picture of a compare cell: a scene stored raw with an EXIF orientation, shown upright (as every decoder of the app delivers it).</summary>
/// <param name="Scene">A <see cref="PixelGoldenScenes"/> id.</param>
/// <param name="Width">Stored (pre-orientation) width.</param>
/// <param name="Height">Stored (pre-orientation) height.</param>
/// <param name="ExifOrientation">1..8.</param>
public sealed record PixelGoldenCompareSide(string Scene, int Width, int Height, int ExifOrientation = 1)
{
    public int OrientedWidth => ExifOrientation is >= 5 and <= 8 ? Height : Width;

    public int OrientedHeight => ExifOrientation is >= 5 and <= 8 ? Width : Height;
}

/// <summary>
/// WP-28b: a compare frame (two cells, selection border) over the usual Fit main picture. The WPF <c>MainWindow</c> golden is recorded
/// with the real <c>ComparePanel</c>; the shell draws it with <c>PresentationComposer</c>/<c>CompareLayer</c>.
/// </summary>
/// <param name="Name">File name stem of the golden PNG.</param>
/// <param name="ClientWidth">Viewer client area, DIP.</param>
/// <param name="ClientHeight">Viewer client area, DIP.</param>
/// <param name="Dpi">Monitor scale (1.0 = 96 dpi).</param>
/// <param name="Main">The main picture underneath (Fit); only its 8 DIP margin ring around the panel shows.</param>
/// <param name="Left">Left cell picture; null = still loading (empty cell).</param>
/// <param name="Right">Right cell picture; null = still loading (empty cell).</param>
/// <param name="Selected">Which border is green.</param>
public sealed record PixelGoldenCompareCase(
    string Name,
    int ClientWidth,
    int ClientHeight,
    double Dpi,
    PixelGoldenCompareSide Main,
    PixelGoldenCompareSide? Left,
    PixelGoldenCompareSide? Right,
    PixelGoldenCompareSelection Selected)
{
    public int PixelWidth => (int)Math.Round(ClientWidth * Dpi);

    public int PixelHeight => (int)Math.Round(ClientHeight * Dpi);

    /// <summary>The Fit case that renders the main picture alone (HUD hidden), so the recorder and the shell share one code path for it.</summary>
    public PixelGoldenCase MainCase => new(Name, Main.Scene, Main.Width, Main.Height, ClientWidth, ClientHeight, Dpi, 0, 0, 0, false,
        Main.ExifOrientation, false, PixelGoldenExpectation.Fit);

    public override string ToString() => Name;
}

/// <summary>
/// WP-28b (G-PIX extension): the cases that WP-28 listed under "Chua co" - compare of two pictures, F11 / DPI change of the window,
/// and a bitmap whose pixel size differs from the original (a RAW's embedded preview before the full decode). The mid-way
/// crossfade has no WPF counterpart and therefore no golden file (see <c>PixelGoldenCrossfadeTests</c>).
/// </summary>
public static class PixelGoldenExtCatalog
{
    /// <summary>The fake screen of the F11 cases, device pixels: far below any runner's monitor (CI is 1024x768).</summary>
    public const int ScreenWidthPixels = 720;

    /// <summary>See <see cref="ScreenWidthPixels"/>.</summary>
    public const int ScreenHeightPixels = 480;

    private static readonly Lazy<IReadOnlyList<PixelGoldenCase>> LazyFrames = new(BuildFrames);
    private static readonly Lazy<IReadOnlyList<PixelGoldenCompareCase>> LazyCompare = new(BuildCompare);

    /// <summary>F11/DPI and bitmap-size cases: ordinary G-PIX cases (one frame each), drawn by the same renderer as the base catalog.</summary>
    public static IReadOnlyList<PixelGoldenCase> Frames => LazyFrames.Value;

    /// <summary>Compare cases.</summary>
    public static IReadOnlyList<PixelGoldenCompareCase> Compare => LazyCompare.Value;

    /// <summary>Every golden file the extension owns (file name stems).</summary>
    public static IEnumerable<string> GoldenNames => Frames.Select(c => c.Name).Concat(Compare.Select(c => c.Name));

    public static PixelGoldenCase Frame(string name) =>
        Frames.FirstOrDefault(c => c.Name == name) ?? throw new ArgumentException("No G-PIX extension frame named " + name, nameof(name));

    public static PixelGoldenCompareCase CompareCase(string name) =>
        Compare.FirstOrDefault(c => c.Name == name) ?? throw new ArgumentException("No G-PIX compare case named " + name, nameof(name));

    private static List<PixelGoldenCase> BuildFrames()
    {
        var cases = new List<PixelGoldenCase>();

        void Add(string name, string scene, int iw, int ih, double cw, double ch, double dpi, int zoom, double ox, double oy,
            PixelGoldenExpectation expectation, int bitmapWidth = 0, int bitmapHeight = 0) =>
            cases.Add(new PixelGoldenCase(name, scene, iw, ih, (int)cw, (int)ch, dpi, zoom, ox, oy, false, 1, false, expectation)
            {
                BitmapWidth = bitmapWidth,
                BitmapHeight = bitmapHeight,
            });

        // --- F11: the first frame once the window covers the (fake) 720x480 px screen, Fit refitted to the new viewport ---
        Add("f11-screen-fit-landscape", PixelGoldenScenes.Photo, 1200, 800, 720, 480, 1.0, 0, 0, 0, PixelGoldenExpectation.Fit);
        Add("f11-screen-fit-portrait", PixelGoldenScenes.Photo, 800, 1200, 720, 480, 1.0, 0, 0, 0, PixelGoldenExpectation.Fit);
        Add("f11-screen-z100-origin", PixelGoldenScenes.Photo, 1200, 800, 720, 480, 1.0, 100, 0, 0, PixelGoldenExpectation.Identical);

        // --- DPI changes in F11 (the monitor changes from 100 % to 150 %) ---
        // Same device pixels (720x480): the window DIP size shrinks to 480x320.
        Add("f11-dpi150-samepx-fit", PixelGoldenScenes.Photo, 1200, 800, 480, 320, 1.5, 0, 0, 0, PixelGoldenExpectation.Fit);
        Add("f11-dpi150-samepx-z100-origin", PixelGoldenScenes.Photo, 1200, 800, 480, 320, 1.5, 100, 0, 0, PixelGoldenExpectation.NearIdentical);

        // Other device pixels (960x600 on the new monitor): client 640x400 DIP. 1.5:1 picture in a 1.6:1 viewport -> pillarbox.
        Add("f11-dpi150-otherpx-fit", PixelGoldenScenes.Photo, 1200, 800, 640, 400, 1.5, 0, 0, 0, PixelGoldenExpectation.Fit);

        // --- RAW: the embedded preview (480x320) is shown stretched to the layout of the 1920x1280 original, then the full decode ---
        Add("rawsize-fit-preview", PixelGoldenScenes.Photo, 1920, 1280, 600, 400, 1.0, 0, 0, 0, PixelGoldenExpectation.Fit, 480, 320);
        Add("rawsize-fit-full", PixelGoldenScenes.Photo, 1920, 1280, 600, 400, 1.0, 0, 0, 0, PixelGoldenExpectation.Fit);
        Add("rawsize-z100-preview", PixelGoldenScenes.Photo, 1920, 1280, 600, 400, 1.0, 100, 0.5, 0.5, PixelGoldenExpectation.Zoom, 480, 320);
        Add("rawsize-z100-full", PixelGoldenScenes.Photo, 1920, 1280, 600, 400, 1.0, 100, 0.5, 0.5, PixelGoldenExpectation.Identical);
        return cases;
    }

    private static List<PixelGoldenCompareCase> BuildCompare()
    {
        var photo = new PixelGoldenCompareSide(PixelGoldenScenes.Photo, 1200, 800);
        var landscape = photo;
        var portrait = new PixelGoldenCompareSide(PixelGoldenScenes.Photo, 800, 1200);
        var cases = new List<PixelGoldenCompareCase>();

        void Add(string name, double dpi, PixelGoldenCompareSide? left, PixelGoldenCompareSide? right, PixelGoldenCompareSelection selected,
            int clientWidth = 600, int clientHeight = 400) =>
            cases.Add(new PixelGoldenCompareCase(name, clientWidth, clientHeight, dpi, photo, left, right, selected));

        // Layout of two cells (each: Margin 4 + Border 2) inside the 8 DIP panel; pictures Uniform + centred.
        Add("cmp-photo-land-port-left", 1.0, landscape, portrait, PixelGoldenCompareSelection.Left);
        Add("cmp-photo-land-port-right", 1.0, landscape, portrait, PixelGoldenCompareSelection.Right);

        // Exact 1.4:1 pictures fill a 280x200 slot: one downscaled 4x, one upscaled 2x; nothing selected (both borders #555).
        Add("cmp-photo-pair-none", 1.0, new PixelGoldenCompareSide(PixelGoldenScenes.Photo, 1120, 800),
            new PixelGoldenCompareSide(PixelGoldenScenes.Photo, 140, 100), PixelGoldenCompareSelection.None);

        // Different EXIF orientation in the two cells: one stored upright, one stored rotated (6 = 90 deg), shown upright.
        Add("cmp-orient-1-vs-6-left", 1.0, new PixelGoldenCompareSide(PixelGoldenScenes.Orient, 240, 160, 1),
            new PixelGoldenCompareSide(PixelGoldenScenes.Orient, 240, 160, 6), PixelGoldenCompareSelection.Left);
        Add("cmp-orient-8-vs-3-dpi150-right", 1.5, new PixelGoldenCompareSide(PixelGoldenScenes.Orient, 240, 160, 8),
            new PixelGoldenCompareSide(PixelGoldenScenes.Orient, 240, 160, 3), PixelGoldenCompareSelection.Right);

        // The right picture is still loading: empty cell with its border, the left one drawn.
        Add("cmp-right-loading-left", 1.0, landscape, null, PixelGoldenCompareSelection.Left);
        return cases;
    }

    public static string Describe(PixelGoldenCompareCase c)
    {
        static string Side(PixelGoldenCompareSide? s) => s is null
            ? "(loading)"
            : string.Create(CultureInfo.InvariantCulture, $"{s.Scene} {s.Width}x{s.Height} exif {s.ExifOrientation}");

        return string.Create(CultureInfo.InvariantCulture,
            $"{c.Name}: client {c.ClientWidth}x{c.ClientHeight} @{c.Dpi:0.##}, left {Side(c.Left)}, right {Side(c.Right)}, selected {c.Selected}");
    }
}
