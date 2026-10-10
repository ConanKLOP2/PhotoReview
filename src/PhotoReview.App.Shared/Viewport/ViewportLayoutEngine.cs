using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;

namespace PhotoReview.App.Viewport;

/// <summary>
/// WP-16 (C-08): phần thực thi của engine viewport thuần. Tái tạo đúng các bước layout WPF mà <c>MainWindow.ImageScroll</c>
/// (ScrollViewer Auto/Auto, template <c>Themes/DarkScrollBars.xaml</c>: lưới 2x2, ScrollContentPresenter ở ô *, thanh cuộn ở
/// cột/hàng Auto) và <c>MainImage</c> (Image, Stretch qua <c>ViewerStretchModeConverter</c>: Uniform -> Uniform, None -> Fill)
/// đang chạy - không có UseLayoutRounding (cửa sổ không bật), nên mọi số là DIP thực, không làm tròn:
/// <list type="number">
/// <item>Đo phần tử ảnh (FrameworkElement.MeasureCore + Image.MeasureOverride): ScrollContentPresenter đo con với kích thước
/// vô hạn ở cả hai trục (cuộn được hai chiều), MinMax kẹp theo Width/Height/MaxWidth/MaxHeight; kết quả là Extent.</item>
/// <item>Chọn thanh cuộn (ScrollViewer.MeasureOverride, chính sách Auto): mỗi lượt đo bắt đầu với cả hai thanh ẩn; lượt 1 so
/// extent với toàn vùng client; lượt 2 đo lại khi có thanh hiện; lượt 3 chỉ khi đúng MỘT thanh hiện - thanh kia được kiểm lại
/// với viewport đã hẹp. Tối đa 3 lần đo như WPF. So sánh dùng <c>DoubleUtil.GreaterThan</c> (sai số tương đối DBL_EPSILON).</item>
/// <item>Sắp xếp (ScrollContentPresenter.ArrangeOverride + FrameworkElement.ArrangeCore + Image.ArrangeOverride): ô của ảnh
/// = max(extent, viewport); kích thước vẽ tính lại từ ô đã kẹp MinMax; ảnh nhỏ hơn ô thì căn giữa (Stretch được coi như Center),
/// lớn hơn thì ghim trái/trên.</item>
/// </list>
/// Offset tối đa = max(0, extent - viewport) (= ScrollViewer.ScrollableWidth/Height). Engine không giữ trạng thái; offset được
/// giữ ở <see cref="ViewportState"/>.
/// </summary>
public static partial class ViewportLayoutEngine
{
    public static partial ViewportLayout Compute(in ViewportInput input)
    {
        var clientWidth = ViewportLayoutMath.SanitizeLength(input.ClientWidth);
        var clientHeight = ViewportLayoutMath.SanitizeLength(input.ClientHeight);
        var element = ViewportLayoutMath.ImageElement.From(input);
        var desired = element.Measure();

        var (horizontalBar, verticalBar) = ViewportLayoutMath.ResolveBars(input.ScrollBars, desired.Width, desired.Height, clientWidth, clientHeight,
            ViewportLayoutMath.SanitizeLength(input.ScrollBarThickness), out var viewportWidth, out var viewportHeight);

        var imageRect = element.Arrange(desired, viewportWidth, viewportHeight);
        return new ViewportLayout(
            viewportWidth, viewportHeight,
            desired.Width, desired.Height,
            horizontalBar, verticalBar,
            imageRect,
            ViewportLayoutMath.ScrollableLength(desired.Width, viewportWidth),
            ViewportLayoutMath.ScrollableLength(desired.Height, viewportHeight));
    }

    /// <summary>
    /// ScrollContentPresenter.CoerceOffset: trước kẹp trên (extent - viewport), sau kẹp dưới 0 - nên khi extent nhỏ hơn
    /// viewport offset là 0. NaN (WPF ném ArgumentOutOfRange ở ScrollToHorizontalOffset) được coi như 0 để không làm hỏng
    /// trạng thái UI.
    /// </summary>
    public static partial (double Horizontal, double Vertical) ClampOffset(in ViewportLayout layout, double horizontal, double vertical) =>
        (ViewportLayoutMath.CoerceOffset(horizontal, layout.ExtentWidth, layout.ViewportWidth), ViewportLayoutMath.CoerceOffset(vertical, layout.ExtentHeight, layout.ViewportHeight));
}

/// <summary>
/// WP-16: các bước tính của <see cref="ViewportLayoutEngine"/> (tách khỏi kiểu hợp đồng để bề mặt C-08 trong contracts.v1.txt
/// không đổi). Nội bộ, thuần.
/// </summary>
internal static class ViewportLayoutMath
{
    /// <summary>DoubleUtil.DBL_EPSILON của WPF (2^-52; khác <see cref="double.Epsilon"/> của .NET là số dương nhỏ nhất).</summary>
    internal const double WpfDoubleEpsilon = 2.2204460492503131e-016;

