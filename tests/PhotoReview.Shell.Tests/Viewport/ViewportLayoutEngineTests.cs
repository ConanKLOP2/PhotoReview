using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.App.Viewport;

namespace PhotoReview.Shell.Tests.Viewport;

/// <summary>
/// WP-16: <see cref="ViewportLayoutEngine"/> - giá trị mong đợi tính tay từ thuật toán layout WPF (ScrollViewer Auto/Auto +
/// Image Uniform/Fill, xem doc của engine). Cùng các ca này đã được đối chứng bit-for-bit với WPF thật ở
/// <c>PhotoReview.App.Tests.Viewport.ViewportEngineWpfParityTests</c> (2.660 ca lưới + ca biên).
/// </summary>
public sealed class ViewportLayoutEngineTests
{
    private const double Bar = 10;
    private const double Inf = double.PositiveInfinity;

    private static ViewportInput Fit(double cw, double ch, double bitmapW, double bitmapH) =>
        new(cw, ch, Bar, ScrollBarPolicy.Auto, ViewerStretchMode.Uniform, double.NaN, double.NaN, cw, ch, bitmapW, bitmapH);

    private static ViewportInput Zoomed(double cw, double ch, double w, double h, ScrollBarPolicy policy = ScrollBarPolicy.Auto) =>
        new(cw, ch, Bar, policy, ViewerStretchMode.None, w, h, Inf, Inf, Math.Max(1, w / 4), Math.Max(1, h / 4));

    private static void AssertLayout(ViewportLayout layout, double vw, double vh, double ew, double eh, bool hBar, bool vBar,
        double x, double y, double w, double h, double maxH, double maxV)
    {
        Assert.Equal(vw, layout.ViewportWidth, 9);
        Assert.Equal(vh, layout.ViewportHeight, 9);
        Assert.Equal(ew, layout.ExtentWidth, 9);
        Assert.Equal(eh, layout.ExtentHeight, 9);
        Assert.Equal(hBar, layout.HorizontalBarVisible);
        Assert.Equal(vBar, layout.VerticalBarVisible);
        Assert.Equal(x, layout.ImageRect.X, 9);
        Assert.Equal(y, layout.ImageRect.Y, 9);
        Assert.Equal(w, layout.ImageRect.Width, 9);
        Assert.Equal(h, layout.ImageRect.Height, 9);
        Assert.Equal(maxH, layout.MaxHorizontalOffset, 9);
        Assert.Equal(maxV, layout.MaxVerticalOffset, 9);
    }

    // ---- Fit (Stretch=Uniform, MaxWidth/MaxHeight = vùng client) ----

    [Fact]
    public void Fit_Landscape_FillsTheHeightAndIsCentredHorizontally()
    {
        // 1500x1000 vào 1280x720: tỉ lệ min(0,8533; 0,72) = 0,72 -> 1080x720, lề trái (1280-1080)/2.
        AssertLayout(ViewportLayoutEngine.Compute(Fit(1280, 720, 1500, 1000)),
            1280, 720, 1080, 720, false, false, 100, 0, 1080, 720, 0, 0);
    }

    [Fact]
    public void Fit_Portrait_FillsTheHeightAndIsCentredHorizontally()
    {
        AssertLayout(ViewportLayoutEngine.Compute(Fit(1280, 720, 1000, 1500)),
            1280, 720, 480, 720, false, false, 400, 0, 480, 720, 0, 0);
    }

    [Fact]
    public void Fit_Panorama_FillsTheWidthAndIsCentredVertically()
    {
        var layout = ViewportLayoutEngine.Compute(Fit(800, 600, 3000, 250));
        var h = 250 * (800 / 3000.0);
        AssertLayout(layout, 800, 600, 800, h, false, false, 0, (600 - h) / 2, 800, h, 0, 0);
    }

    [Fact]
    public void Fit_SmallBitmap_IsEnlargedToFill_StretchDirectionBoth()
    {
        AssertLayout(ViewportLayoutEngine.Compute(Fit(800, 600, 100, 75)),
            800, 600, 800, 600, false, false, 0, 0, 800, 600, 0, 0);
    }

