using PhotoReview.App.Input;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>
/// WP-17: nút của toolbar - nền theo trạng thái (Button/ButtonHover/ButtonPressed của <see cref="DarkPalette"/>), chữ giữa nút,
/// <see cref="Clicked"/> khi <see cref="Click"/> (host chuyển chuột trái nhả trong nút). Không tự bắt chuột: host đặt
/// <see cref="IsHot"/>/<see cref="IsPressed"/> (WP-23 nối vào con trỏ).
/// </summary>
public class ButtonElement : OverlayElement
{
    private string _content = string.Empty;
    private TextStyle _style = new();
    private ITextLayout? _layout;
    private ITextRenderer? _layoutOwner;

    public ButtonElement(string id)
        : base(id)
    {
        Padding = new OverlayThickness(8, 4);
    }

    public string Content
    {
        get => _content;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!string.Equals(_content, value, StringComparison.Ordinal))
            {
                _content = value;
                DropLayout();
            }
        }
    }

    public TextStyle Style
    {
        get => _style;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_style != value)
            {
                _style = value;
                DropLayout();
            }
        }
    }

    /// <summary>Đệm giữa viền và chữ (mặc định 8,4 như <c>Button</c> trong toolbar).</summary>
    public OverlayThickness Padding { get; set; }

    /// <summary>Viền 1 DIP của <c>Button</c> WPF (tính vào kích thước).</summary>
    public double BorderThickness { get; set; } = 1;

    public double CornerRadius { get; set; } = 2;

    public bool IsEnabled { get; set; } = true;

    public bool IsHot { get; set; }

    public bool IsPressed { get; set; }

    /// <summary>WP-23: bàn phím đang ở nút này (viền sáng như <c>IsKeyboardFocused</c> của DarkControls).</summary>
    public bool IsFocused { get; set; }

    /// <summary>WP-23: trạng thái bật của ToggleButton (nút Tools khi menu đang mở): nền như khi nhấn.</summary>
    public bool IsChecked { get; set; }

    public ColorF Foreground { get; set; } = DarkPalette.TextBright;

    /// <summary>WP-23: nền thường (mặc định <c>Dark.Button</c>; toolbar dùng <c>Dark.Input</c> như style của MainWindow.xaml).</summary>
    public ColorF Background { get; set; } = DarkPalette.Button;

    public ColorF BorderColor { get; set; } = DarkPalette.BorderStrong;

    /// <summary>WP-23: <c>Width</c> của XAML (ngoài, gồm viền); NaN = theo nội dung.</summary>
    public double FixedWidth { get; set; } = double.NaN;

    /// <summary>WP-23: <c>Height</c> của XAML; NaN = theo nội dung (co giãn theo hàng khi Stretch).</summary>
    public double FixedHeight { get; set; } = double.NaN;

    /// <summary>WP-23: chữ tooltip (<c>ToolTip</c>); rỗng = không tooltip.</summary>
    public string ToolTip { get; set; } = string.Empty;

    /// <summary>WP-23: <c>AutomationProperties.Name</c> (UIA do gói khác nối).</summary>
    public string AutomationName { get; set; } = string.Empty;

    public event EventHandler? Clicked;

    /// <summary>Phát <see cref="Clicked"/> khi nút bật, hiện và không mờ hẳn; trả true nếu đã phát.</summary>
    public bool Click()
    {
        if (!IsEnabled || !IsVisible || Opacity <= 0f)
        {
            return false;
        }

        Clicked?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public override void ReleaseResources() => DropLayout();

    protected override SizeD MeasureCore(SizeD available, in OverlayLayoutContext context)
    {
        double chrome = BorderThickness * 2;
        double textWidth = Math.Max(0, available.Width - Padding.Horizontal - chrome);
        EnsureLayout(context.Text, textWidth);
        SizeD text = _layout!.Size;
        double width = double.IsNaN(FixedWidth) ? text.Width + Padding.Horizontal + chrome : FixedWidth;
        double height = double.IsNaN(FixedHeight) ? text.Height + Padding.Vertical + chrome : FixedHeight;
        return new SizeD(width, height);
    }

    protected override void RenderCore(IDrawContext dc)
    {
        ColorF background = !IsEnabled ? Background
            : IsPressed || IsChecked ? DarkPalette.ButtonPressed
            : IsHot ? DarkPalette.ButtonHover
            : Background;
        if (CornerRadius > 0)
        {
            dc.FillRoundedRectangle(Bounds, CornerRadius, background);
        }
        else
        {
            dc.FillRectangle(Bounds, background);
        }

        if (BorderThickness > 0)
        {
            double half = BorderThickness / 2;
            var stroke = new RectD(Bounds.X + half, Bounds.Y + half, Math.Max(0, Bounds.Width - BorderThickness), Math.Max(0, Bounds.Height - BorderThickness));
            dc.DrawRectangle(stroke, IsFocused ? DarkPalette.Get("Dark.AccentHover") : BorderColor, BorderThickness);
        }

        if (_layout is null || _content.Length == 0)
        {
            return;
        }

        SizeD size = _layout.Size;
        double x = Bounds.X + ((Bounds.Width - size.Width) / 2);
        double y = Bounds.Y + ((Bounds.Height - size.Height) / 2);
        dc.PushClip(Bounds);
        try
        {
            ColorF color = IsEnabled ? Foreground : DarkPalette.Get("Dark.TextMuted");
            dc.DrawText(_layout, new PointD(x, y), color);
        }
        finally
        {
            dc.PopClip();
        }
    }

    private void EnsureLayout(ITextRenderer renderer, double width)
    {
        // Nút luôn một dòng không cắt: chiều rộng không đổi kết quả -> không đưa vào khoá, chỉ đo một lần cho tới khi đổi chữ.
        if (_layout is null || !ReferenceEquals(_layoutOwner, renderer))
        {
            _layout?.Dispose();
            _layout = renderer.CreateLayout(_content, _style, double.PositiveInfinity, TextTrimming.None);
            _layoutOwner = renderer;
        }
    }

    private void DropLayout()
    {
        _layout?.Dispose();
        _layout = null;
        _layoutOwner = null;
    }
}
