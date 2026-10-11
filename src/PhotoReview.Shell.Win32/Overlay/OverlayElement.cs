using PhotoReview.App.Input;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>Lề/đệm (DIP), thay <c>System.Windows.Thickness</c>.</summary>
public readonly record struct OverlayThickness(double Left, double Top, double Right, double Bottom)
{
    public OverlayThickness(double uniform)
        : this(uniform, uniform, uniform, uniform)
    {
    }

    public OverlayThickness(double horizontal, double vertical)
        : this(horizontal, vertical, horizontal, vertical)
    {
    }

    public double Horizontal => Left + Right;

    public double Vertical => Top + Bottom;

    public static OverlayThickness Zero => default;
}

public enum OverlayHorizontalAlignment
{
    Left = 0,
    Center = 1,
    Right = 2,
    Stretch = 3,
}

public enum OverlayVerticalAlignment
{
    Top = 0,
    Center = 1,
    Bottom = 2,
    Stretch = 3,
}

/// <summary>
/// WP-17 (C-11): gốc của phần tử overlay - mô hình bố cục hai bước như WPF (Measure rồi Arrange) rút gọn cho Border/StackPanel/
/// TextBlock/Button: lề ngoài (<see cref="Margin"/>), căn lề, <see cref="MaxWidth"/>, ẩn = collapsed. Mọi số là DIP; DPI chỉ
/// tới lớp vẽ (D2D SetDpi), nên bố cục KHÔNG phụ thuộc <see cref="OverlayLayoutContext.DpiScale"/> (WPF cũng đo theo DIP).
/// </summary>
public abstract class OverlayElement : IOverlayElement, IOpacityTarget
{
    private float _opacity = 1f;

    protected OverlayElement(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Id = id;
    }

    public string Id { get; }

    /// <summary>False = collapsed: không chiếm chỗ, không vẽ, không nhận chuột.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>0..1 (kẹp). Animator ghi qua <see cref="IOpacityTarget"/>.</summary>
    public float Opacity
    {
        get => _opacity;
        set => _opacity = float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, 1f);
    }

    public OverlayThickness Margin { get; set; }

    public OverlayHorizontalAlignment HorizontalAlignment { get; set; } = OverlayHorizontalAlignment.Stretch;

    public OverlayVerticalAlignment VerticalAlignment { get; set; } = OverlayVerticalAlignment.Stretch;

    /// <summary>Giới hạn rộng của hộp phần tử (không tính lề), như <c>FrameworkElement.MaxWidth</c>.</summary>
    public double MaxWidth { get; set; } = double.PositiveInfinity;

    /// <summary>False = click xuyên qua (như <c>IsHitTestVisible=false</c>); TextElement mặc định false.</summary>
    public virtual bool IsHitTestVisible { get; set; } = true;

    /// <summary>Hộp sau <see cref="ArrangeIn"/> (không gồm lề), toạ độ DIP trong cửa sổ.</summary>
    public RectD Bounds { get; private set; }

    /// <summary>Kích thước mong muốn (gồm lề) từ lần <see cref="Measure"/> gần nhất.</summary>
    public SizeD DesiredSize { get; private set; }

    /// <summary>Phần tử gốc: đo trong toàn bộ client rồi đặt theo căn lề.</summary>
    public void Arrange(in OverlayLayoutContext context)
    {
        Measure(context.Client, context);
        ArrangeIn(new RectD(0, 0, context.Client.Width, context.Client.Height), context);
    }

    /// <summary>Đo trong vùng <paramref name="available"/> (gồm lề); trả (và lưu) kích thước mong muốn gồm lề.</summary>
    public SizeD Measure(SizeD available, in OverlayLayoutContext context)
    {
        if (!IsVisible)
        {
            return DesiredSize = default;
        }

        double width = Math.Max(0, Math.Min(available.Width - Margin.Horizontal, MaxWidth));
        double height = Math.Max(0, available.Height - Margin.Vertical);
        SizeD content = MeasureCore(new SizeD(width, height), context);
        double w = Math.Min(content.Width, MaxWidth);
        return DesiredSize = new SizeD(w + Margin.Horizontal, content.Height + Margin.Vertical);
    }

    /// <summary>Đặt phần tử vào ô <paramref name="slot"/> (gồm lề) theo căn lề rồi đặt con.</summary>
    public void ArrangeIn(RectD slot, in OverlayLayoutContext context)
    {
        if (!IsVisible)
        {
            Bounds = new RectD(slot.X, slot.Y, 0, 0);
            return;
        }

        double innerX = slot.X + Margin.Left;
        double innerY = slot.Y + Margin.Top;
        double innerW = Math.Max(0, slot.Width - Margin.Horizontal);
        double innerH = Math.Max(0, slot.Height - Margin.Vertical);
        double desiredW = Math.Max(0, DesiredSize.Width - Margin.Horizontal);
        double desiredH = Math.Max(0, DesiredSize.Height - Margin.Vertical);

        double width = HorizontalAlignment == OverlayHorizontalAlignment.Stretch ? Math.Min(innerW, MaxWidth) : Math.Min(desiredW, innerW);
        double height = VerticalAlignment == OverlayVerticalAlignment.Stretch ? innerH : Math.Min(desiredH, innerH);
        double x = HorizontalAlignment switch
        {
            OverlayHorizontalAlignment.Right => innerX + innerW - width,
            OverlayHorizontalAlignment.Center => innerX + ((innerW - width) / 2),
            _ => innerX,
        };
        double y = VerticalAlignment switch
        {
            OverlayVerticalAlignment.Bottom => innerY + innerH - height,
            OverlayVerticalAlignment.Center => innerY + ((innerH - height) / 2),
            _ => innerY,
        };

        Bounds = new RectD(x, y, width, height);
        ArrangeChildren(context);
    }

    public void Render(IDrawContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        if (!IsVisible || Opacity <= 0f)
        {
            return;
        }

        dc.PushOpacity(Opacity);
        try
        {
            RenderCore(dc);
        }
        finally
        {
            dc.PopOpacity();
        }
    }

    /// <summary>Ẩn hoặc opacity 0 =&gt; false (click xuyên như <c>IsHitTestVisible=false</c>).</summary>
    public bool HitTest(PointD point) => IsVisible && IsHitTestVisible && Opacity > 0f && Bounds.Contains(point);

    /// <summary>Phần tử sâu nhất (con của panel trước) trúng <paramref name="point"/>, hoặc null.</summary>
    public virtual OverlayElement? FindAt(PointD point) => HitTest(point) ? this : null;

    /// <summary>Tìm theo <see cref="Id"/> trong cây (chính nó hoặc hậu duệ).</summary>
    public virtual OverlayElement? Find(string id) => string.Equals(Id, id, StringComparison.Ordinal) ? this : null;

    /// <summary>Kích thước nội dung (không lề) trong vùng cho phép; <c>available</c> đã trừ lề và kẹp MaxWidth.</summary>
    protected abstract SizeD MeasureCore(SizeD available, in OverlayLayoutContext context);

    protected virtual void ArrangeChildren(in OverlayLayoutContext context)
    {
    }

    protected abstract void RenderCore(IDrawContext dc);

    /// <summary>Trả tài nguyên (layout chữ) của phần tử và hậu duệ.</summary>
    public virtual void ReleaseResources()
    {
    }
}
