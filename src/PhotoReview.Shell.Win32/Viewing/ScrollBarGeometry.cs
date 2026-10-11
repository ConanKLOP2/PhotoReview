using PhotoReview.App.Input;
using PhotoReview.App.Viewport;

namespace PhotoReview.Shell.Win32.Viewing;

/// <summary>Trục của một thanh cuộn.</summary>
internal enum ScrollAxis
{
    Vertical = 0,
    Horizontal = 1,
}

/// <summary>Phần của thanh cuộn mà một điểm rơi vào (<see cref="ScrollBarGeometry.HitTest"/>).</summary>
internal enum ScrollBarPart
{
    /// <summary>Ngoài mọi thanh cuộn.</summary>
    None = 0,

    /// <summary>Con trượt (<c>Thumb</c>).</summary>
    Thumb,

    /// <summary>Đường rãnh phía trước con trượt (<c>DecreaseRepeatButton</c>: PageUp/PageLeft).</summary>
    TrackBefore,

    /// <summary>Đường rãnh phía sau con trượt (<c>IncreaseRepeatButton</c>: PageDown/PageRight).</summary>
    TrackAfter,

    /// <summary>Nền thanh cuộn ngoài rãnh (lề của <c>Track</c>): nuốt chuột, không làm gì.</summary>
    Background,

    /// <summary>Ô góc (<c>PART_ScrollCorner</c>, chỉ khi cả hai thanh hiện).</summary>
    Corner,
}

/// <summary>
/// Số đo của thanh cuộn (DIP), lấy từ <c>Themes/DarkScrollBars.xaml</c> và <c>Track</c> của WPF. Giá trị mặc định = bản WPF ở
/// 96 DPI; <see cref="MinThumbLength"/> trong WPF phụ thuộc <c>SystemParameters</c> nên được tiêm.
/// </summary>
internal readonly record struct ScrollBarMetrics
{
    /// <summary>Mặc định = bản WPF (khác <c>default(ScrollBarMetrics)</c> toàn số 0).</summary>
    public ScrollBarMetrics()
    {
    }

    /// <summary><c>ScrollBar Width/Height = 10</c>.</summary>
    public double Thickness { get; init; } = 10;

    /// <summary>Lề của <c>Track</c> dọc theo trục (<c>Margin="1,2"</c> dọc: 2; ngang <c>"2,1"</c>: 2).</summary>
    public double TrackMarginAlong { get; init; } = 2;

    /// <summary>Lề của <c>Track</c> ngang trục (1).</summary>
    public double TrackMarginAcross { get; init; } = 1;

    /// <summary>Độ dài tối thiểu của con trượt (<c>Track.ComputeScrollBarLengths</c>).</summary>
    public double MinThumbLength { get; init; } = DefaultMinThumbLength;

    /// <summary><c>CornerRadius="4"</c> của <c>Dark.ScrollThumb</c>.</summary>
    public double ThumbRadius { get; init; } = 4;

    /// <summary>
    /// Sàn của <c>Track</c> WPF: <c>Math.Floor(nút cuộn / 2)</c> = 8 (đo bằng ScrollViewer thật; nút cuộn 16,5-17 DIP ở mọi DPI
    /// hệ thống thường gặp nên luôn ra 8).
    /// </summary>
    internal const double DefaultMinThumbLength = 8;
}

/// <summary>Hình học của một thanh cuộn (toạ độ client, DIP).</summary>
/// <param name="Axis">Trục.</param>
/// <param name="Visible">Thanh có hiện (theo <see cref="ViewportLayout"/> và chính sách).</param>
/// <param name="Bar">Hình chữ nhật của cả thanh (nền <c>Dark.ScrollTrack</c>).</param>
/// <param name="Track">Hình chữ nhật của <c>Track</c> (thanh trừ lề).</param>
/// <param name="Thumb">Con trượt; rỗng khi <see cref="ThumbVisible"/> sai.</param>
/// <param name="ThumbVisible">Rãnh đủ chỗ cho con trượt.</param>
/// <param name="MaxOffset">Offset tối đa (<c>ScrollViewer.ScrollableWidth/Height</c>).</param>
/// <param name="Offset">Offset hiện tại (<c>ScrollBar.Value</c>).</param>
/// <param name="ViewportLength">Độ dài vùng nhìn theo trục (<c>ViewportSize</c>; một trang cuộn).</param>
internal readonly record struct ScrollBarBox(
    ScrollAxis Axis, bool Visible, RectD Bar, RectD Track, RectD Thumb, bool ThumbVisible,
    double MaxOffset, double Offset, double ViewportLength)
{
    public static ScrollBarBox Hidden(ScrollAxis axis) => new(axis, false, default, default, default, false, 0, 0, 0);

    /// <summary>Toạ độ đầu rãnh theo trục.</summary>
    public double TrackStart => Axis == ScrollAxis.Vertical ? Track.Y : Track.X;

    public double TrackLength => Axis == ScrollAxis.Vertical ? Track.Height : Track.Width;

    public double ThumbStart => Axis == ScrollAxis.Vertical ? Thumb.Y : Thumb.X;

    public double ThumbLength => Axis == ScrollAxis.Vertical ? Thumb.Height : Thumb.Width;

    /// <summary>Quãng con trượt đi được dọc rãnh.</summary>
    public double Travel => Math.Max(0.0, TrackLength - ThumbLength);

    /// <summary>Thành phần của <paramref name="point"/> theo trục.</summary>
    public double Along(PointD point) => Axis == ScrollAxis.Vertical ? point.Y : point.X;
}

