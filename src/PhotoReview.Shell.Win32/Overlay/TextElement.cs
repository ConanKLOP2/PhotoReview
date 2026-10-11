using PhotoReview.App.Input;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>
/// WP-17: <c>TextBlock</c> - một chuỗi, một style, màu; <see cref="TextTrimming.CharacterEllipsis"/> cắt "…" khi vượt chỗ
/// cho phép. Giữ lease layout tới khi chuỗi/style/chiều rộng đổi hoặc <see cref="ReleaseResources"/>.
/// </summary>
public class TextElement : OverlayElement
{
    private string _text = string.Empty;
    private TextStyle _style = new();
    private TextTrimming _trimming;
    private ITextLayout? _layout;
    private ITextRenderer? _layoutOwner;
    private double _layoutWidth = double.NaN;

    public TextElement(string id)
        : base(id)
    {
        IsHitTestVisible = false;
    }

    public string Text
    {
        get => _text;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!string.Equals(_text, value, StringComparison.Ordinal))
            {
                _text = value;
                Invalidate();
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
                Invalidate();
            }
        }
    }

    public TextTrimming Trimming
    {
        get => _trimming;
        set
        {
            if (_trimming != value)
            {
                _trimming = value;
                Invalidate();
            }
        }
    }

    /// <summary>Alpha thẳng; mặc định <c>Dark.TextBright</c>.</summary>
    public ColorF Color { get; set; } = DarkPalette.TextBright;

    /// <summary>Layout hiện giữ (sau Measure); null khi chưa đo.</summary>
    internal ITextLayout? Layout => _layout;

    public override void ReleaseResources() => Invalidate();

    protected override SizeD MeasureCore(SizeD available, in OverlayLayoutContext context)
    {
        // maxWidth hữu hạn: chữ vượt thì cắt "…" (Trimming) hoặc ngắt dòng (None). Đo lại chỉ khi một trong các khoá đổi.
        double width = available.Width;
        if (_layout is null || !ReferenceEquals(_layoutOwner, context.Text) || _layoutWidth != width)
        {
            _layout?.Dispose();
            _layout = context.Text.CreateLayout(_text, _style, width, _trimming);
            _layoutOwner = context.Text;
            _layoutWidth = width;
        }

        return _layout.Size;
    }

    protected override void RenderCore(IDrawContext dc)
    {
        if (_layout is null || _text.Length == 0)
        {
            return;
        }

        dc.PushClip(Bounds);
        try
        {
            dc.DrawText(_layout, new PointD(Bounds.X, Bounds.Y), Color);
        }
        finally
        {
            dc.PopClip();
        }
    }

    private void Invalidate()
    {
        _layout?.Dispose();
        _layout = null;
        _layoutOwner = null;
        _layoutWidth = double.NaN;
    }
}
