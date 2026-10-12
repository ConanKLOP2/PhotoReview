using System.Globalization;
using PhotoReview.TestSupport.PixelGolden;
using Xunit.Abstractions;

namespace PhotoReview.Shell.Tests.Golden;

/// <summary>
/// WP-28 (G-PIX, NO-WPF-EXEC-PLAN 7.3 (2)): the last frame of the Win32 shell against the WPF <c>MainWindow</c>. Each case's golden
/// (<c>tests/Fixtures/golden/pixel/{case}.png</c>, recorded from the real WPF window by <c>PixelGoldenRecorder</c>) is compared with
/// the same picture, client size, DPI and zoom/scroll state drawn by the shell (real viewport layout, composer, D2D renderer on the
/// WARP offscreen surface, scroll bars) on: picture geometry (&lt;= 1 px), picture-region PSNR/SSIM/MAE (Fit >= 32 dB, other zoom
/// >= 28 dB, byte-identical at 1:1), and the scroll bar/canvas strips. Independent of the runner's monitor: sizes come from the case.
/// A miss writes a diff image (expected | actual | difference) next to the failure message.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PixelGoldenTests(ITestOutputHelper output)
{
    public static TheoryData<string> PictureCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in PixelGoldenCatalog.All.Where(c => !c.Hud && c.Scene != PixelGoldenScenes.Orient))
        {
            data.Add(c.Name);
        }

        return data;
    }

    public static TheoryData<int> Orientations()
    {
        var data = new TheoryData<int>();
        for (var exif = 1; exif <= 8; exif++)
        {
            data.Add(exif);
        }

        return data;
    }

    internal static PixelFrame ReadGolden(string name)
    {
        var path = PixelGoldenFiles.PathOf(name);
        Assert.True(File.Exists(path), $"Missing golden {path}: record it with tools/diag/record-golden.ps1 (PixelGoldenRecordTests).");
        return PngFile.Read(path);
    }

    /// <summary>Renders the case on the shell, measures it against its golden, and writes the diff image when it misses a bar.</summary>
    internal static PixelGoldenMeasure Verify(PixelGoldenCase c, ShellFrame shell, ITestOutputHelper output)
    {
        var golden = ReadGolden(c.Name);
        Assert.Equal((c.PixelWidth, c.PixelHeight), (golden.Width, golden.Height));
        Assert.Equal((c.PixelWidth, c.PixelHeight), (shell.Frame.Width, shell.Frame.Height));
        var measure = PixelGoldenComparer.Measure(c, golden, shell.Frame, shell.Viewport, shell.Image);
        output.WriteLine(measure.Summary);
        var violations = PixelGoldenComparer.Violations(measure);
        if (violations.Count > 0)
        {
            var diff = PixelDiffReport.Write(PixelGoldenFiles.ReportDirectory(), c.Name, golden, shell.Frame);
            Assert.Fail($"{PixelGoldenCatalog.Describe(c)}\n  {string.Join("\n  ", violations)}\n  diff image: {diff}");
        }

        return measure;
    }

    [Theory]
    [MemberData(nameof(PictureCases))]
    public void ShellFrame_MatchesTheWpfGolden_WithinTheSection73Bars(string caseName)
    {
        var c = PixelGoldenCatalog.Get(caseName);

        Verify(c, PixelGoldenShellRenderer.Render(c), output);
    }

    [Theory]
    [MemberData(nameof(Orientations))]
    public void ExifOrientation_DecodedBuffer_IsByteIdenticalToTheWpfTransformedBitmap(int exif)
    {
        var c = PixelGoldenCatalog.Get("orient-" + exif);

        var measure = Verify(c, PixelGoldenShellRenderer.Render(c, ShellPictureRoute.Decoded), output);

        Assert.Equal(0, measure.ImageMaxAbs);
    }

    [Theory]
    [MemberData(nameof(Orientations))]
    public void ExifOrientation_RawBufferWithTheRendererMatrix_IsByteIdenticalToTheWpfTransformedBitmap(int exif)
    {
        var c = PixelGoldenCatalog.Get("orient-" + exif);

        var measure = Verify(c, PixelGoldenShellRenderer.Render(c, ShellPictureRoute.RawWithMatrix), output);

        Assert.Equal(0, measure.ImageMaxAbs);
    }

    public static TheoryData<string> OneToOneCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in PixelGoldenCatalog.All.Where(c => !c.Hud && c.ZoomPercent == 100 && c.ExifOrientation == 1))
        {
            data.Add(c.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(OneToOneCases))]
    public void OneToOne_IsTheSourceCopiedToTheNearestWholePixel_OfTheLayoutPosition(string caseName)
    {
        // At 100 % the shell copies source pixels: wherever the layout puts the picture (fractional offset, half-pixel centring,
        // DPI 1.5) the frame must be exactly the picture at the nearest whole device pixel - never a blend and never off by one.
        var c = PixelGoldenCatalog.Get(caseName);
        var shell = PixelGoldenShellRenderer.Render(c);
        var scene = PixelGoldenScenes.Create(c.Scene, c.ImageWidth, c.ImageHeight);
        var canvas = PixelGoldenCatalog.Canvas;
        var wrong = 0;
        (int X, int Y)? first = null;
        for (var y = 0; y < shell.Viewport.Height; y++)
        {
            for (var x = 0; x < shell.Viewport.Width; x++)
            {
                var ix = x - shell.Image.X;
                var iy = y - shell.Image.Y;
                var expected = ix >= 0 && iy >= 0 && ix < scene.Width && iy < scene.Height ? scene.At(ix, iy) : canvas;
                if (shell.Frame.At(x, y) != expected)
                {
                    wrong++;
                    first ??= (x, y);
                }
            }
        }

        Assert.True(wrong == 0, $"{caseName}: {wrong} px of the viewport differ from the picture placed at {shell.Image.X},{shell.Image.Y} (first at {first})");
    }

    [Fact]
    public void Canvas_OfTheShellWindowAndTheRenderer_IsTheWpfDarkCanvas()
    {
        // The golden renderer clears with RenderColors.DarkCanvas; the window clears with its own constant. Both must be #101010.
        var field = typeof(PhotoReview.Shell.Win32.ShellMainWindow).GetField("CanvasColor",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);
        var window = (PhotoReview.Shell.Rendering.ColorF)field.GetValue(null)!;
        var (b, g, r) = PixelGoldenCatalog.Canvas;

        Assert.Equal(PhotoReview.Shell.Rendering.RenderColors.DarkCanvas, window);
        Assert.Equal(PhotoReview.Shell.Rendering.RenderColors.FromRgb(r, g, b), window);
    }

    [Fact]
    public void Catalog_HasTheCoverageTheCardRequires()
    {
        var all = PixelGoldenCatalog.All;
        Assert.True(all.Count >= 30);
        Assert.Contains(all, c => c.IsFit && !c.Hud);
        Assert.Contains(all, c => c.ZoomPercent == 100 && c.Expectation == PixelGoldenExpectation.Identical);
        Assert.Contains(all, c => c.ZoomPercent is > 0 and < 100);
        Assert.Contains(all, c => c.ZoomPercent > 100);
        Assert.Contains(all, c => c.Dpi > 1.0);
        Assert.Contains(all, c => c.LinearScaling);
        Assert.Contains(all, c => c.Hud);
        for (var exif = 1; exif <= 8; exif++)
        {
            Assert.Contains(all, c => c.ExifOrientation == exif);
        }

        Assert.Equal(all.Count, all.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void GoldenFiles_AreExactlyTheCatalogCases_AndFitTheBudget()
    {
        var expected = PixelGoldenCatalog.All.Select(c => c.Name)
            .Concat(PixelGoldenCatalog.All.Where(c => c.Hud).Select(c => PixelGoldenCatalog.WithoutHud(c).Name))
            .Order(StringComparer.Ordinal).ToList();
        Assert.True(Directory.Exists(PixelGoldenFiles.Directory), "tests/Fixtures/golden/pixel is missing");
        var files = Directory.EnumerateFiles(PixelGoldenFiles.Directory, "*.png").ToList();
        var names = files.Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(expected, names);
        Assert.True(files.Sum(f => new FileInfo(f).Length) <= 5L * 1024 * 1024, "G-PIX golden exceeds 5 MB");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void ReportAllCases_WritesMeasuresAndDiffImages_WhenAFolderIsGiven()
    {
        // Not a gate: the numbers behind the decision fragment (tools/diag/pixel-golden-report.ps1 sets the variable).
        var directory = Environment.GetEnvironmentVariable(PixelGoldenFiles.ReportDirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            output.WriteLine($"{PixelGoldenFiles.ReportDirectoryVariable} not set: skipped");
            return;
        }

        Directory.CreateDirectory(directory);
        var lines = new List<string>();
        foreach (var c in PixelGoldenCatalog.All.Where(c => !c.Hud))
        {
            var shell = PixelGoldenShellRenderer.Render(c);
            var golden = ReadGolden(c.Name);
            var measure = PixelGoldenComparer.Measure(c, golden, shell.Frame, shell.Viewport, shell.Image);
            lines.Add(measure.Summary + (PixelGoldenComparer.Violations(measure).Count > 0 ? "  <-- MISSES A BAR" : string.Empty));
            PixelDiffReport.Write(directory, c.Name, golden, shell.Frame);
        }

        File.WriteAllLines(Path.Combine(directory, "report.txt"), lines);
        Assert.Equal(PixelGoldenCatalog.All.Count(c => !c.Hud), lines.Count);
        foreach (var line in lines)
        {
            output.WriteLine(line);
        }
    }
}