    [Fact]
    public void Fit_NeverShowsScrollBars_EvenWhenTheImageExactlyFillsTheClient()
    {
        var layout = ViewportLayoutEngine.Compute(Fit(1600, 900, 1600, 900));
        Assert.False(layout.HorizontalBarVisible);
        Assert.False(layout.VerticalBarVisible);
        Assert.Equal(1600, layout.ViewportWidth);
    }

    [Fact]
    public void Uniform_WithoutABound_MeasuresToTheBitmapsNaturalSize_AndScrolls()
    {
        // ViewerState trước lần UpdateViewport đầu: Uniform, MaxImage = vô hạn -> ảnh đo bằng kích thước tự nhiên.
        var input = Fit(1000, 600, 1500, 900) with { MaxImageWidth = Inf, MaxImageHeight = Inf };
        AssertLayout(ViewportLayoutEngine.Compute(input), 990, 590, 1500, 900, true, true, 0, 0, 1500, 900, 510, 310);
    }

    [Fact]
    public void Uniform_BoundedInWidthOnly_ScalesBothAxesByTheWidth()
    {
        var input = Fit(1000, 600, 1500, 900) with { MaxImageWidth = 800, MaxImageHeight = Inf };
        // 800/1500 cho cả hai trục: 800x480; ô = max(extent, viewport) = 1000x600 -> căn giữa.
        AssertLayout(ViewportLayoutEngine.Compute(input), 1000, 600, 800, 480, false, false, 100, 60, 800, 480, 0, 0);
    }

    [Fact]
    public void Uniform_BoundedInHeightOnly_ScalesBothAxesByTheHeight()
    {
        var input = Fit(1000, 600, 1500, 900) with { MaxImageWidth = Inf, MaxImageHeight = 300 };
        AssertLayout(ViewportLayoutEngine.Compute(input), 1000, 600, 500, 300, false, false, 250, 150, 500, 300, 0, 0);
    }

    [Fact]
    public void Fit_WithoutABitmap_IsAnEmptyElementAtTheCentre()
    {
        AssertLayout(ViewportLayoutEngine.Compute(Fit(1000, 600, 0, 0)), 1000, 600, 0, 0, false, false, 500, 300, 0, 0, 0, 0);
    }

    // ---- Zoom (Stretch=None -> Fill, Width/Height tường minh) ----

    [Fact]
    public void Zoomed_LargerThanTheClient_ShowsBothBars_WhichTakeTheirThicknessFromTheViewport()
    {
        AssertLayout(ViewportLayoutEngine.Compute(Zoomed(1000, 600, 3000, 2000)),
            990, 590, 3000, 2000, true, true, 0, 0, 3000, 2000, 2010, 1410);
    }

    [Fact]
    public void Zoomed_WiderOnly_ShowsTheHorizontalBar_AndCentresVerticallyInTheShorterViewport()
    {
        AssertLayout(ViewportLayoutEngine.Compute(Zoomed(1000, 600, 2000, 300)),
            1000, 590, 2000, 300, true, false, 0, 145, 2000, 300, 1000, 0);
    }

    [Fact]
    public void Zoomed_TallerOnly_ShowsTheVerticalBar_AndCentresHorizontallyInTheNarrowerViewport()
    {
        AssertLayout(ViewportLayoutEngine.Compute(Zoomed(1000, 600, 500, 2000)),
            990, 600, 500, 2000, false, true, 245, 0, 500, 2000, 0, 1400);
    }

    [Fact]
    public void Zoomed_SmallerThanTheClient_IsCentred_WithoutBars()
    {
        AssertLayout(ViewportLayoutEngine.Compute(Zoomed(1000, 600, 400, 300)),
            1000, 600, 400, 300, false, false, 300, 150, 400, 300, 0, 0);
    }

    [Fact]
    public void ThirdMeasurePass_TheVerticalBarMakesTheWidthOverflow_SoTheHorizontalBarAppearsToo()
    {
        // Lượt 1: 995 <= 1000 (không thanh ngang), 2000 > 600 (thanh dọc). Lượt 3: 995 > 1000 - 10 -> thanh ngang.
        AssertLayout(ViewportLayoutEngine.Compute(Zoomed(1000, 600, 995, 2000)),
            990, 590, 995, 2000, true, true, 0, 0, 995, 2000, 5, 1410);
    }

