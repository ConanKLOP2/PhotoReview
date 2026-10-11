using System.Diagnostics;
using PhotoReview.App.Input;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Input;
using PhotoReview.Shell.Win32.Overlay;

namespace PhotoReview.Shell.Win32.Viewing;

/// <summary>Màu của con trượt theo trạng thái (<c>Dark.ScrollThumb</c> của <c>DarkScrollBars.xaml</c>).</summary>
internal static class ScrollBarColors
{
    /// <summary>Nền thanh cuộn và ô góc: <c>Dark.ScrollTrack</c>.</summary>
    public static ColorF Track => DarkPalette.Get("Dark.ScrollTrack");

    /// <summary><c>Border Background="#4A4A4A"</c>.</summary>
    public static ColorF Thumb { get; } = DarkPalette.FromArgb(0xFF4A4A4A);

    /// <summary><c>IsMouseOver</c>: <c>#6A6A6A</c>.</summary>
    public static ColorF ThumbHover { get; } = DarkPalette.FromArgb(0xFF6A6A6A);

    /// <summary><c>IsDragging</c>: <c>#7A7A7A</c>.</summary>
    public static ColorF ThumbPressed { get; } = DarkPalette.FromArgb(0xFF7A7A7A);
}

/// <summary>Tuỳ chọn của <see cref="ShellScrollBars"/> (mặc định = chạy thật; test đổi từng cái).</summary>
internal sealed class ScrollBarOptions
{
    /// <summary>Số đo (DIP); mặc định = <c>DarkScrollBars.xaml</c> và <see cref="ViewportControllerOptions.ScrollBarThickness"/>.</summary>
    public ScrollBarMetrics Metrics { get; init; } = new ScrollBarMetrics();

    /// <summary>Trễ lần lặp đầu / khoảng lặp (ms) của nhấn-giữ trên rãnh (<c>RepeatButton</c>); mặc định theo cài đặt bàn phím.</summary>
    public Func<(int DelayMs, int IntervalMs)> RepeatTiming { get; init; } = ScrollBarNative.RepeatTiming;

    /// <summary>Xin WM_MOUSELEAVE cho cửa sổ (để bỏ trạng thái hover khi con trỏ rời cửa sổ); test thay bằng bản ghi.</summary>
    public Action<nint> TrackMouseLeave { get; init; } = ScrollBarNative.TrackMouseLeave;

    /// <summary>Số tick mỗi giây của <see cref="ViewportController.Timestamp"/>.</summary>
    public long TicksPerSecond { get; init; } = Stopwatch.Frequency;
}

/// <summary>
/// WP-26 (NE-3 a): thanh cuộn dọc/ngang tự vẽ của cửa sổ chính shell, thay <c>ScrollBar</c> trong template
/// <c>DarkScrollBars.xaml</c> của <c>ImageScroll</c>. Chiếm chỗ như WPF (engine viewport WP-16 đã trừ độ dày vào vùng nhìn), vẽ
/// nền <c>Dark.ScrollTrack</c> + con trượt bo 4 DIP (màu theo hover/kéo) + ô góc, và xử lý chuột:
/// <list type="bullet">
/// <item>Kéo con trượt: offset tỉ lệ vị trí con trượt trong rãnh, con trượt bám con trỏ (như <c>Track.ValueFromDistance</c>),
/// kẹp [0, max]; capture chuột tới khi nhả.</item>
/// <item>Nhấn rãnh: một trang (= vùng nhìn, <c>ScrollViewer.PageUp/PageDown/PageLeft/PageRight</c>), rồi lặp theo nhịp bàn phím của
/// hệ thống khi giữ, dừng khi con trượt tới con trỏ (nút rãnh biến mất dưới con trỏ).</item>
/// <item>Mọi nhấn trái trên thanh cuộn/ô góc bị nuốt (không bao giờ thành pan/zoom của ảnh nằm dưới); wheel/touchpad vẫn qua
/// <c>ShellInput</c> (WP-22).</item>
/// </list>
/// Phải đăng ký TRƯỚC <c>ShellInput</c> (handler đăng ký trước thấy chuột trước): ảnh đang zoom có hộp phủ cả vùng thanh cuộn nên
/// hit-test của ảnh sẽ nuốt nhấn. Chỉ dùng trên UI thread.
/// </summary>
internal sealed class ShellScrollBars : IWindowMessageHandler, IDisposable
{
    private readonly IShellWindow _window;
    private readonly ViewportController _viewport;
    private readonly Func<bool> _isCompareVisible;
    private readonly Action? _onPress;
    private readonly ScrollBarOptions _options;
    private readonly EventHandler _onFrame;

