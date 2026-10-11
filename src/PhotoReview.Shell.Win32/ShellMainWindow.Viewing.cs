using System.ComponentModel;
using System.Diagnostics;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Win32;

// WP-21: trình bày ảnh (ảnh chính, compare, crossfade, nâng cấp preview -> full, prefetch GPU). Phần tử XAML MainImage /
// OutgoingImage / ComparePanel được thay bởi PresentationComposer; file này chỉ là keo nối với cửa sổ, view model và cache.
internal sealed partial class ShellMainWindow
{
    /// <summary>
    /// Bản nâng cấp cùng ảnh có bitmap lớn hơn ngưỡng này (4 MP ~ 6 ms upload đồng bộ) được upload nền trước khi thay; nhỏ hơn
    /// (thumbnail -> preview cỡ màn hình) thay ngay vì upload đồng bộ rẻ hơn việc giữ ảnh mờ thêm vài khung.
    /// </summary>
    internal const long DeferUploadBytes = 16L * 1024 * 1024;

    private IFrameClock? _viewingClock;
    private PresentationComposer? _composer;
    private PresentationPrefetcher? _prefetcher;
    private EventHandler? _onCacheCleared;
    private PhotoReview.Imaging.Caching.PreviewImageService? _clearSource;

    private readonly FramePacer _pacer = new();

    /// <summary>Thời gian từng khung (tổng + Present) để đo độ chặn của <c>Present</c>.</summary>
    internal FrameStats Stats { get; } = new();

    /// <summary>Chính sách nhịp khung (Immediate khi cửa sổ thường; VSync + chờ waitable khi phủ màn hình).</summary>
    internal FramePacer Pacer => _pacer;

    internal PresentationComposer? Composer => _composer;

    internal PresentationPrefetcher? Prefetcher => _prefetcher;

    /// <summary>Gọi một lần từ constructor, trước khi có bề mặt.</summary>
    private void InitializeViewing(IFrameClock frameClock)
    {
        _viewingClock = frameClock;
        _viewModel.ImageChanging += OnImageChanging;
        _viewModel.Compare.PropertyChanged += OnCompareChanged;
        _viewModel.PropertyChanged += OnNavigationChanged;
        _pacer.Degraded += (_, _) => _log.Warn("Present(1) blocked the UI thread; the shell now presents without waiting for vblank (WP-21).");
        _clearSource = _viewModel.PreviewService;
        if (_clearSource is not null)
        {
            _onCacheCleared = (_, _) => _dispatcher.Post(ClearGpuCache);
            _clearSource.CacheCleared += _onCacheCleared;
        }
    }

    /// <summary>Gọi sau khi <c>_cache</c> được tạo (<see cref="AttachRenderer"/>).</summary>
    private void AttachViewing()
    {
        var cache = _cache!;
        _composer = new PresentationComposer(cache, _dispatcher, _viewingClock!, _window.Invalidate);
        _composer.CurrentChanged += OnComposerCurrentChanged;
        _prefetcher = new PresentationPrefetcher(cache, new PrefetchHost(this));
        _prefetcher.PlanChanged += OnPrefetchPlanChanged;
        if (_image is { IsDisposed: false } existing)
        {
            _composer.Set(existing);
        }
    }

    private void DisposeViewing()
    {
        _viewModel.ImageChanging -= OnImageChanging;
        _viewModel.Compare.PropertyChanged -= OnCompareChanged;
        _viewModel.PropertyChanged -= OnNavigationChanged;
        if (_clearSource is not null && _onCacheCleared is not null)
        {
            _clearSource.CacheCleared -= _onCacheCleared;
        }

        _composer?.Dispose();
        _composer = null;
        _prefetcher = null;
    }

    // Ảnh mới -------------------------------------------------------------------------------