    internal static double CoerceOffset(double offset, double extent, double viewport)
    {
        if (double.IsNaN(offset)) return 0;
        if (offset > extent - viewport) offset = extent - viewport;
        if (offset < 0) offset = 0;
        return offset;
    }

    internal static double ScrollableLength(double extent, double viewport) => Math.Max(0.0, extent - viewport);

    /// <summary>
    /// Thanh cuộn nhìn thấy + kích thước viewport (ô * của lưới template trừ cột/hàng Auto của thanh đang hiện).
    /// <see cref="ScrollBarPolicy.Overlay"/>: thanh vẽ đè (không chiếm chỗ) và hiện khi nội dung tràn;
    /// <see cref="ScrollBarPolicy.Hidden"/>: không bao giờ hiện, vẫn cuộn được. Hai chính sách này chỉ có ở bản Win32 (NE-3).
    /// </summary>
    internal static (bool Horizontal, bool Vertical) ResolveBars(ScrollBarPolicy policy, double extentWidth, double extentHeight,
        double clientWidth, double clientHeight, double thickness, out double viewportWidth, out double viewportHeight)
    {
        bool horizontal, vertical;
        switch (policy)
        {
            case ScrollBarPolicy.Overlay:
                horizontal = GreaterThan(extentWidth, clientWidth);
                vertical = GreaterThan(extentHeight, clientHeight);
                viewportWidth = clientWidth;
                viewportHeight = clientHeight;
                return (horizontal, vertical);
            case ScrollBarPolicy.Hidden:
                viewportWidth = clientWidth;
                viewportHeight = clientHeight;
                return (false, false);
        }

        // ScrollViewer.MeasureOverride, Auto/Auto. Lượt đo 1: cả hai thanh Collapsed -> viewport = toàn client.
        horizontal = GreaterThan(extentWidth, clientWidth);
        vertical = GreaterThan(extentHeight, clientHeight);
        // Lượt đo 2 (khi có thanh hiện) chỉ làm hẹp viewport; extent không phụ thuộc viewport (con được đo với vô hạn).
        // Lượt đo 3: "appearance of one scrollbar may cause appearance of another" - chỉ khi đúng một thanh đã hiện.
        if (horizontal != vertical)
        {
            if (!horizontal) horizontal = GreaterThan(extentWidth, BarredLength(clientWidth, thickness, true));
            else vertical = GreaterThan(extentHeight, BarredLength(clientHeight, thickness, true));
        }

        viewportWidth = BarredLength(clientWidth, thickness, vertical);
        viewportHeight = BarredLength(clientHeight, thickness, horizontal);
        return (horizontal, vertical);
    }

    /// <summary>Ô * của lưới: phần còn lại sau cột/hàng Auto (độ dày thanh khi thanh hiện), không âm.</summary>
    internal static double BarredLength(double client, double thickness, bool barVisible) =>
        barVisible ? Math.Max(0.0, client - thickness) : client;

    /// <summary>DoubleUtil.GreaterThan của WPF: lớn hơn và không "gần bằng".</summary>
    internal static bool GreaterThan(double value1, double value2) => value1 > value2 && !AreClose(value1, value2);

    /// <summary>DoubleUtil.AreClose của WPF.</summary>
    internal static bool AreClose(double value1, double value2)
    {
        if (value1 == value2) return true;
        var eps = (Math.Abs(value1) + Math.Abs(value2) + 10.0) * WpfDoubleEpsilon;
        var delta = value1 - value2;
        return -eps < delta && eps > delta;
    }

    /// <summary>DoubleUtil.IsZero của WPF.</summary>
    internal static bool IsZero(double value) => Math.Abs(value) < 10.0 * WpfDoubleEpsilon;

    /// <summary>Độ dài DIP hợp lệ: NaN, âm hoặc vô hạn -> 0 (client/thanh cuộn chưa đo).</summary>
    internal static double SanitizeLength(double value) => double.IsFinite(value) && value > 0 ? value : 0;

