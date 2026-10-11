using System.Diagnostics;
using PhotoReview.App.Input;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Viewing;

/// <summary>Đầu vào của một khung trình bày (tính từ ViewerState/ViewportController/Compare mỗi khung).</summary>
/// <param name="Viewport">Vùng client (DIP).</param>
/// <param name="MainDestination">Hộp đích của ảnh chính (DIP): <c>ImageOrigin</c> + kích thước thật của ảnh.</param>
/// <param name="EffectiveQuality"><c>ViewerState.EffectiveScalingQuality</c> (ảnh chính).</param>
/// <param name="CompareQuality"><c>ViewerState.ScalingQuality</c> (hai ô compare dùng thiết lập gốc, không có ép HQ của Refresh).</param>
/// <param name="DpiScale">DPI / 96: đổi hộp đích sang pixel thiết bị để đo mức thu nhỏ.</param>
internal readonly record struct PresentationInputs(
    SizeD Viewport,
    RectD MainDestination,
    ScalingQuality EffectiveQuality,
    ScalingQuality CompareQuality,
    double DpiScale,
    bool CompareVisible,
    CompareFrame Compare);

/// <summary>Kết quả của một lần <see cref="PresentationComposer.Draw"/>.</summary>
/// <param name="MainDrawn">Ảnh chính (<c>Current</c>) đã được vẽ.</param>
/// <param name="CrossfadeDrawn">Lớp ảnh cũ của crossfade đã được vẽ (opacity &gt; 0).</param>
/// <param name="CompareImages">Số ảnh compare đã vẽ (0..2).</param>
/// <param name="Interpolation">Bộ lọc đã dùng cho ảnh chính.</param>
/// <param name="Degraded">Bộ lọc bị hạ xuống Linear vì ảnh đang chuyển động và thu nhỏ nặng (sẽ có khung HQ khi đứng yên).</param>
internal readonly record struct ComposedFrame(
    bool MainDrawn, bool CrossfadeDrawn, int CompareImages, ImageInterpolation Interpolation, bool Degraded);

/// <summary>
/// WP-21: trạng thái + thứ tự vẽ của phần "trình bày ảnh" (thay <c>MainImage</c> + <c>OutgoingImage</c> + <c>ComparePanel</c>
/// của <c>MainWindow.xaml</c>), tách khỏi cửa sổ để test được trên bề mặt offscreen và đồng hồ khung giả.
/// <list type="bullet">
/// <item>Thứ tự (dưới lên): ảnh chính, lớp ảnh cũ crossfade, compare - như XAML. Canvas do cửa sổ <c>Clear</c>.</item>
/// <item>Nâng cấp cùng ảnh (preview -&gt; full, ADR 0008): buffer mới được UPLOAD TRƯỚC theo dải ở mức Background, ảnh cũ vẫn
/// được vẽ vào đúng hộp đích tới khi bitmap mới sẵn (hoặc quá <see cref="MaxDeferMs"/>) - không có khung nào chặn UI thread
/// vài chục ms để upload đồng bộ ảnh 24 MP, và không nhảy layout (hộp đích do layout/ViewerState quyết, không phụ thuộc buffer).</item>
/// <item>Bộ lọc: <see cref="ScalingPolicy"/> (chuyển động + thu nhỏ nặng thì tạm Linear, đứng yên <see cref="ScalingPolicy.SettleMs"/> thì
/// vẽ lại HQ).</item>
/// </list>
/// Dùng trên UI thread.
/// </summary>
internal sealed class PresentationComposer : IDisposable
{
    /// <summary>Chờ tối đa chừng này cho bitmap của bản nâng cấp (prefetch bị bỏ đói/evict thì upload đồng bộ như cũ).</summary>
    internal const int MaxDeferMs = 1000;

    private readonly GpuImageCache _cache;
    private readonly IFrameClock _clock;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action _invalidate;
    private readonly Func<long> _now;
    private readonly Action<TimeSpan, Action> _scheduleOnUi;
    private readonly long _ticksPerSecond;
    private readonly long _settleTicks;
    private readonly CrossfadeLayer _crossfade;
    private readonly EventHandler<FrameTickEventArgs> _onSettleTick;
    private PixelBuffer? _current;
    private PixelBuffer? _pending;
    private long _pendingSince;
    private long _pendingToken;
    private long _lastDrawnId = -1;
    private RectD _lastDrawnRect;
    private ImageInterpolation _lastDrawnInterpolation;
    private bool _hasLastDrawn;
    private long _lastMotionTicks;
    private bool _settleArmed;
    private bool _disposed;

