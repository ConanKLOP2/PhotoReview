using PhotoReview.App.Input;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>
/// WP-17 (C-11): chứa các phần tử gốc của overlay theo thứ tự z (thêm sau = nằm trên), bố cục trong client, vẽ, hit-test
/// (phần tử ẩn/opacity 0 bị bỏ qua - click xuyên) và tra phần tử theo <see cref="IOverlayElement.Id"/> cho <see cref="Animator"/>.
/// Panel cụ thể (Toolbar, Status, ...) do WP-23 thêm bằng <see cref="PanelElement"/>/<see cref="TextElement"/>/<see cref="ButtonElement"/>.
/// </summary>
public sealed class OverlayHost : IDisposable
{
    private readonly List<OverlayElement> _roots = [];
    private bool _disposed;

    /// <summary>Phần tử gốc theo thứ tự z (cuối = trên cùng).</summary>
    public IReadOnlyList<OverlayElement> Roots => _roots;

    public void Add(OverlayElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Find(root.Id) is not null)
        {
            throw new ArgumentException($"An overlay element with id '{root.Id}' already exists.", nameof(root));
        }

        _roots.Add(root);
    }

    public bool Remove(OverlayElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return _roots.Remove(root);
    }

    /// <summary>Tìm theo Id trong mọi cây; null nếu không có.</summary>
    public OverlayElement? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        foreach (OverlayElement root in _roots)
        {
            if (root.Find(id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Cho <see cref="Animator"/>.</summary>
    public IOpacityTarget? Resolve(string id) => Find(id);

    public void Arrange(in OverlayLayoutContext context)
    {
        foreach (OverlayElement root in _roots)
        {
            root.Arrange(context);
        }
    }

    public void Render(IDrawContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        foreach (OverlayElement root in _roots)
        {
            root.Render(dc);
        }
    }

    /// <summary>Phần tử trên cùng, sâu nhất ở <paramref name="point"/>; null = click xuyên xuống ảnh.</summary>
    public OverlayElement? HitTest(PointD point)
    {
        for (int i = _roots.Count - 1; i >= 0; i--)
        {
            if (_roots[i].FindAt(point) is { } hit)
            {
                return hit;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (OverlayElement root in _roots)
        {
            root.ReleaseResources();
        }

        _roots.Clear();
    }
}
