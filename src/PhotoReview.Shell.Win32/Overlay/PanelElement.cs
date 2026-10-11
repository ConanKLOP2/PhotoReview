using PhotoReview.App.Input;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Overlay;

public enum OverlayOrientation
{
    Vertical = 0,
    Horizontal = 1,
}

/// <summary>
/// WP-17: <c>Border</c> + <c>StackPanel</c> của WPF: nền bo góc, đệm, các con xếp dọc/ngang. Rộng theo con rộng nhất khi
/// căn Left/Right/Center (như <c>Border HorizontalAlignment=Left</c>), kéo giãn khi Stretch (ComparePanel).
/// </summary>
public class PanelElement : OverlayElement
{
    private readonly List<OverlayElement> _children = [];

    public PanelElement(string id)
        : base(id)
    {
    }

    public IReadOnlyList<OverlayElement> Children => _children;

    /// <summary>Alpha thẳng; mặc định <c>Dark.Overlay</c>.</summary>
    public ColorF Background { get; set; } = DarkPalette.Overlay;

    public double CornerRadius { get; set; }

    public OverlayThickness Padding { get; set; }

    public OverlayOrientation Orientation { get; set; } = OverlayOrientation.Vertical;

    public PanelElement Add(OverlayElement child)
    {
        ArgumentNullException.ThrowIfNull(child);
        _children.Add(child);
        return this;
    }

    public bool Remove(OverlayElement child)
    {
        ArgumentNullException.ThrowIfNull(child);
        return _children.Remove(child);
    }

    public override OverlayElement? FindAt(PointD point)
    {
        if (!HitTest(point))
        {
            return null;
        }

        for (int i = _children.Count - 1; i >= 0; i--)
        {
            if (_children[i].FindAt(point) is { } hit)
            {
                return hit;
            }
        }

        return this;
    }

    public override OverlayElement? Find(string id)
    {
        if (base.Find(id) is { } self)
        {
            return self;
        }

        foreach (OverlayElement child in _children)
        {
            if (child.Find(id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    public override void ReleaseResources()
    {
        foreach (OverlayElement child in _children)
        {
            child.ReleaseResources();
        }
    }

    protected override SizeD MeasureCore(SizeD available, in OverlayLayoutContext context)
    {
        double innerW = Math.Max(0, available.Width - Padding.Horizontal);
        double innerH = Math.Max(0, available.Height - Padding.Vertical);
        double width = 0;
        double height = 0;
        bool vertical = Orientation == OverlayOrientation.Vertical;
        foreach (OverlayElement child in _children)
        {
            SizeD size = child.Measure(new SizeD(vertical ? innerW : Math.Max(0, innerW - width), vertical ? Math.Max(0, innerH - height) : innerH), context);
            if (vertical)
            {
                width = Math.Max(width, size.Width);
                height += size.Height;
            }
            else
            {
                width += size.Width;
                height = Math.Max(height, size.Height);
            }
        }

        return new SizeD(width + Padding.Horizontal, height + Padding.Vertical);
    }

    protected override void ArrangeChildren(in OverlayLayoutContext context)
    {
        double x = Bounds.X + Padding.Left;
        double y = Bounds.Y + Padding.Top;
        double innerW = Math.Max(0, Bounds.Width - Padding.Horizontal);
        double innerH = Math.Max(0, Bounds.Height - Padding.Vertical);
        bool vertical = Orientation == OverlayOrientation.Vertical;
        foreach (OverlayElement child in _children)
        {
            if (!child.IsVisible)
            {
                child.ArrangeIn(new RectD(x, y, 0, 0), context);
                continue;
            }

            SizeD desired = child.DesiredSize;
            if (vertical)
            {
                child.ArrangeIn(new RectD(x, y, innerW, desired.Height), context);
                y += desired.Height;
            }
            else
            {
                child.ArrangeIn(new RectD(x, y, desired.Width, innerH), context);
                x += desired.Width;
            }
        }
    }

    protected override void RenderCore(IDrawContext dc)
    {
        if (Background.A > 0f && Bounds.Width > 0 && Bounds.Height > 0)
        {
            if (CornerRadius > 0)
            {
                dc.FillRoundedRectangle(Bounds, CornerRadius, Background);
            }
            else
            {
                dc.FillRectangle(Bounds, Background);
            }
        }

        foreach (OverlayElement child in _children)
        {
            child.Render(dc);
        }
    }
}