    private bool _pointerInside;
    private bool _tracking;
    private PointD _pointer;

    private bool _dragging;
    private ScrollAxis _dragAxis;
    private double _dragGrab;

    private bool _paging;
    private ScrollAxis _pageAxis;
    private int _pageDirection;
    private bool _pageRegionActive;
    private long _nextRepeatTicks;
    private bool _frameHooked;
    private bool _disposed;

    public ShellScrollBars(IShellWindow window, ViewportController viewport, Func<bool> isCompareVisible, Action? onPress = null,
        ScrollBarOptions? options = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        _isCompareVisible = isCompareVisible ?? throw new ArgumentNullException(nameof(isCompareVisible));
        _onPress = onPress;
        _options = options ?? new ScrollBarOptions();
        _onFrame = OnFrame;
        _window.AddMessageHandler(this);
    }

    /// <summary>Đang kéo con trượt.</summary>
    public bool IsDragging => _dragging;

    /// <summary>Đang giữ chuột trên rãnh (lặp trang).</summary>
    public bool IsPaging => _paging;

    /// <summary>Số khung đã vẽ thanh cuộn (khung không có thanh nào không tính): test/chẩn đoán.</summary>
    internal long DrawnFrames { get; private set; }

    /// <summary>Hình học hiện tại (theo layout và offset của <see cref="ViewportController"/>).</summary>
    public ScrollBarLayout CurrentLayout => ScrollBarGeometry.Compute(_window.ClientSizeDip, _viewport.Layout,
        _viewport.HorizontalOffset, _viewport.VerticalOffset, _options.Metrics);

    /// <summary>
    /// Phần của thanh cuộn thấy được tại điểm (client DIP): <c>ComparePanel</c> (lề 8, nền canvas) che ImageScroll nên phần
    /// thanh cuộn nằm dưới nó không nhận chuột.
    /// </summary>
    public ScrollBarPart HitTest(PointD point) => HitTest(CurrentLayout, point);

    private ScrollBarPart HitTest(in ScrollBarLayout layout, PointD point)
    {
        if (_isCompareVisible() && ScrollBarGeometry.Inside(CompareLayout.Panel(_window.ClientSizeDip), point))
        {
            return ScrollBarPart.None;
        }

        return ScrollBarGeometry.HitTest(layout, point);
    }

    /// <summary>Trục mà con trỏ đang ở trên con trượt (hover), null khi không.</summary>
    internal ScrollAxis? HoveredThumbAxis
    {
        get
        {
            if (_dragging || _paging || !_pointerInside)
            {
                return null;
            }

            var layout = CurrentLayout;
            if (HitTest(layout, _pointer) != ScrollBarPart.Thumb)
            {
                return null;
            }

            return ScrollBarGeometry.AxisAt(layout, _pointer);
        }
    }

    // ---- thông điệp ----

    public bool TryHandle(in WindowMessage message, out nint result)
    {
        result = 0;
        switch (message.Msg)
        {
            case WindowMessages.WmLButtonDown:
            case WindowMessages.WmLButtonDblClk:
                return OnLeftDown(InputNative.SignedLowWord(message.LParam), InputNative.SignedHighWord(message.LParam));
            case WindowMessages.WmMouseMove:
                OnMove(InputNative.SignedLowWord(message.LParam), InputNative.SignedHighWord(message.LParam));
                return false;
            case WindowMessages.WmLButtonUp:
                return OnLeftUp();
            case WindowMessages.WmCaptureChanged:
                // Mất capture (kể cả do hệ thống): kết thúc kéo/lặp, không nhả capture lần nữa.
                EndInteraction(releaseCapture: false);
                return false;
            case ScrollBarNative.WmCancelMode:
                EndInteraction(releaseCapture: true);
                return false;
            case ScrollBarNative.WmMouseLeave:
                OnLeave();
                return false;
            default:
                return false;
        }
    }

