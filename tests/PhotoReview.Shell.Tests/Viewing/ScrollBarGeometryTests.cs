using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.App.Viewport;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>
/// WP-26: hình học thuần của thanh cuộn (<see cref="ScrollBarGeometry"/>): số đo của <c>DarkScrollBars.xaml</c>, kích thước và vị
/// trí con trượt, ẩn khi vừa khung, ô góc, ánh xạ con trượt -> offset và hit-test. Đối chiếu với WPF thật nằm ở
/// <c>ScrollBarWpfParityTests</c> (Shell.WpfBridge.Tests).
/// </summary>
public sealed class ScrollBarGeometryTests
{
    private static readonly ScrollBarMetrics Metrics = new();

    private static ViewportLayout LayoutFor(double clientWidth, double clientHeight, double extentWidth, double extentHeight) =>
        ViewportLayoutEngine.Compute(new ViewportInput(clientWidth, clientHeight, 10, ScrollBarPolicy.Auto, ViewerStretchMode.None,
            extentWidth, extentHeight, double.PositiveInfinity, double.PositiveInfinity, extentWidth, extentHeight));

    private static ScrollBarLayout Compute(double clientWidth, double clientHeight, double extentWidth, double extentHeight,
        double horizontalOffset = 0, double verticalOffset = 0, ScrollBarMetrics? metrics = null) =>
        ScrollBarGeometry.Compute(new SizeD(clientWidth, clientHeight), LayoutFor(clientWidth, clientHeight, extentWidth, extentHeight),
            horizontalOffset, verticalOffset, metrics ?? Metrics);

    [Fact]
    public void Defaults_AreTheValuesOfDarkScrollBarsXaml()
    {
        Assert.Equal(10, Metrics.Thickness);
        Assert.Equal(2, Metrics.TrackMarginAlong);
        Assert.Equal(1, Metrics.TrackMarginAcross);
        Assert.Equal(8, Metrics.MinThumbLength);
        Assert.Equal(4, Metrics.ThumbRadius);
        // Cùng độ dày mà engine viewport trừ khỏi vùng nhìn: hai số không được lệch nhau.
        Assert.Equal(new ViewportControllerOptions().ScrollBarThickness, Metrics.Thickness);
    }

    [Fact]
    public void BothBars_BarsOccupyTheEdges_TracksHaveTheirMargins_AndTheCornerIsTheSquareLeft()
    {
        var layout = Compute(1000, 600, 3000, 2000);

        Assert.True(layout.Vertical.Visible);
        Assert.True(layout.Horizontal.Visible);
        Assert.Equal(new RectD(990, 0, 10, 590), layout.Vertical.Bar);
        Assert.Equal(new RectD(0, 590, 990, 10), layout.Horizontal.Bar);
        Assert.Equal(new RectD(991, 2, 8, 586), layout.Vertical.Track);   // Margin="1,2"
        Assert.Equal(new RectD(2, 591, 986, 8), layout.Horizontal.Track); // Margin="2,1"
        Assert.True(layout.HasCorner);
        Assert.Equal(new RectD(990, 590, 10, 10), layout.Corner);
    }

    [Fact]
    public void ThumbLength_IsTrackTimesViewportOverExtent_AndPositionIsProportionalToTheOffset()
    {
        var atTop = Compute(1000, 600, 3000, 2000).Vertical;
        // viewport 590, extent 2000, rãnh 586 -> 586 * 590 / 2000 = 172,87.
        Assert.Equal(172.87, atTop.Thumb.Height, 6);
        Assert.Equal(2, atTop.Thumb.Y, 9);
        Assert.Equal(991, atTop.Thumb.X, 9);
        Assert.Equal(8, atTop.Thumb.Width, 9);

        var middle = Compute(1000, 600, 3000, 2000, verticalOffset: 705).Vertical; // max 1410: đúng nửa
        Assert.Equal(2 + ((586 - 172.87) / 2), middle.Thumb.Y, 6);

        var bottom = Compute(1000, 600, 3000, 2000, verticalOffset: 1410).Vertical;
        Assert.Equal(2 + 586 - 172.87, bottom.Thumb.Y, 6);
        Assert.Equal(588, bottom.Thumb.Bottom, 6); // rãnh kết thúc cách đáy thanh (590) 2 DIP

        var horizontal = Compute(1000, 600, 3000, 2000, horizontalOffset: 2010).Horizontal;
        Assert.Equal(986 * 990 / 3000.0, horizontal.Thumb.Width, 6);
        Assert.Equal(2 + 986 - (986 * 990 / 3000.0), horizontal.Thumb.X, 6);
    }

