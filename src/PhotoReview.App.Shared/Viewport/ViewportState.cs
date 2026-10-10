using PhotoReview.App.Input;

namespace PhotoReview.App.Viewport;

/// <summary>
/// WP-16: trạng thái của một viewport (thay phần trạng thái của ScrollViewer + ScrollContentPresenter): input layout hiện tại,
/// layout đã tính và offset. Thuần, không UI, một luồng (luồng UI của chủ sở hữu). Giữ đúng hai hành vi WPF mà
/// <c>PointerInputController</c>/<c>FitViewController</c> dựa vào:
/// <list type="bullet">
/// <item><b>Layout trễ:</b> đổi input hoặc <see cref="ScrollTo"/> không đổi ngay <see cref="Layout"/>/offset; chúng được áp ở
/// <see cref="UpdateLayout"/> (WPF: ScrollToHorizontalOffset xếp lệnh vào hàng đợi, offset/extent mới chỉ đọc được sau lượt layout
/// - <c>ScrollViewer.UpdateLayout()</c> hoặc lượt layout ở mức Render trước khi vẽ). Chủ sở hữu (ViewportController) xếp một lượt
/// layout ở <c>UiPriority.Render</c> mỗi khi <see cref="IsLayoutPending"/>.</item>
/// <item><b>Offset yêu cầu được nhớ:</b> ScrollContentPresenter giữ offset được yêu cầu (chỉ kẹp dưới 0) và chỉ kẹp trên khi tính
/// offset hiệu lực; nên khi viewport nở ra rồi co lại (resize) offset trở về vị trí cũ thay vì dính ở giá trị đã bị kẹp.</item>
/// </list>
/// </summary>
internal sealed class ViewportState
{
    private ViewportInput _input;
    private bool _hasInput;
    private bool _inputDirty = true;
    private bool _scrollPending;
    private double _requestedHorizontal;
    private double _requestedVertical;

    /// <summary>Input đang dùng (đã đặt bằng <see cref="SetInput"/>; có thể chưa được layout).</summary>
    public ViewportInput Input => _input;

    /// <summary>Layout của lượt <see cref="UpdateLayout"/> gần nhất.</summary>
    public ViewportLayout Layout { get; private set; }

    /// <summary>= ScrollViewer.HorizontalOffset (offset hiệu lực đã kẹp, sau lượt layout gần nhất).</summary>
    public double HorizontalOffset { get; private set; }

    /// <summary>= ScrollViewer.VerticalOffset.</summary>
    public double VerticalOffset { get; private set; }

    /// <summary>Có input hoặc yêu cầu cuộn chưa được layout.</summary>
    public bool IsLayoutPending => _inputDirty || _scrollPending;

    /// <summary>Đặt input layout (giống binding đổi Width/MaxWidth/Stretch/Source hoặc ScrollViewer đổi kích thước). Không tính ngay.</summary>
    public void SetInput(in ViewportInput input)
    {
        if (_hasInput && input.Equals(_input)) return;
        _input = input;
        _hasInput = true;
        _inputDirty = true;
    }

    /// <summary>
    /// ScrollToHorizontalOffset(<paramref name="horizontal"/>) rồi ScrollToVerticalOffset(<paramref name="vertical"/>): ghi yêu cầu
    /// (kẹp dưới 0 như ScrollContentPresenter.ValidateInputOffset), áp ở <see cref="UpdateLayout"/>. NaN bị bỏ qua cho trục đó
    /// (WPF ném ngoại lệ; ở đây không để một phép tính hỏng làm sập luồng UI).
    /// </summary>
    public void ScrollTo(double horizontal, double vertical)
    {
        if (!double.IsNaN(horizontal)) _requestedHorizontal = Math.Max(0.0, horizontal);
        if (!double.IsNaN(vertical)) _requestedVertical = Math.Max(0.0, vertical);
        _scrollPending = true;
    }

    /// <summary>ScrollToHome: cả hai offset về 0.</summary>
    public void ScrollHome() => ScrollTo(0, 0);

    /// <summary>
    /// Lượt layout: tính lại <see cref="Layout"/> nếu input đổi, rồi offset hiệu lực = offset yêu cầu kẹp vào [0, Max].
    /// Trả về true nếu layout hoặc offset nhìn thấy thay đổi (chủ sở hữu cần vẽ lại).
    /// </summary>
    public bool UpdateLayout()
    {
        if (!IsLayoutPending) return false;
        var previous = (Layout, HorizontalOffset, VerticalOffset);
        if (_inputDirty)
        {
            Layout = ViewportLayoutEngine.Compute(_input);
            _inputDirty = false;
        }
        _scrollPending = false;
        (HorizontalOffset, VerticalOffset) = ViewportLayoutEngine.ClampOffset(Layout, _requestedHorizontal, _requestedVertical);
        return previous != (Layout, HorizontalOffset, VerticalOffset);
    }

    /// <summary>Góc trên-trái phần tử ảnh trong toạ độ viewport (= image.TranslatePoint((0,0), scroll)).</summary>
    public PointD ImageOrigin => new(Layout.ImageRect.X - HorizontalOffset, Layout.ImageRect.Y - VerticalOffset);

    /// <summary>Điểm viewport -> toạ độ phần tử ảnh (= scroll.TranslatePoint(p, image)).</summary>
    public PointD ToImageElement(PointD viewportPoint)
    {
        var origin = ImageOrigin;
        return new PointD(viewportPoint.X - origin.X, viewportPoint.Y - origin.Y);
    }

    /// <summary>= Image.ActualWidth.</summary>
    public double ImageActualWidth => Layout.ImageRect.Width;

    /// <summary>= Image.ActualHeight.</summary>
    public double ImageActualHeight => Layout.ImageRect.Height;
}
