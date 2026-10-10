using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.App.Viewport;
using PhotoReview.TestSupport.Golden;

namespace PhotoReview.Shell.Tests.Viewport;

/// <summary>
/// WP-16 / G-VIEW: <see cref="ViewportLayoutEngine"/> tái tạo <c>viewport-layout.v1.json</c> (ghi từ MainWindow WPF thật bởi
/// WP-10) với sai số &lt;= 0,5 DIP cho mọi trường, thanh cuộn nhìn thấy khớp tuyệt đối (C-08). Test golden tự Skip khi file chưa
/// có; golden KHÔNG được sửa để engine xanh - lệch là lỗi engine (hoặc báo lead). Hai test còn lại kiểm chính bộ so sánh.
/// </summary>
public sealed class ViewportGoldenTests
{
    public const double Tolerance = 0.5;

    [GoldenFact(GoldenFixture.ViewportLayoutFile)]
    public void Engine_ReproducesTheWpfGolden_WithinHalfADip()
    {
        var path = GoldenFixture.Find(GoldenFixture.ViewportLayoutFile)!;
        var cases = GoldenFixture.ReadCases<GoldenViewportCase>(File.ReadAllText(path));
        Assert.True(cases.Count >= 600, $"G-VIEW cần >= 600 ca (NO-WPF-EXEC-PLAN 7.3), file có {cases.Count}");
        var failures = Compare(cases);
        Assert.True(failures.Count == 0, $"{failures.Count}/{cases.Count} ca lệch golden:\n" + string.Join('\n', failures.Take(30)));
    }

    [Fact]
    public void Comparer_AcceptsDeviationsUpToHalfADip_AndRejectsMoreOrADifferentBar()
    {
        var input = new ViewportInputDto(1000, 600, 10, "Auto", "None", 3000, 2000, double.PositiveInfinity, double.PositiveInfinity, 750, 500);
        var exact = new ViewportLayoutDto(990, 590, 3000, 2000, true, true, 0, 0, 3000, 2000, 2010, 1410);

        Assert.Empty(Compare([new GoldenViewportCase("exact", input, exact)]));
        Assert.Empty(Compare([new GoldenViewportCase("0.5", input, exact with { ViewportWidth = 990.5, ImageX = -0.5 })]));
        Assert.Single(Compare([new GoldenViewportCase("0.51", input, exact with { MaxVerticalOffset = 1410.51 })]));
        Assert.Single(Compare([new GoldenViewportCase("bar", input, exact with { HorizontalBarVisible = false })]));
        Assert.Single(Compare([new GoldenViewportCase("image", input, exact with { ImageHeight = 1999 })]));
    }

    [Fact]
    public void Reader_AcceptsAnArrayOrAWrappedArray_WithNamedFloatingPointLiterals()
    {
        const string Case = """
            {"name":"fit","input":{"clientWidth":1280,"clientHeight":720,"scrollBarThickness":10,"scrollBars":"Auto","stretch":"Uniform",
             "imageWidth":"NaN","imageHeight":"NaN","maxImageWidth":1280,"maxImageHeight":720,"bitmapWidth":1500,"bitmapHeight":1000},
             "expected":{"viewportWidth":1280,"viewportHeight":720,"extentWidth":1080,"extentHeight":720,"horizontalBarVisible":false,
             "verticalBarVisible":false,"imageX":100,"imageY":0,"imageWidth":1080,"imageHeight":720,"maxHorizontalOffset":0,"maxVerticalOffset":0}}
            """;
        var plain = GoldenFixture.ReadCases<GoldenViewportCase>($"[{Case}]");
        var wrapped = GoldenFixture.ReadCases<GoldenViewportCase>($$"""{"version":1,"cases":[{{Case}}]}""");

        Assert.Equal(plain.Single(), wrapped.Single());
        Assert.True(double.IsNaN(plain[0].Input.ImageWidth));
        Assert.Empty(Compare(plain));
        Assert.Throws<InvalidDataException>(() => GoldenFixture.ReadCases<GoldenViewportCase>("""{"version":1}"""));
    }

    internal static ViewportInput ToInput(ViewportInputDto dto) => new(
        dto.ClientWidth, dto.ClientHeight, dto.ScrollBarThickness,
        Enum.Parse<ScrollBarPolicy>(dto.ScrollBars, ignoreCase: true),
        Enum.Parse<ViewerStretchMode>(dto.Stretch, ignoreCase: true),
        dto.ImageWidth, dto.ImageHeight, dto.MaxImageWidth, dto.MaxImageHeight, dto.BitmapWidth, dto.BitmapHeight);

    private static List<string> Compare(IEnumerable<GoldenViewportCase> cases)
    {
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var actual = ViewportLayoutEngine.Compute(ToInput(c.Input));
            var e = c.Expected;
            var diffs = new (string Field, double Expected, double Actual)[]
            {
                ("ViewportWidth", e.ViewportWidth, actual.ViewportWidth), ("ViewportHeight", e.ViewportHeight, actual.ViewportHeight),
                ("ExtentWidth", e.ExtentWidth, actual.ExtentWidth), ("ExtentHeight", e.ExtentHeight, actual.ExtentHeight),
                ("ImageX", e.ImageX, actual.ImageRect.X), ("ImageY", e.ImageY, actual.ImageRect.Y),
                ("ImageWidth", e.ImageWidth, actual.ImageRect.Width), ("ImageHeight", e.ImageHeight, actual.ImageRect.Height),
                ("MaxHorizontalOffset", e.MaxHorizontalOffset, actual.MaxHorizontalOffset),
                ("MaxVerticalOffset", e.MaxVerticalOffset, actual.MaxVerticalOffset),
            }.Where(d => !(Math.Abs(d.Expected - d.Actual) <= Tolerance)).Select(d => $"{d.Field} {d.Expected} != {d.Actual}").ToList();
            if (e.HorizontalBarVisible != actual.HorizontalBarVisible) diffs.Add($"HorizontalBar {e.HorizontalBarVisible} != {actual.HorizontalBarVisible}");
            if (e.VerticalBarVisible != actual.VerticalBarVisible) diffs.Add($"VerticalBar {e.VerticalBarVisible} != {actual.VerticalBarVisible}");
            if (diffs.Count > 0) failures.Add($"{c.Name}: {string.Join(", ", diffs)}");
        }
        return failures;
    }
}
