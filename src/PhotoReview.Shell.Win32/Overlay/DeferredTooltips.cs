namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>
/// WP-23: <see cref="IOverlayTooltips"/> tạo control thật SAU (qua <paramref name="post"/>, mức Background) để chi phí nạp
/// comctl32 v6 + tạo cửa sổ tooltip không nằm trên đường tới khung đầu. Trước khi tạo xong: ghi nhớ danh sách vùng gần nhất,
/// bỏ qua chuột; khi tạo xong áp danh sách đó. Lỗi tạo control (hiếm) -> không tooltip, không ném. UI thread.
/// </summary>
internal sealed class DeferredTooltips : IOverlayTooltips
{
    private readonly Func<IOverlayTooltips> _create;
    private IOverlayTooltips? _inner;
    private bool _failed;
    private bool _disposed;
    private IReadOnlyList<OverlayTooltip> _pending = [];
    private double _pendingDpi = 1;

    public DeferredTooltips(Func<IOverlayTooltips> create, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(post);
        _create = create;
        post(CreateNow);
    }

    /// <summary>Control thật đã tạo (test).</summary>
    internal bool IsCreated => _inner is not null;

    public void Sync(IReadOnlyList<OverlayTooltip> tools, double dpiScale)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (_disposed)
        {
            return;
        }

        if (_inner is { } inner)
        {
            inner.Sync(tools, dpiScale);
            return;
        }

        _pending = [.. tools];
        _pendingDpi = dpiScale;
    }

    public void RelayMouse(uint message, nint wParam, nint lParam) => _inner?.RelayMouse(message, wParam, lParam);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inner?.Dispose();
        _inner = null;
    }

    private void CreateNow()
    {
        if (_disposed || _inner is not null || _failed)
        {
            return;
        }

        try
        {
            IOverlayTooltips created = _create();
            _inner = created;
            created.Sync(_pending, _pendingDpi);
            _pending = [];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _failed = true; // không có tooltip còn hơn là làm sập khung
        }
    }
}
