using System.Windows;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.App.Viewport;

namespace PhotoReview.App.Tests.Viewport;

/// <summary>
/// WP-16 (adapter test): <see cref="IImageSurface"/> + <see cref="IFitSurface"/> trên <see cref="ViewportState"/> - engine thuần
/// đứng thay ScrollViewer + Image để chạy <see cref="FitViewController"/> và <see cref="PointerInputController"/> THẬT, không sửa.
/// Bắt chước đúng các nối của MainWindow/WpfImageSurface:
/// <list type="bullet">
/// <item>binding: input layout đọc lại từ <see cref="ViewerState"/> ở mỗi lượt layout (Stretch, Width/Height, MaxWidth/MaxHeight);</item>
/// <item><c>UpdateLayout</c> lặp tới khi sạch, gồm <c>MainImage_SizeChanged</c> -> <c>UpdateFitSize</c> khi đang Fit
/// (như LayoutManager chạy lại trong cùng UpdateLayout);</item>
/// <item><c>YieldToRenderAsync</c> = một lượt layout ở mức Render (việc đã xếp trước đó, ví dụ resize, chạy trước);</item>
/// <item>khung render (<see cref="Frame"/>): handler rồi lượt layout của khung đó.</item>
/// </list>
/// Đây là phần lõi mà <c>Shell.Win32.Viewing.ViewportController</c> làm trên cửa sổ Win32; khi C-07 v1.1 đưa
/// IImageSurface/IFitSurface sang App.Shared, ViewportController implement thẳng hai interface và adapter này bỏ đi.
/// </summary>
internal sealed class EngineImageSurface : IImageSurface, IFitSurface
{
    public const double ScrollBarThickness = 10;

    private readonly ViewerState _viewer;
    private readonly ViewportState _state = new();
    private double _clientWidth;
    private double _clientHeight;
    private (double Width, double Height) _bitmap;

    public EngineImageSurface(ViewerState viewer, double clientWidth, double clientHeight, (double Width, double Height) bitmap)
    {
        _viewer = viewer;
        _clientWidth = clientWidth;
        _clientHeight = clientHeight;
        _bitmap = bitmap;
        _viewer.UpdateViewport(clientWidth, clientHeight);
        LayoutPass();
    }

    public List<string> Log { get; } = [];

    public ViewportState State => _state;

    public bool IsLoaded { get; set; } = true;

    /// <summary>Cửa sổ đổi kích thước: ImageScroll_SizeChanged -> UpdateFitSize, rồi layout.</summary>
    public void Resize(double clientWidth, double clientHeight)
    {
        _clientWidth = clientWidth;
        _clientHeight = clientHeight;
        _viewer.UpdateViewport(clientWidth, clientHeight);
        LayoutPass();
    }

    /// <summary>Bitmap đang vẽ đổi (bản preview -> bản đầy đủ): chỉ kích thước tự nhiên.</summary>
    public void SetBitmap(double width, double height) => _bitmap = (width, height);

    private ViewportInput CurrentInput() => new(
        _clientWidth, _clientHeight, ScrollBarThickness, ScrollBarPolicy.Auto,
        _viewer.Stretch, _viewer.ImageWidth, _viewer.ImageHeight, _viewer.MaxImageWidth, _viewer.MaxImageHeight,
        _bitmap.Width, _bitmap.Height);

    // ---- IFitSurface ----

    public (double Width, double Height) ViewportSize => (_clientWidth, _clientHeight);

    public void UpdateLayout()
    {
        Log.Add("UpdateLayout");
        LayoutPass();
    }

    /// <summary>Số lần MainImage_SizeChanged gọi UpdateFitSize (ngoài các lời gọi của controller).</summary>
    public int SizeChangedFitUpdates { get; private set; }

    private void LayoutPass()
    {
        for (var guard = 0; guard < 8; guard++)
        {
            _state.SetInput(CurrentInput());
            var before = (_state.ImageActualWidth, _state.ImageActualHeight);
            if (!_state.UpdateLayout()) return;
            if (before != (_state.ImageActualWidth, _state.ImageActualHeight) && _viewer.IsFit)
            {
                SizeChangedFitUpdates++;
                _viewer.UpdateViewport(_clientWidth, _clientHeight);
            }
        }
        throw new InvalidOperationException("layout không hội tụ");
    }

    public void UpdateFitSize()
    {
        Log.Add("UpdateFitSize");
        _viewer.UpdateViewport(_clientWidth, _clientHeight);
    }

    private readonly Queue<TaskCompletionSource> _pendingYields = new();
    private int _yields;

    /// <summary>Việc chạy khi lần YieldToRenderAsync thứ n (đếm từ 1) hoàn tất, trước lượt layout của nó - ví dụ cửa sổ đổi cỡ giữa hai pass của Fit.</summary>
    public Dictionary<int, Action> AtYield { get; } = [];

