using PhotoReview.App.Input;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>
/// WP-23: <c>Grid</c> một ô không nền của <c>MainWindow.xaml</c> (<c>InfoOverlayHost</c>): các con CHỒNG lên nhau, mỗi con tự
/// căn lề trong toàn bộ ô; vùng trống không nhận chuột (Grid không có Background). Opacity/IsHitTestVisible của lớp áp cho
/// mọi con - auto-hide của overlay thông tin chỉ chạm hai thuộc tính này của lớp (Q-R34), không đổi Visibility của con.
/// </summary>
public sealed class LayerElement : OverlayElement
{
    private readonly List<OverlayElement> _children = [];

    public LayerElement(string id)
        : base(id)
    {
    }

    public IReadOnlyList<OverlayElement> Children => _children;

    public LayerElement Add(OverlayElement child)
    {
        ArgumentNullException.ThrowIfNull(child);
        _children.Add(child);
        return this;
    }

    public override OverlayElement? FindAt(PointD point)
    {
        if (!IsVisible || !IsHitTestVisible || Opacity <= 0f || !Bounds.Contains(point))
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

        return null;
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
        double width = 0;
        double height = 0;
        foreach (OverlayElement child in _children)
        {
            SizeD size = child.Measure(available, context);
            width = Math.Max(width, size.Width);
            height = Math.Max(height, size.Height);
        }

        return new SizeD(width, height);
    }

    protected override void ArrangeChildren(in OverlayLayoutContext context)
    {
        foreach (OverlayElement child in _children)
        {
            child.ArrangeIn(Bounds, context);
        }
    }

    protected override void RenderCore(IDrawContext dc)
    {
        foreach (OverlayElement child in _children)
        {
            child.Render(dc);
        }
    }
}
