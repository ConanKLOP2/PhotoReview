using System.Diagnostics;
using PhotoReview.App.Input;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Overlay;

namespace PhotoReview.Shell.Win32.Viewing;

/// <summary>
/// WP-21 (Q-R37): lớp ảnh CŨ của crossfade = <c>OutgoingImage</c> của <c>MainWindow.xaml</c>. Giữ NGUYÊN khung hình đã thấy
/// (pixel buffer + hộp đích đã vẽ lần cuối, kể cả offset cuộn) trong khi ảnh mới đã hiện đầy đủ bên dưới; chỉ opacity của lớp
/// này đi 1 -&gt; 0 (<see cref="Easing.QuadraticOut"/>) trong <c>ImageTransitionMs</c>, nên không có khoảng tối và chỉ một
/// animation chạy. Điều hướng mới trong lúc đang mờ: HUỶ tween cũ rồi chạy lại từ 1 với ảnh cũ mới (như
/// <c>BeginAnimation(SnapshotAndReplace)</c>) - không xếp hàng, ảnh mới không bao giờ bị trễ vì hiệu ứng.
/// Đồng hồ khung do người gọi tiêm (<see cref="IFrameClock"/>): test chạy tất định, không có hiệu ứng thì không đăng ký đồng hồ.
/// Dùng trên UI thread.
/// </summary>
internal sealed class CrossfadeLayer : IOpacityTarget, IDisposable
{
    /// <summary>Id của phần tử trong <see cref="Animator"/> (x:Name WPF).</summary>
    internal const string ElementId = "OutgoingImage";

    private readonly Animator _animator;
    private readonly Action _invalidate;
    private PixelBuffer? _outgoing;
    private RectD _frame;
    private ImageInterpolation _interpolation;
    private float _opacity;
    private bool _disposed;

    /// <param name="clock">Đồng hồ khung (nhịp tween).</param>
    /// <param name="invalidate">Gọi sau mỗi khung có opacity đổi (host vẽ lại).</param>
    /// <param name="ticksPerSecond">Đơn vị <c>Timestamp</c> của đồng hồ; mặc định Stopwatch.</param>
    internal CrossfadeLayer(IFrameClock clock, Action invalidate, long ticksPerSecond = 0)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(invalidate);
        _invalidate = invalidate;
        _animator = new Animator(clock, ResolveTarget, ticksPerSecond > 0 ? ticksPerSecond : Stopwatch.Frequency);
        _animator.Changed += OnAnimatorChanged;
    }

    /// <summary>Opacity hiện tại của lớp (0..1); 0 hoặc không có ảnh = không vẽ.</summary>
    public float Opacity
    {
        get => _opacity;
        set => _opacity = value;
    }

    /// <summary>Đang giữ ảnh cũ (đang mờ).</summary>
    internal bool IsActive => _outgoing is not null;

    /// <summary>Số lần bắt đầu hiệu ứng (chẩn đoán/test).</summary>
    internal int StartCount { get; private set; }

    internal PixelBuffer? Outgoing => _outgoing;

    internal RectD Frame => _frame;

    /// <summary>Id PixelBuffer của ảnh cũ nếu còn đang mờ (để cache giữ bitmap GPU của nó).</summary>
    internal long? OutgoingBufferId => _outgoing is { IsDisposed: false } buffer ? buffer.Id : null;

    /// <summary>
    /// Bắt đầu mờ ảnh <paramref name="outgoing"/> đang nằm ở <paramref name="frame"/> (khung đã vẽ lần cuối). Bỏ qua khi
    /// buffer đã giải phóng, khung rỗng/không hữu hạn hay thời lượng không dương. Thay hiệu ứng đang chạy.
    /// </summary>
    /// <returns>True khi hiệu ứng bắt đầu.</returns>
    internal bool Start(PixelBuffer outgoing, RectD frame, ImageInterpolation interpolation, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(outgoing);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (outgoing.IsDisposed || duration <= TimeSpan.Zero || frame.Width <= 0 || frame.Height <= 0
            || !double.IsFinite(frame.X) || !double.IsFinite(frame.Y) || !double.IsFinite(frame.Width) || !double.IsFinite(frame.Height))
        {
            return false;
        }

        // SnapshotAndReplace: huỷ tween cũ NGAY (Animator coi FadeTo cùng đích là không làm gì), ảnh cũ mới, bắt đầu lại từ 1.
        _animator.Cancel(ElementId);
        _outgoing = outgoing;
        _frame = frame;
        _interpolation = interpolation;
        _opacity = 1f;
        StartCount++;
        _animator.FadeTo(ElementId, 0f, duration, Easing.QuadraticOut);
        return true;
    }

    /// <summary>Dừng ngay và nhả ảnh cũ (đóng cửa sổ, đổi thiết bị, tắt hiệu ứng).</summary>
    internal void Cancel()
    {
        _animator.Cancel(ElementId);
        Release();
    }

    /// <summary>
    /// Vẽ lớp ảnh cũ trên cùng (clip theo viewport). Trả true khi có vẽ. Hết hiệu ứng, buffer bị giải phóng hay opacity 0 thì
    /// nhả ảnh cũ và không vẽ.
    /// </summary>
    internal bool Draw(IGpuImageCache cache, IDrawContext context, RectD viewport)
    {
        if (_outgoing is null)
        {
            return false;
        }

        if (_outgoing.IsDisposed || (_opacity <= 0f && !_animator.IsAnimating))
        {
            Release();
            return false;
        }

        if (_opacity <= 0f)
        {
            return false;
        }

        context.PushClip(viewport);
        try
        {
            if (ImageLayer.TryDraw(cache, context, _outgoing, _frame, _interpolation, _opacity))
            {
                return true;
            }
        }
        finally
        {
            context.PopClip();
        }

        Cancel(); // không vẽ được (buffer vừa bị giải phóng): hiệu ứng không còn gì để mờ
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _animator.Changed -= OnAnimatorChanged;
        _animator.Dispose();
        Release();
    }

    private CrossfadeLayer? ResolveTarget(string id) => id == ElementId && _outgoing is not null ? this : null;

    private void OnAnimatorChanged(object? sender, EventArgs e)
    {
        if (_opacity <= 0f)
        {
            Release();
        }

        _invalidate();
    }

    private void Release()
    {
        _outgoing = null;
        _opacity = 0f;
    }
}