    [Fact]
    public void ThirdMeasurePass_TheHorizontalBarMakesTheHeightOverflow_SoTheVerticalBarAppearsToo()
    {
        AssertLayout(ViewportLayoutEngine.Compute(Zoomed(1000, 600, 2000, 595)),
            990, 590, 2000, 595, true, true, 0, 0, 2000, 595, 1010, 5);
    }

    [Fact]
    public void ThirdMeasurePass_ExactlyFillingTheNarrowedViewport_DoesNotOverflow()
    {
        AssertLayout(ViewportLayoutEngine.Compute(Zoomed(1000, 600, 990, 2000)),
            990, 600, 990, 2000, false, true, 0, 0, 990, 2000, 0, 1400);
        AssertLayout(ViewportLayoutEngine.Compute(Zoomed(1000, 600, 2000, 590)),
            1000, 590, 2000, 590, true, false, 0, 0, 2000, 590, 1000, 0);
    }

    [Fact]
    public void Overflow_UsesWpfDoubleUtil_AFloatingPointHairOverTheClientIsNotAnOverflow()
    {
        // FitWidth: ảnh = px * (cw * dpi / px) / dpi có thể lệch cw ~1e-13; WPF (AreClose) không hiện thanh.
        var hair = ViewportLayoutEngine.Compute(Zoomed(1000, 600, 1000 + 1e-13, 600));
        Assert.False(hair.HorizontalBarVisible);
        Assert.False(hair.VerticalBarVisible);

        var real = ViewportLayoutEngine.Compute(Zoomed(1000, 600, 1000.001, 600));
        Assert.True(real.HorizontalBarVisible);
        Assert.Equal(590, real.ViewportHeight, 9);
    }

    [Fact]
    public void ClientThinnerThanABar_LeavesAnEmptyViewport_NotANegativeOne()
    {
        AssertLayout(ViewportLayoutEngine.Compute(Zoomed(6, 6, 50, 50)), 0, 0, 50, 50, true, true, 0, 0, 50, 50, 50, 50);
    }

    [Fact]
    public void ExplicitSize_WithoutABitmap_KeepsTheExtent_ButDrawsNothing()
    {
        var input = Zoomed(1000, 600, 2000, 1500) with { BitmapWidth = 0, BitmapHeight = 0 };
        // Extent = Width/Height (MinMax), ActualWidth = 0, căn giữa trong ô 2000x1500.
        AssertLayout(ViewportLayoutEngine.Compute(input), 990, 590, 2000, 1500, true, true, 1000, 750, 0, 0, 1010, 910);
    }

    [Fact]
    public void Fill_WithAnAutoSize_MeasuresNaturally_ThenStretchesToTheViewport()
    {
        // Stretch=None khi kích thước nguồn chưa biết (ImageWidth = NaN): Fill đo theo bitmap rồi giãn ra theo ô.
        var input = new ViewportInput(1000, 600, Bar, ScrollBarPolicy.Auto, ViewerStretchMode.None, double.NaN, double.NaN, Inf, Inf, 400, 300);
        AssertLayout(ViewportLayoutEngine.Compute(input), 1000, 600, 400, 300, false, false, 0, 0, 1000, 600, 0, 0);
    }

    [Fact]
    public void MaxSmallerThanAnExplicitSize_ClampsTheElement()
    {
        var input = Zoomed(1000, 600, 2000, 1500) with { MaxImageWidth = 500, MaxImageHeight = 400 };
        AssertLayout(ViewportLayoutEngine.Compute(input), 1000, 600, 500, 400, false, false, 250, 100, 500, 400, 0, 0);
    }

    // ---- chính sách thanh cuộn (NE-3, chỉ bản Win32) ----

    [Fact]
    public void OverlayBars_AreShownOnOverflow_ButTakeNoSpace()
    {
        AssertLayout(ViewportLayoutEngine.Compute(Zoomed(1000, 600, 995, 2000, ScrollBarPolicy.Overlay)),
            1000, 600, 995, 2000, false, true, 2.5, 0, 995, 2000, 0, 1400);
    }

    [Fact]
    public void HiddenBars_AreNeverShown_TheContentStillScrolls()
    {
        AssertLayout(ViewportLayoutEngine.Compute(Zoomed(1000, 600, 3000, 2000, ScrollBarPolicy.Hidden)),
            1000, 600, 3000, 2000, false, false, 0, 0, 3000, 2000, 2000, 1400);
    }