    private PointD ToDip(int pixelX, int pixelY)
    {
        var scale = ViewportController.NormalizedDpi(_window.DpiScale);
        return new PointD(pixelX / scale, pixelY / scale);
    }

    private bool OnLeftDown(int pixelX, int pixelY)
    {
        var point = ToDip(pixelX, pixelY);
        var layout = CurrentLayout;
        var part = HitTest(layout, point);
        if (part == ScrollBarPart.None)
        {
            return false;
        }

        _pointer = point;
        _pointerInside = true;
        EndInteraction(releaseCapture: false); // một lần nhấn đứt dở trước đó (mất sự kiện nhả)
        _onPress?.Invoke(); // Window_PreviewMouseDown của WPF chạy trước cho mọi nhấn: dừng đà kinetic

        var axis = ScrollBarGeometry.AxisAt(layout, point);
        switch (part)
        {
            case ScrollBarPart.Thumb when axis is { } thumbAxis:
                _dragging = true;
                _dragAxis = thumbAxis;
                _dragGrab = layout.Box(thumbAxis).Along(point) - layout.Box(thumbAxis).ThumbStart;
                _window.CapturePointer();
                _window.Invalidate();
                break;
            case ScrollBarPart.TrackBefore or ScrollBarPart.TrackAfter when axis is { } pageAxis:
                _pageAxis = pageAxis;
                _pageDirection = part == ScrollBarPart.TrackBefore ? -1 : 1;
                _paging = true;
                _pageRegionActive = true;
                _window.CapturePointer();
                PageOnce();
                _nextRepeatTicks = _viewport.Timestamp + MillisecondsToTicks(_options.RepeatTiming().DelayMs);
                HookFrames();
                _window.Invalidate();
                break;
        }

        return true; // thanh cuộn/ô góc nằm trên ảnh: nuốt mọi nhấn trái
    }

    private void OnMove(int pixelX, int pixelY)
    {
        var point = ToDip(pixelX, pixelY);
        var hoverBefore = HoveredThumbAxis;
        _pointer = point;
        _pointerInside = true;
        if (!_tracking && _window.Hwnd != 0)
        {
            _tracking = true;
            _options.TrackMouseLeave(_window.Hwnd);
        }

        if (_dragging)
        {
            DragTo(point);
            return;
        }

        if (_paging)
        {
            var layout = CurrentLayout;
            var overRegion = IsOverPageRegion(layout, point);
            if (overRegion && !_pageRegionActive)
            {
                // RepeatButton: vào lại khi còn giữ chuột bắt đầu bộ đếm lại (không lật trang ngay).
                _nextRepeatTicks = _viewport.Timestamp + MillisecondsToTicks(_options.RepeatTiming().DelayMs);
            }

            _pageRegionActive = overRegion;
            return;
        }

        if (HoveredThumbAxis != hoverBefore)
        {
            _window.Invalidate();
        }
    }

    private bool OnLeftUp()
    {
        if (!_dragging && !_paging)
        {
            return false;
        }

        EndInteraction(releaseCapture: true);
        return true;
    }

    private void OnLeave()
    {
        _tracking = false;
        var hadHover = HoveredThumbAxis is not null;
        _pointerInside = false;
        if (hadHover)
        {
            _window.Invalidate();
        }
    }

    // ---- kéo con trượt ----

    private void DragTo(PointD point)
    {
        var box = CurrentLayout.Box(_dragAxis);
        if (!box.Visible)
        {
            return; // layout đổi giữa chừng (Fit...): không còn gì để kéo
        }

        var offset = ScrollBarGeometry.OffsetForThumbStart(box, box.Along(point) - _dragGrab);
        ApplyAxisOffset(_dragAxis, offset);
    }

    private void ApplyAxisOffset(ScrollAxis axis, double offset)
    {
        var horizontal = _viewport.HorizontalOffset;
        var vertical = _viewport.VerticalOffset;
        if (axis == ScrollAxis.Vertical)
        {
            vertical = offset;
        }
        else
        {
            horizontal = offset;
        }

        _viewport.ScrollTo(horizontal, vertical);
        _viewport.UpdateLayout();
        _window.Invalidate();
    }

    // ---- nhấn rãnh: trang + lặp ----