    /// <summary>
    /// <c>MainViewModel.ImageChanging</c>: ĐỒNG BỘ, trước khi ViewerState nhận kích thước nguồn mới và trước khi ảnh mới thay vào,
    /// nên khung đã thấy lần cuối vẫn còn nguyên để đóng băng (như <c>MainWindow.StartImageFade</c>).
    /// </summary>
    private void OnImageChanging(object? sender, ImageChangingEventArgs e)
    {
        if (_disposed || _composer is null || !e.IsFileChange)
        {
            return;
        }

        var settings = _viewModel.Settings;
        if (settings.ImageTransition == ImageTransition.None)
        {
            return;
        }

        var ms = Math.Clamp(settings.ImageTransitionMs, AppSettings.MinImageTransitionMs, AppSettings.MaxImageTransitionMs);
        _composer.BeginCrossfade(TimeSpan.FromMilliseconds(ms));
    }

    /// <summary>Thay thân <c>OnImageChanged</c>: hub báo ảnh đổi (UI thread, SAU khi ViewerState đã cập nhật).</summary>
    private void HandleImageChanged(object? image, bool isFileChange)
    {
        var buffer = image as PixelBuffer;
        if (_composer is null)
        {
            _image = buffer;
        }
        else
        {
            var replace = _composer.SetIncoming(buffer, isFileChange, DeferUploadBytes);
            if (replace)
            {
                _image = _composer.Current;
            }

            UpdateRetained();
        }

        _viewport.InvalidateLayout();
        _window.Invalidate();
        if (_image is not null && !_disposed && !_firstImageDrawn)
        {
            // Ảnh đầu: vẽ NGAY trong lượt này (không chờ nhịp khung kế) - mốc "ảnh đầu đã vẽ".
            RenderNow();
        }
    }

    /// <summary>Bản nâng cấp đã upload xong và được chuyển thành ảnh chính: layout đo lại theo bitmap mới.</summary>
    private void OnComposerCurrentChanged(object? sender, EventArgs e)
    {
        _image = _composer?.Current;
        _viewport.InvalidateLayout();
        UpdateRetained();
    }