/// <summary>Hình học của cả hai thanh cuộn và ô góc.</summary>
internal readonly record struct ScrollBarLayout(ScrollBarBox Vertical, ScrollBarBox Horizontal, RectD Corner, bool HasCorner)
{
    public static ScrollBarLayout None { get; } = new(ScrollBarBox.Hidden(ScrollAxis.Vertical), ScrollBarBox.Hidden(ScrollAxis.Horizontal), default, false);

    public bool AnyVisible => Vertical.Visible || Horizontal.Visible;

    public ScrollBarBox Box(ScrollAxis axis) => axis == ScrollAxis.Vertical ? Vertical : Horizontal;
}

/// <summary>
/// WP-26 (NE-3 a): hình học thuần của thanh cuộn tự vẽ, tái tạo <c>ScrollBar</c> + <c>Track</c> của <c>DarkScrollBars.xaml</c>:
/// thanh 10 DIP (dọc ở cột phải, ngang ở hàng dưới, chiếm chỗ), <c>Track Margin="1,2"</c>/<c>"2,1"</c> không nút mũi tên, con trượt
/// dài <c>rãnh x viewport / (max + viewport)</c> tối thiểu <see cref="ScrollBarMetrics.MinThumbLength"/>, vị trí tỉ lệ offset
/// (dọc: offset 0 ở trên), ô góc khi cả hai thanh hiện. Không phụ thuộc cửa sổ nên test được trên số.
/// </summary>
internal static class ScrollBarGeometry
{
    /// <summary>Hình học từ layout của engine viewport và offset hiện tại (DIP, toạ độ client).</summary>
    public static ScrollBarLayout Compute(SizeD client, in ViewportLayout layout, double horizontalOffset, double verticalOffset,
        in ScrollBarMetrics metrics)
    {
        var width = Sanitize(client.Width);
        var height = Sanitize(client.Height);
        var thickness = Sanitize(metrics.Thickness);
        var horizontalVisible = layout.HorizontalBarVisible;
        var verticalVisible = layout.VerticalBarVisible;

        // Hàng/cột Auto của lưới: thanh dọc cao bằng hàng * (client trừ thanh ngang), thanh ngang rộng bằng cột * (client trừ thanh dọc).
        var barThicknessX = Math.Min(thickness, width);
        var barThicknessY = Math.Min(thickness, height);
        var verticalBar = verticalVisible
            ? new RectD(Math.Max(0.0, width - thickness), 0, barThicknessX, Math.Max(0.0, height - (horizontalVisible ? thickness : 0)))
            : default;
        var horizontalBar = horizontalVisible
            ? new RectD(0, Math.Max(0.0, height - thickness), Math.Max(0.0, width - (verticalVisible ? thickness : 0)), barThicknessY)
            : default;

        var vertical = verticalVisible
            ? BuildBox(ScrollAxis.Vertical, verticalBar, layout.MaxVerticalOffset, verticalOffset, layout.ViewportHeight, layout.ExtentHeight, metrics)
            : ScrollBarBox.Hidden(ScrollAxis.Vertical);
        var horizontal = horizontalVisible
            ? BuildBox(ScrollAxis.Horizontal, horizontalBar, layout.MaxHorizontalOffset, horizontalOffset, layout.ViewportWidth, layout.ExtentWidth, metrics)
            : ScrollBarBox.Hidden(ScrollAxis.Horizontal);

        var both = verticalVisible && horizontalVisible;
        var corner = both ? new RectD(Math.Max(0.0, width - thickness), Math.Max(0.0, height - thickness), barThicknessX, barThicknessY) : default;
        return new ScrollBarLayout(vertical, horizontal, corner, both);
    }