    private void PageOnce()
    {
        var box = CurrentLayout.Box(_pageAxis);
        if (!box.Visible)
        {
            return;
        }

        var current = _pageAxis == ScrollAxis.Vertical ? _viewport.VerticalOffset : _viewport.HorizontalOffset;
        ApplyAxisOffset(_pageAxis, current + (_pageDirection * box.ViewportLength));
    }

    private bool IsOverPageRegion(in ScrollBarLayout layout, PointD point)
    {
        var part = HitTest(layout, point);
        var wanted = _pageDirection < 0 ? ScrollBarPart.TrackBefore : ScrollBarPart.TrackAfter;
        return part == wanted && ScrollBarGeometry.AxisAt(layout, point) == _pageAxis;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!_paging)
        {
            UnhookFrames();
            return;
        }

        var now = _viewport.Timestamp;
        if (!_pageRegionActive || now < _nextRepeatTicks)
        {
            return;
        }

        // Nút rãnh biến mất dưới con trỏ (con trượt tới nơi) -> RepeatButton thôi lặp.
        if (!IsOverPageRegion(CurrentLayout, _pointer))
        {
            _pageRegionActive = false;
            return;
        }

        PageOnce();
        _nextRepeatTicks = now + MillisecondsToTicks(_options.RepeatTiming().IntervalMs);
    }

    private long MillisecondsToTicks(int milliseconds) => Math.Max(0, milliseconds) * _options.TicksPerSecond / 1000;

    private void HookFrames()
    {
        if (_frameHooked)
        {
            return;
        }

        _frameHooked = true;
        _viewport.HookRenderFrame(_onFrame);
    }

    private void UnhookFrames()
    {
        if (!_frameHooked)
        {
            return;
        }

        _frameHooked = false;
        _viewport.UnhookRenderFrame(_onFrame);
    }

    private void EndInteraction(bool releaseCapture)
    {
        var wasActive = _dragging || _paging;
        _dragging = false;
        _paging = false;
        _pageRegionActive = false;
        UnhookFrames();
        if (!wasActive)
        {
            return;
        }

        if (releaseCapture && _window.HasPointerCapture)
        {
            _window.ReleasePointer();
        }

        _window.Invalidate();
    }

    // ---- vẽ ----

    /// <summary>
    /// Vẽ nền thanh cuộn, con trượt và ô góc lên <paramref name="context"/> (sau ảnh, trước overlay). Khi so sánh đang hiện,
    /// <c>ComparePanel</c> (lề 8 DIP, nền canvas) phủ lên: chỉ phần thanh cuộn ngoài panel (dải 8 DIP bên phải/dưới) còn thấy.
    /// </summary>
    public void Draw(IDrawContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var layout = CurrentLayout;
        if (!layout.AnyVisible)
        {
            return;
        }

        DrawnFrames++;
        if (!_isCompareVisible())
        {
            DrawBars(context, layout);
            return;
        }

        var client = _window.ClientSizeDip;
        var panel = CompareLayout.Panel(client);
        var right = new RectD(panel.Right, 0, Math.Max(0, client.Width - panel.Right), client.Height);
        var bottom = new RectD(0, panel.Bottom, client.Width, Math.Max(0, client.Height - panel.Bottom));
        foreach (var strip in new[] { right, bottom })
        {
            if (strip.Width <= 0 || strip.Height <= 0)
            {
                continue;
            }

            context.PushClip(strip);
            try
            {
                DrawBars(context, layout);
            }
            finally
            {
                context.PopClip();
            }
        }
    }

    private void DrawBars(IDrawContext context, in ScrollBarLayout layout)
    {
        var hovered = HoveredThumbAxis;
        foreach (var box in new[] { layout.Vertical, layout.Horizontal })
        {
            if (!box.Visible)
            {
                continue;
            }

            context.FillRectangle(box.Bar, ScrollBarColors.Track);
            if (!box.ThumbVisible)
            {
                continue;
            }

            var pressed = _dragging && _dragAxis == box.Axis;
            var color = pressed ? ScrollBarColors.ThumbPressed : hovered == box.Axis ? ScrollBarColors.ThumbHover : ScrollBarColors.Thumb;
            context.FillRoundedRectangle(box.Thumb, _options.Metrics.ThumbRadius, color);
        }

        if (layout.HasCorner)
        {
            context.FillRectangle(layout.Corner, ScrollBarColors.Track);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dragging = false;
        _paging = false;
        UnhookFrames();
    }
}