    internal PresentationComposer(GpuImageCache cache, IUiDispatcher dispatcher, IFrameClock clock, Action invalidate,
        Func<long>? timestamp = null, long ticksPerSecond = 0, int settleMs = ScalingPolicy.SettleMs,
        Action<TimeSpan, Action>? scheduleOnUi = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(invalidate);
        _cache = cache;
        _dispatcher = dispatcher;
        _clock = clock;
        _invalidate = invalidate;
        _now = timestamp ?? Stopwatch.GetTimestamp;
        _ticksPerSecond = ticksPerSecond > 0 ? ticksPerSecond : Stopwatch.Frequency;
        _settleTicks = Math.Max(0, settleMs) * _ticksPerSecond / 1000;
        _lastMotionTicks = long.MinValue / 2;
        _scheduleOnUi = scheduleOnUi ?? ScheduleViaDispatcher;
        _crossfade = new CrossfadeLayer(clock, invalidate, _ticksPerSecond);
        _onSettleTick = OnSettleTick;
        _cache.UploadCompleted += OnUploadCompleted;
    }

    /// <summary>Ảnh chính đang được vẽ (đồng bộ với bitmap mà layout dùng để đo).</summary>
    internal PixelBuffer? Current => _current;

    /// <summary>Bản nâng cấp đang chờ upload xong (ảnh cũ vẫn hiển thị); null khi không có.</summary>
    internal PixelBuffer? Pending => _pending;

    internal CrossfadeLayer Crossfade => _crossfade;

    /// <summary>Khung cuối đã thấy: buffer + hộp đích (để crossfade đóng băng đúng cái đang hiện). Rỗng khi chưa vẽ ảnh nào.</summary>
    internal (PixelBuffer Pixels, RectD Frame, ImageInterpolation Interpolation)? LastDrawn =>
        _hasLastDrawn && _current is { IsDisposed: false } buffer && buffer.Id == _lastDrawnId
            ? (buffer, _lastDrawnRect, _lastDrawnInterpolation)
            : null;

    /// <summary>Đang chờ khung HQ sau khi tạm hạ bộ lọc.</summary>
    internal bool IsSettleArmed => _settleArmed;

    /// <summary>Số lần bản nâng cấp được chuyển thành ảnh chính (chẩn đoán/test).</summary>
    internal int PromotionCount { get; private set; }

    /// <summary>Raised khi <see cref="Current"/> đổi do chuyển bản nâng cấp (cửa sổ phải đo lại layout theo bitmap mới).</summary>
    internal event EventHandler? CurrentChanged;

    /// <summary>
    /// Ảnh mới từ presenter. <paramref name="isFileChange"/> (hoặc chưa có ảnh nào/xoá ảnh/bitmap nhỏ hơn
    /// <paramref name="deferUploadBytes"/>) thay NGAY; còn lại là nâng cấp cùng ảnh: upload trước rồi mới thay.
    /// Trả true khi <see cref="Current"/> đã đổi ngay.
    /// </summary>
    internal bool SetIncoming(PixelBuffer? image, bool isFileChange, long deferUploadBytes = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pending = null;
        if (image is null || isFileChange || _current is null || _current.IsDisposed || image.IsDisposed
            || (long)image.Width * image.Height * 4 < deferUploadBytes)
        {
            _current = image;
            return true;
        }

        // Nâng cấp cùng ảnh: giữ ảnh cũ trên màn hình, upload dần bản mới.
        _cache.Prefetch(image);
        if (_cache.IsFullyUploaded(image.Id))
        {
            _current = image;
            return true;
        }

        _pending = image;
        _pendingSince = _now();
        ArmPendingDeadline(++_pendingToken);
        return false;
    }

    /// <summary>Đặt ảnh chính không qua nâng cấp (khởi tạo/test).</summary>
    internal void Set(PixelBuffer? image)
    {
        _pending = null;
        _current = image;
    }

    /// <summary>
    /// Bắt đầu crossfade từ khung đã thấy lần cuối. Gọi TRƯỚC khi ảnh mới thay vào (đúng lúc <c>ImageChanging</c>).
    /// Không có khung nào đã vẽ (ảnh đầu sau khi mở thư mục) thì không có gì để mờ.
    /// </summary>
    internal bool BeginCrossfade(TimeSpan duration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (LastDrawn is not { } shown)
        {
            return false;
        }

        return _crossfade.Start(shown.Pixels, shown.Frame, shown.Interpolation, duration);
    }

    /// <summary>Nhả ảnh cũ + dừng hiệu ứng (đóng/xoá cache/đổi thiết bị).</summary>
    internal void CancelCrossfade() => _crossfade.Cancel();

    /// <summary>Các id PixelBuffer mà cache GPU phải giữ để khung kế tiếp không phải upload lại.</summary>
    internal void CollectRetainedIds(ICollection<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (_current is { IsDisposed: false } current)
        {
            ids.Add(current.Id);
        }

        if (_pending is { IsDisposed: false } pending)
        {
            ids.Add(pending.Id);
        }

        if (_crossfade.OutgoingBufferId is { } outgoing)
        {
            ids.Add(outgoing);
        }
    }