    /// <summary>Như Dispatcher.InvokeAsync(noop, Render): hoàn tất SAU, khi dispatcher giả (<see cref="Pump"/>) chạy tới mức Render.</summary>
    public Task YieldToRenderAsync()
    {
        Log.Add("Yield");
        var pending = new TaskCompletionSource();
        _pendingYields.Enqueue(pending);
        return pending.Task;
    }

    /// <summary>
    /// Dispatcher giả: lượt layout, rồi hoàn tất từng yield đang chờ (mỗi cái có lượt layout trước nó, như hàng đợi Render của
    /// WPF), continuation chạy ngay trên luồng này; kết thúc bằng một lượt layout (Render ưu tiên hơn Input).
    /// </summary>
    public void Pump()
    {
        LayoutPass();
        while (_pendingYields.TryDequeue(out var pending))
        {
            _yields++;
            if (AtYield.Remove(_yields, out var scheduled)) scheduled();
            LayoutPass();
            pending.SetResult();
        }
        LayoutPass();
    }

    /// <summary>Chạy một thao tác async của controller tới khi xong, bơm dispatcher giả giữa các lần chờ.</summary>
    public async Task Run(Task operation)
    {
        for (var guard = 0; guard < 100 && !operation.IsCompleted; guard++) Pump();
        Assert.True(operation.IsCompleted, "thao tác không xong sau 100 lượt bơm");
        await operation;
        Pump();
    }

    public ViewportSnapshot Capture()
    {
        Log.Add("Capture");
        var layout = _state.Layout;
        return new ViewportSnapshot(_viewer.Zoom, _viewer.Stretch, _viewer.MaxImageWidth, _viewer.MaxImageHeight,
            _state.ImageActualWidth, _state.ImageActualHeight, layout.ExtentWidth, layout.ExtentHeight,
            layout.ViewportWidth, layout.ViewportHeight, _state.HorizontalOffset, _state.VerticalOffset,
            layout.HorizontalBarVisible ? Visibility.Visible : Visibility.Collapsed,
            layout.VerticalBarVisible ? Visibility.Visible : Visibility.Collapsed);
    }

    public void ScrollHome()
    {
        Log.Add("ScrollHome");
        _state.ScrollHome();
    }

    // ---- IImageSurface ----

    public double HorizontalOffset => _state.HorizontalOffset;
    public double VerticalOffset => _state.VerticalOffset;
    public double ViewportWidth => _state.Layout.ViewportWidth;
    public double ViewportHeight => _state.Layout.ViewportHeight;
    public double ExtentWidth => _state.Layout.ExtentWidth;
    public double ExtentHeight => _state.Layout.ExtentHeight;
    public (double Horizontal, double Vertical) DragThreshold => (4, 4);

    public void ScrollTo(double horizontal, double vertical) => _state.ScrollTo(horizontal, vertical);

    public Point ImageOrigin => new(_state.ImageOrigin.X, _state.ImageOrigin.Y);

    public Point ToImageElement(Point surfacePoint)
    {
        var p = _state.ToImageElement(new PointD(surfacePoint.X, surfacePoint.Y));
        return new Point(p.X, p.Y);
    }

    public double ImageActualWidth => _state.ImageActualWidth;
    public double ImageActualHeight => _state.ImageActualHeight;
    public (double Width, double Height)? SourceSize => _bitmap.Width > 0 && _bitmap.Height > 0 ? _bitmap : null;

    public bool Captured { get; private set; }
    public void CaptureMouse() => Captured = true;
    public void ReleaseMouseCapture() => Captured = false;
    public bool PanCursor { get; private set; }
    public void SetPanCursor(bool panning) => PanCursor = panning;

    private EventHandler? _frameHandlers;
    public bool HasFrameHandler => _frameHandlers is not null;
    public void HookRenderFrame(EventHandler handler) => _frameHandlers += handler;
    public void UnhookRenderFrame(EventHandler handler) => _frameHandlers -= handler;
    public TimeSpan? RenderingTime(EventArgs e) => e is FrameArgs frame ? frame.Time : null;
    public long Timestamp { get; set; }
    public PhotoReview.Core.Abstractions.DisplayTiming? DisplayTiming => null;
    public Point? PointerPosition { get; set; }

    /// <summary>Một khung: CompositionTarget.Rendering rồi lượt layout của khung.</summary>
    public void Frame(TimeSpan renderingTime)
    {
        _frameHandlers?.Invoke(this, new FrameArgs(renderingTime));
        LayoutPass();
    }

    /// <summary>Điểm ảnh (tỉ lệ 0..1 của ảnh đang vẽ) nằm dưới <paramref name="viewportPoint"/>.</summary>
    public (double X, double Y) ImageFractionAt(Point viewportPoint)
    {
        var p = _state.ToImageElement(new PointD(viewportPoint.X, viewportPoint.Y));
        return (p.X / _state.ImageActualWidth, p.Y / _state.ImageActualHeight);
    }

    public sealed class FrameArgs(TimeSpan time) : EventArgs
    {
        public TimeSpan Time { get; } = time;
    }
}