    [Fact]
    public void ThumbHasAMinimumLength_ThatTheProportionalSizeWouldGoBelow()
    {
        var box = Compute(1000, 600, 3000, 1_000_000).Vertical;
        Assert.Equal(8, box.Thumb.Height, 9);
        Assert.True(box.ThumbVisible);
        // Con trượt tối thiểu vẫn đi hết rãnh: offset tối đa -> sát đáy rãnh.
        var bottom = Compute(1000, 600, 3000, 1_000_000, verticalOffset: 1e9).Vertical;
        Assert.Equal(bottom.Track.Bottom, bottom.Thumb.Bottom, 9);
    }

    [Fact]
    public void NoBarsAreProduced_WhenTheImageFitsTheViewport()
    {
        var layout = Compute(1000, 600, 800, 500);
        Assert.False(layout.AnyVisible);
        Assert.False(layout.HasCorner);
        Assert.Equal(ScrollBarPart.None, ScrollBarGeometry.HitTest(layout, new PointD(995, 300)));
    }

    [Fact]
    public void OnlyOneBar_NoCorner_AndTheBarSpansTheWholeEdge()
    {
        var verticalOnly = Compute(1000, 600, 900, 2000);
        Assert.True(verticalOnly.Vertical.Visible);
        Assert.False(verticalOnly.Horizontal.Visible);
        Assert.False(verticalOnly.HasCorner);
        Assert.Equal(new RectD(990, 0, 10, 600), verticalOnly.Vertical.Bar);

        var horizontalOnly = Compute(1000, 600, 3000, 500);
        Assert.False(horizontalOnly.Vertical.Visible);
        Assert.Equal(new RectD(0, 590, 1000, 10), horizontalOnly.Horizontal.Bar);
    }

    [Fact]
    public void TheSecondBarAppearsBecauseTheFirstOneTookRoom_LikeWpfsThirdMeasure()
    {
        // Ảnh rộng 995 trong client 1000: chỉ thanh dọc (cao 2000), nhưng thanh dọc làm viewport còn 990 -> thanh ngang hiện.
        var layout = Compute(1000, 600, 995, 2000);
        Assert.True(layout.Vertical.Visible);
        Assert.True(layout.Horizontal.Visible);
        Assert.True(layout.HasCorner);
    }

    [Fact]
    public void ThumbIsHidden_WhenTheTrackHasNoRoomForIt_ButTheBarStillShows()
    {
        // Thanh dọc cao 14 - 10 = 4 DIP: rãnh 0.
        var layout = Compute(14, 14, 100, 100);
        Assert.True(layout.Vertical.Visible);
        Assert.False(layout.Vertical.ThumbVisible);
        Assert.Equal(ScrollBarPart.Background, ScrollBarGeometry.HitTest(layout, new PointD(12, 2)));
    }

    [Fact]
    public void OffsetForThumbStart_IsLinear_ClampedAndZeroWithoutTravel()
    {
        var box = Compute(1000, 600, 3000, 2000).Vertical;
        var travel = box.Travel;

        Assert.Equal(0, ScrollBarGeometry.OffsetForThumbStart(box, box.TrackStart), 9);
        Assert.Equal(0, ScrollBarGeometry.OffsetForThumbStart(box, box.TrackStart - 50), 9);
        Assert.Equal(1410, ScrollBarGeometry.OffsetForThumbStart(box, box.TrackStart + travel), 9);
        Assert.Equal(1410, ScrollBarGeometry.OffsetForThumbStart(box, box.TrackStart + travel + 999), 9);
        Assert.Equal(1410 * 100 / travel, ScrollBarGeometry.OffsetForThumbStart(box, box.TrackStart + 100), 9);

        var hidden = Compute(14, 14, 100, 100).Vertical;
        Assert.Equal(0, ScrollBarGeometry.OffsetForThumbStart(hidden, 3), 9);
    }