    private void OnCompareChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        UpdateRetained();
        _window.Invalidate();
    }

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || _prefetcher is null || e.PropertyName != nameof(MainViewModel.CurrentIndex))
        {
            return;
        }

        _prefetcher.Navigated(_viewModel.CurrentIndex, _viewModel.Catalog.Count);
    }

    private void OnPrefetchPlanChanged(object? sender, EventArgs e) => UpdateRetained();

    /// <summary>
    /// <c>GpuImageCache.Retain</c>: ảnh đang hiện (+ bản nâng cấp chờ, lớp ảnh cũ đang mờ, hai ô compare) và các ảnh kế tiếp đã
    /// prefetch không bị LRU đuổi; phần còn lại theo ngân sách.
    /// </summary>
    private void UpdateRetained()
    {
        if (_cache is not { } cache || _composer is null)
        {
            return;
        }

        var ids = new HashSet<long>();
        _composer.CollectRetainedIds(ids);
        if (_prefetcher is { } prefetcher)
        {
            foreach (var id in prefetcher.PlannedIds)
            {
                ids.Add(id);
            }
        }

        var compare = _viewModel.Compare;
        if (compare.IsVisible)
        {
            if (compare.LeftImage is PixelBuffer { IsDisposed: false } left)
            {
                ids.Add(left.Id);
            }

            if (compare.RightImage is PixelBuffer { IsDisposed: false } right)
            {
                ids.Add(right.Id);
            }
        }

        cache.Retain(ids);
    }

    /// <summary>
    /// "Xoá cache" (INV-2): bitmap GPU không được sống lâu hơn mục RAM đã tạo ra nó. Ảnh đang hiện vẫn còn buffer CPU nên được
    /// upload lại ở khung kế (không nháy: khung đó vẽ cùng ảnh). Lớp ảnh cũ của crossfade (buffer đã xoá khỏi cache) dừng.
    /// </summary>
    private void ClearGpuCache()
    {
        if (_disposed)
        {
            return;
        }

        _cache?.Clear();
        _composer?.CancelCrossfade();
        _window.Invalidate();
    }

    // Vẽ ------------------------------------------------------------------------------------

    private void DrawPresentation(PhotoReview.Shell.Rendering.D2DDrawContext context)
    {
        if (_composer is null)
        {
            return;
        }

        var viewer = _viewModel.Viewer;
        var origin = _viewport.ImageOrigin;
        var destination = new RectD(origin.X, origin.Y, _viewport.ImageActualWidth, _viewport.ImageActualHeight);
        var compare = _viewModel.Compare;
        var inputs = new PresentationInputs(
            _window.ClientSizeDip,
            destination,
            viewer.EffectiveScalingQuality,
            viewer.ScalingQuality,
            _window.DpiScale,
            compare.IsVisible,
            new CompareFrame(compare.LeftImage as PixelBuffer, compare.RightImage as PixelBuffer, compare.IsLeftSelected,
                compare.IsRightSelected));
        var frame = _composer.Draw(context, inputs);

        // "Ảnh hiện tại đã nằm trong một khung đã Present" (mốc RenderedFrame) chỉ khi ảnh được vẽ chính là ảnh mà presenter vừa
        // gán: trong lúc chờ upload bản nâng cấp, khung vẫn có ảnh cũ nhưng không được tính cho mốc của bản mới.
        if (frame.MainDrawn && ReferenceEquals(_composer.Current, _hub.CurrentImage))
        {
            _imageDrawnInFrame = true;
        }
        else if (frame.CompareImages > 0)
        {
            _imageDrawnInFrame = true; // hub phát mốc "compare" khi khung có ảnh compare
        }
    }

    /// <summary>
    /// Chọn ô compare tại điểm (toạ độ client DIP; <c>MouseLeftButtonDown</c> của hai Border). WP-22 nối chuột vào đây.
    /// Trả true khi điểm nằm trên một ô (sự kiện đã được xử lý).
    /// </summary>
    internal bool TrySelectCompareAt(PointD clientDip)
    {
        if (_disposed || !_viewModel.Compare.IsVisible)
        {
            return false;
        }

        switch (CompareLayout.HitTest(_window.ClientSizeDip, clientDip))
        {
            case CompareSide.Left:
                _viewModel.Compare.SelectLeft();
                return true;
            case CompareSide.Right:
                _viewModel.Compare.SelectRight();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Host của <see cref="PresentationPrefetcher"/> trên cửa sổ thật.</summary>
    private sealed class PrefetchHost(ShellMainWindow owner) : IPrefetchHost
    {
        public string? PathAt(int index)
        {
            var catalog = owner._viewModel.Catalog;
            return index >= 0 && index < catalog.Count ? catalog.PathAt(index) : null;
        }

        public PixelBuffer?[] LookupCached(string[] paths)
        {
            var service = owner._viewModel.PreviewService;
            var result = new PixelBuffer?[paths.Length];
            if (service is null)
            {
                return result;
            }

            for (var i = 0; i < paths.Length; i++)
            {
                try
                {
                    if (service.TryGetCachedPreview(paths[i], out var decoded))
                    {
                        result[i] = decoded.PlatformImage as PixelBuffer;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    // File biến mất giữa chừng: không prefetch.
                }
            }

            return result;
        }

        public void RunBackground(Action work) => ThreadPool.QueueUserWorkItem(static state => ((Action)state!)(), work);

        public void PostToUi(Action action) => owner._dispatcher.Post(() =>
        {
            if (!owner._disposed)
            {
                action();
            }
        }, PhotoReview.Core.Abstractions.UiPriority.Background);

        public void ScheduleOnUi(TimeSpan delay, Action action) =>
            _ = Task.Delay(delay).ContinueWith(_ => PostToUi(action), TaskScheduler.Default);
    }
}