    private static ScrollBarBox BuildBox(ScrollAxis axis, RectD bar, double maxOffset, double offset, double viewportLength, double extent,
        in ScrollBarMetrics metrics)
    {
        var vertical = axis == ScrollAxis.Vertical;
        var track = vertical
            ? new RectD(bar.X + metrics.TrackMarginAcross, bar.Y + metrics.TrackMarginAlong,
                Math.Max(0.0, bar.Width - (2 * metrics.TrackMarginAcross)), Math.Max(0.0, bar.Height - (2 * metrics.TrackMarginAlong)))
            : new RectD(bar.X + metrics.TrackMarginAlong, bar.Y + metrics.TrackMarginAcross,
                Math.Max(0.0, bar.Width - (2 * metrics.TrackMarginAlong)), Math.Max(0.0, bar.Height - (2 * metrics.TrackMarginAcross)));
        var trackLength = vertical ? track.Height : track.Width;
        var range = Math.Max(0.0, maxOffset);
        var clampedOffset = Math.Min(range, Math.Max(0.0, double.IsNaN(offset) ? 0 : offset));

        // Track.ComputeScrollBarLengths: ViewportSize / (Maximum - Minimum + ViewportSize).
        var viewport = Sanitize(viewportLength);
        var total = range + viewport;
        var thumbLength = total > 0 ? trackLength * viewport / total : trackLength;
        thumbLength = Math.Min(Math.Max(0.0, thumbLength), trackLength);
        thumbLength = Math.Max(metrics.MinThumbLength, thumbLength);
        var thumbVisible = trackLength > metrics.MinThumbLength || (trackLength >= thumbLength && trackLength > 0);
        if (!thumbVisible)
        {
            return new ScrollBarBox(axis, true, bar, track, default, false, range, clampedOffset, viewport);
        }

        thumbLength = Math.Min(thumbLength, trackLength);
        var travel = trackLength - thumbLength;
        var thumbStart = (vertical ? track.Y : track.X) + (range > 0 ? travel * clampedOffset / range : 0);
        var thumb = vertical
            ? new RectD(track.X, thumbStart, track.Width, thumbLength)
            : new RectD(thumbStart, track.Y, thumbLength, track.Height);
        return new ScrollBarBox(axis, true, bar, track, thumb, true, range, clampedOffset, viewport);
    }

    /// <summary>
    /// Offset ứng với con trượt bắt đầu ở <paramref name="thumbStart"/> (toạ độ client theo trục), kẹp vào [0, max]:
    /// <c>Track.ValueFromDistance</c> cộng dồn, tương đương ánh xạ tuyến tính thế này vì con trượt luôn bám con trỏ.
    /// </summary>
    public static double OffsetForThumbStart(in ScrollBarBox box, double thumbStart)
    {
        var travel = box.Travel;
        if (!(travel > 0) || !(box.MaxOffset > 0))
        {
            return 0;
        }

        var ratio = (thumbStart - box.TrackStart) / travel;
        return Math.Min(box.MaxOffset, Math.Max(0.0, ratio * box.MaxOffset));
    }

    /// <summary>Phần của thanh cuộn tại điểm (toạ độ client); <see cref="ScrollBarPart.None"/> khi ngoài các thanh.</summary>
    public static ScrollBarPart HitTest(in ScrollBarLayout layout, PointD point)
    {
        if (layout.HasCorner && Inside(layout.Corner, point))
        {
            return ScrollBarPart.Corner;
        }

        foreach (var box in new[] { layout.Vertical, layout.Horizontal })
        {
            if (!box.Visible || !Inside(box.Bar, point))
            {
                continue;
            }

            if (box.ThumbVisible && Inside(box.Thumb, point))
            {
                return ScrollBarPart.Thumb;
            }

            if (Inside(box.Track, point))
            {
                var along = box.Along(point);
                if (!box.ThumbVisible)
                {
                    return ScrollBarPart.Background;
                }

                return along < box.ThumbStart ? ScrollBarPart.TrackBefore : ScrollBarPart.TrackAfter;
            }

            return ScrollBarPart.Background;
        }

        return ScrollBarPart.None;
    }

    /// <summary>Trục của thanh cuộn chứa điểm (kể cả ô góc: dọc).</summary>
    public static ScrollAxis? AxisAt(in ScrollBarLayout layout, PointD point)
    {
        if (layout.Vertical.Visible && Inside(layout.Vertical.Bar, point))
        {
            return ScrollAxis.Vertical;
        }

        return layout.Horizontal.Visible && Inside(layout.Horizontal.Bar, point) ? ScrollAxis.Horizontal : null;
    }

    /// <summary>Độ dài DIP hợp lệ: NaN, âm hoặc vô hạn -> 0 (như <c>ViewportLayoutMath.SanitizeLength</c> của engine).</summary>
    private static double Sanitize(double value) => double.IsFinite(value) && value > 0 ? value : 0;

    /// <summary>Nửa mở: biên trái/trên thuộc hình, phải/dưới thì không (hai hình kề nhau không cùng chứa một điểm).</summary>
    internal static bool Inside(RectD rect, PointD point) =>
        rect.Width > 0 && rect.Height > 0 && point.X >= rect.X && point.X < rect.X + rect.Width && point.Y >= rect.Y && point.Y < rect.Y + rect.Height;
}