    /// <summary>
    /// Chuyển bản nâng cấp thành ảnh chính nếu đã upload xong (hoặc quá hạn). Gọi ĐẦU mỗi khung, trước layout. True khi đã chuyển.
    /// </summary>
    internal bool TryPromotePending()
    {
        if (_pending is null)
        {
            return false;
        }

        if (_pending.IsDisposed)
        {
            _pending = null; // chủ sở hữu bỏ nó: không còn gì để nâng cấp
            return false;
        }

        var waited = _now() - _pendingSince;
        if (!_cache.IsFullyUploaded(_pending.Id) && waited < MaxDeferMs * _ticksPerSecond / 1000)
        {
            return false;
        }

        _current = _pending;
        _pending = null;
        PromotionCount++;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Vẽ ảnh chính, lớp ảnh cũ, compare (đúng thứ tự). Canvas đã được Clear bởi người gọi.</summary>
    internal ComposedFrame Draw(IDrawContext context, in PresentationInputs input)
    {
        ArgumentNullException.ThrowIfNull(context);
        var mainDrawn = false;
        var interpolation = ImageInterpolations.FromScalingQuality(input.EffectiveQuality);
        var degraded = false;
        var viewport = new RectD(0, 0, input.Viewport.Width, input.Viewport.Height);

        if (_current is { IsDisposed: false } pixels)
        {
            var destination = input.MainDestination;
            var requested = interpolation;
            var now = _now();
            var moved = pixels.Id == _lastDrawnId && _hasLastDrawn && _lastDrawnRect != destination;
            if (moved)
            {
                _lastMotionTicks = now;
            }

            var inMotion = now - _lastMotionTicks < _settleTicks;
            var dpi = input.DpiScale > 0 ? input.DpiScale : 1.0;
            interpolation = ScalingPolicy.Choose(requested, pixels.Width, pixels.Height, destination.Width * dpi,
                destination.Height * dpi, inMotion);
            degraded = interpolation != requested;

            context.PushClip(viewport);
            try
            {
                mainDrawn = ImageLayer.TryDraw(_cache, context, pixels, destination, interpolation);
            }
            finally
            {
                context.PopClip();
            }

            if (mainDrawn)
            {
                _lastDrawnId = pixels.Id;
                _lastDrawnRect = destination;
                _lastDrawnInterpolation = interpolation;
                _hasLastDrawn = true;
            }
        }

        SetSettleArmed(degraded);
        var crossfadeDrawn = _crossfade.Draw(_cache, context, viewport);
        var compareImages = input.CompareVisible
            ? CompareLayer.Draw(_cache, context, input.Viewport, input.Compare,
                ImageInterpolations.FromScalingQuality(input.CompareQuality))
            : 0;
        return new ComposedFrame(mainDrawn, crossfadeDrawn, compareImages, interpolation, degraded);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.UploadCompleted -= OnUploadCompleted;
        SetSettleArmed(false);
        _crossfade.Dispose();
        _pending = null;
        _current = null;
    }

    private void SetSettleArmed(bool armed)
    {
        if (armed == _settleArmed || (_disposed && armed))
        {
            return;
        }

        _settleArmed = armed;
        if (armed)
        {
            _clock.Frame += _onSettleTick;
            _clock.RequestFrame();
        }
        else
        {
            _clock.Frame -= _onSettleTick;
        }
    }

    private void OnSettleTick(object? sender, FrameTickEventArgs e)
    {
        if (_now() - _lastMotionTicks < _settleTicks)
        {
            _clock.RequestFrame();
            return;
        }

        // Đứng yên đủ lâu: một khung nữa (Draw sẽ chọn lại bộ lọc HQ và tự gỡ đăng ký).
        _invalidate();
    }

    /// <summary>Bitmap của một ảnh prefetch vừa upload xong: nếu là bản nâng cấp đang chờ thì chuyển ngay.</summary>
    private void OnUploadCompleted(long pixelBufferId)
    {
        if (_disposed || _pending is null || _pending.Id != pixelBufferId)
        {
            return;
        }

        if (TryPromotePending())
        {
            _invalidate();
        }
    }

    /// <summary>Hạn chót: prefetch bị bỏ đói/evict thì sau <see cref="MaxDeferMs"/> chuyển và upload đồng bộ như cũ.</summary>
    private void ArmPendingDeadline(long token) =>
        _scheduleOnUi(TimeSpan.FromMilliseconds(MaxDeferMs), () =>
        {
            if (_disposed || _pending is null || token != _pendingToken)
            {
                return;
            }

            if (TryPromotePending())
            {
                _invalidate();
            }
        });

    private void ScheduleViaDispatcher(TimeSpan delay, Action action) =>
        _ = Task.Delay(delay).ContinueWith(_ => _dispatcher.Post(action, UiPriority.Normal), TaskScheduler.Default);
}