    // ---- input không hợp lệ ----

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-5)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidClientSize_ReadsAsZero(double bad)
    {
        var layout = ViewportLayoutEngine.Compute(Zoomed(bad, bad, 400, 300) with { ScrollBarThickness = bad });
        Assert.Equal(0, layout.ViewportWidth);
        Assert.Equal(0, layout.ViewportHeight);
        Assert.True(layout.HorizontalBarVisible);
        Assert.Equal(400, layout.MaxHorizontalOffset, 9);
    }

    [Fact]
    public void InvalidElementSizes_FallBackToWpfsValidatedMeaning()
    {
        // Width vô hạn/âm -> auto (NaN); MaxWidth NaN/âm -> vô hạn: cùng kết quả với ca Fill auto-size.
        var input = new ViewportInput(1000, 600, Bar, ScrollBarPolicy.Auto, ViewerStretchMode.None, Inf, -1, double.NaN, -3, 400, 300);
        AssertLayout(ViewportLayoutEngine.Compute(input), 1000, 600, 400, 300, false, false, 0, 0, 1000, 600, 0, 0);
        var noBitmap = input with { BitmapWidth = double.NaN, BitmapHeight = -1 };
        Assert.Equal(0, ViewportLayoutEngine.Compute(noBitmap).ExtentWidth);
    }

    // ---- ClampOffset (ScrollContentPresenter.CoerceOffset) ----

    [Fact]
    public void ClampOffset_KeepsInRangeOffsets_AndClampsBothEnds()
    {
        var layout = ViewportLayoutEngine.Compute(Zoomed(1000, 600, 3000, 2000));
        Assert.Equal((123.5, 77.25), ViewportLayoutEngine.ClampOffset(layout, 123.5, 77.25));
        Assert.Equal((2010.0, 1410.0), ViewportLayoutEngine.ClampOffset(layout, 1e9, 1e9));
        Assert.Equal((0.0, 0.0), ViewportLayoutEngine.ClampOffset(layout, -1, -1e9));
        Assert.Equal((2010.0, 0.0), ViewportLayoutEngine.ClampOffset(layout, 2010, 0));
    }

    [Fact]
    public void ClampOffset_ContentSmallerThanTheViewport_IsAlwaysZero()
    {
        var layout = ViewportLayoutEngine.Compute(Zoomed(1000, 600, 400, 300));
        Assert.Equal((0.0, 0.0), ViewportLayoutEngine.ClampOffset(layout, 50, 50));
    }

    [Fact]
    public void ClampOffset_NaN_ReadsAsZero()
    {
        var layout = ViewportLayoutEngine.Compute(Zoomed(1000, 600, 3000, 2000));
        Assert.Equal((0.0, 15.0), ViewportLayoutEngine.ClampOffset(layout, double.NaN, 15));
        Assert.Equal((15.0, 0.0), ViewportLayoutEngine.ClampOffset(layout, 15, double.NaN));
    }

    // ---- DoubleUtil ----

    [Fact]
    public void DoubleUtil_MatchesWpf()
    {
        Assert.True(ViewportLayoutMath.AreClose(1000, 1000 + 1e-13));
        Assert.False(ViewportLayoutMath.AreClose(1000, 1000.000001));
        Assert.True(ViewportLayoutMath.AreClose(double.PositiveInfinity, double.PositiveInfinity));
        // eps = (|a| + |b| + 10) * DBL_EPSILON: biên của khoảng mở.
        var eps = (1.0 + 1.0 + 10.0) * ViewportLayoutMath.WpfDoubleEpsilon;
        Assert.False(ViewportLayoutMath.AreClose(1.0 + (2 * eps), 1.0));
        Assert.False(ViewportLayoutMath.AreClose(1.0, 1.0 + (2 * eps)));
        Assert.True(ViewportLayoutMath.GreaterThan(2, 1));
        Assert.False(ViewportLayoutMath.GreaterThan(1, 2));
        Assert.False(ViewportLayoutMath.GreaterThan(1 + 1e-15, 1));
        Assert.True(ViewportLayoutMath.IsZero(1e-15));
        Assert.True(ViewportLayoutMath.IsZero(-1e-15));
        Assert.False(ViewportLayoutMath.IsZero(1e-14));
    }
}