    [Fact]
    public void ThumbPositionAndOffsetAreInverse_AcrossTheRange()
    {
        foreach (var offset in new[] { 0.0, 1.0, 333.3, 705, 1409, 1410 })
        {
            var box = Compute(1000, 600, 3000, 2000, verticalOffset: offset).Vertical;
            Assert.Equal(offset, ScrollBarGeometry.OffsetForThumbStart(box, box.ThumbStart), 6);
        }
    }

    [Theory]
    [InlineData(995, 100, (int)ScrollBarPart.Thumb)]          // trong con trượt (2..174,87)
    [InlineData(995, 300, (int)ScrollBarPart.TrackAfter)]
    [InlineData(990.5, 300, (int)ScrollBarPart.Background)]   // lề trái của Track (Margin 1)
    [InlineData(995, 1, (int)ScrollBarPart.Background)]       // lề trên của Track (Margin 2)
    [InlineData(995, 588.5, (int)ScrollBarPart.Background)]   // lề dưới
    [InlineData(500, 595, (int)ScrollBarPart.TrackAfter)]     // thanh ngang
    [InlineData(100, 595, (int)ScrollBarPart.Thumb)]
    [InlineData(995, 595, (int)ScrollBarPart.Corner)]
    [InlineData(500, 300, (int)ScrollBarPart.None)]
    public void HitTest_ReportsThePartUnderThePoint(double x, double y, int expected)
    {
        var layout = Compute(1000, 600, 3000, 2000);
        Assert.Equal((ScrollBarPart)expected, ScrollBarGeometry.HitTest(layout, new PointD(x, y)));
    }

    [Fact]
    public void HitTest_TrackBeforeAndAfter_AreSeparatedByTheThumb_WithHalfOpenEdges()
    {
        var layout = Compute(1000, 600, 3000, 2000, verticalOffset: 705);
        var thumb = layout.Vertical.Thumb;
        Assert.Equal(ScrollBarPart.TrackBefore, ScrollBarGeometry.HitTest(layout, new PointD(995, thumb.Y - 0.001)));
        Assert.Equal(ScrollBarPart.Thumb, ScrollBarGeometry.HitTest(layout, new PointD(995, thumb.Y)));
        Assert.Equal(ScrollBarPart.Thumb, ScrollBarGeometry.HitTest(layout, new PointD(995, thumb.Bottom - 0.001)));
        Assert.Equal(ScrollBarPart.TrackAfter, ScrollBarGeometry.HitTest(layout, new PointD(995, thumb.Bottom)));
        Assert.Equal(ScrollBarPart.None, ScrollBarGeometry.HitTest(layout, new PointD(1000, 100))); // phải của thanh: ngoài
    }

    [Fact]
    public void NonFiniteOrNegativeInputs_DoNotProduceNaNBoxes()
    {
        var layout = ViewportLayoutEngine.Compute(new ViewportInput(double.NaN, 600, 10, ScrollBarPolicy.Auto, ViewerStretchMode.None,
            3000, 2000, double.PositiveInfinity, double.PositiveInfinity, 3000, 2000));
        var geometry = ScrollBarGeometry.Compute(new SizeD(double.NaN, 600), layout, double.NaN, -5, Metrics);
        foreach (var box in new[] { geometry.Vertical, geometry.Horizontal })
        {
            Assert.False(double.IsNaN(box.Thumb.X) || double.IsNaN(box.Thumb.Y) || double.IsNaN(box.Thumb.Width) || double.IsNaN(box.Thumb.Height));
        }
    }
}