    /// <summary>
    /// Phần tử Image với các thuộc tính đã chuẩn hoá theo luật kiểm tra của WPF: Width/Height chỉ NaN hoặc hữu hạn &gt;= 0
    /// (ngoài ra coi như NaN = auto), MaxWidth/MaxHeight không NaN và &gt;= 0 (NaN coi như vô hạn), kích thước tự nhiên của
    /// bitmap hữu hạn &gt;= 0 (không có bitmap = 0x0).
    /// </summary>
    internal readonly record struct ImageElement(bool Uniform, double Width, double Height, double MaxWidth, double MaxHeight,
        double NaturalWidth, double NaturalHeight)
    {
        public static ImageElement From(in ViewportInput input) => new(
            input.Stretch == ViewerStretchMode.Uniform,
            SanitizeExplicit(input.ImageWidth), SanitizeExplicit(input.ImageHeight),
            SanitizeMax(input.MaxImageWidth), SanitizeMax(input.MaxImageHeight),
            SanitizeLength(input.BitmapWidth), SanitizeLength(input.BitmapHeight));

        private static double SanitizeExplicit(double value) => double.IsFinite(value) && value >= 0 ? value : double.NaN;

        private static double SanitizeMax(double value) => double.IsNaN(value) || value < 0 ? double.PositiveInfinity : value;

        // FrameworkElement.MinMax (MinWidth/MinHeight = 0).
        public double MaxW => Math.Max(Math.Min(double.IsNaN(Width) ? double.PositiveInfinity : Width, MaxWidth), 0);

        public double MaxH => Math.Max(Math.Min(double.IsNaN(Height) ? double.PositiveInfinity : Height, MaxHeight), 0);

        public double MinW => Math.Max(Math.Min(MaxW, double.IsNaN(Width) ? 0 : Width), 0);

        public double MinH => Math.Max(Math.Min(MaxH, double.IsNaN(Height) ? 0 : Height), 0);

        /// <summary>
        /// FrameworkElement.MeasureCore với availableSize vô hạn (ScrollContentPresenter): DesiredSize sau khi kẹp Max
        /// (= Extent) và bản chưa kẹp (UnclippedDesiredSize, dùng lúc sắp xếp).
        /// </summary>
        public MeasuredSize Measure()
        {
            // max(Min, min(vô hạn, Max)) = Max vì Min <= Max.
            var (overrideWidth, overrideHeight) = StretchToBox(MaxW, MaxH);
            var unclippedWidth = Math.Max(overrideWidth, MinW);
            var unclippedHeight = Math.Max(overrideHeight, MinH);
            return new MeasuredSize(
                Math.Min(unclippedWidth, MaxW), Math.Min(unclippedHeight, MaxH),
                unclippedWidth, unclippedHeight);
        }

        /// <summary>
        /// ScrollContentPresenter.ArrangeOverride (ô = max(DesiredSize, viewport)) + FrameworkElement.ArrangeCore (kẹp MinMax,
        /// căn) + Image.ArrangeOverride. Trả về vị trí/kích thước vẽ của ảnh trong toạ độ extent (trước khi trừ offset).
        /// </summary>
        public RectD Arrange(MeasuredSize measured, double viewportWidth, double viewportHeight)
        {
            var slotWidth = Math.Max(measured.Width, viewportWidth);
            var slotHeight = Math.Max(measured.Height, viewportHeight);
            var arrangeWidth = ArrangeLength(slotWidth, measured.UnclippedWidth, MaxW);
            var arrangeHeight = ArrangeLength(slotHeight, measured.UnclippedHeight, MaxH);
            var (renderWidth, renderHeight) = StretchToBox(arrangeWidth, arrangeHeight);
            return new RectD(
                AlignmentOffset(slotWidth, Math.Min(renderWidth, MaxW)),
                AlignmentOffset(slotHeight, Math.Min(renderHeight, MaxH)),
                renderWidth, renderHeight);
        }

        private static double ArrangeLength(double slot, double unclipped, double max)
        {
            var length = slot;
            if (length < unclipped) length = unclipped;
            var effectiveMax = Math.Max(unclipped, max);
            if (effectiveMax < length) length = effectiveMax;
            return length;
        }

        /// <summary>FrameworkElement.ComputeAlignmentOffset với Stretch: ảnh lớn hơn ô -> Left/Top (0), ngược lại Center.</summary>
        private static double AlignmentOffset(double client, double ink) => ink > client ? 0 : (client - ink) * 0.5;

        /// <summary>Image.MeasureArrangeHelper + Viewbox.ComputeScaleFactor (StretchDirection.Both).</summary>
        private (double Width, double Height) StretchToBox(double boxWidth, double boxHeight)
        {
            var scaleX = 1.0;
            var scaleY = 1.0;
            var constrainedWidth = !double.IsPositiveInfinity(boxWidth);
            var constrainedHeight = !double.IsPositiveInfinity(boxHeight);
            if (constrainedWidth || constrainedHeight)
            {
                scaleX = IsZero(NaturalWidth) ? 0.0 : boxWidth / NaturalWidth;
                scaleY = IsZero(NaturalHeight) ? 0.0 : boxHeight / NaturalHeight;
                if (!constrainedWidth) scaleX = scaleY;
                else if (!constrainedHeight) scaleY = scaleX;
                else if (Uniform) scaleX = scaleY = Math.Min(scaleX, scaleY);
            }
            return (NaturalWidth * scaleX, NaturalHeight * scaleY);
        }
    }

    internal readonly record struct MeasuredSize(double Width, double Height, double UnclippedWidth, double UnclippedHeight);
}
