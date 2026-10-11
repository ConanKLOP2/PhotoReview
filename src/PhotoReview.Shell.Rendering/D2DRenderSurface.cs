using PhotoReview.Shell.Interop.Graphics;

namespace PhotoReview.Shell.Rendering;

/// <summary>
/// WP-15 (C-09): bề mặt vẽ Direct2D 1.1 trên D3D11 (BGRA). Cửa sổ: swap chain flip-model <c>FLIP_DISCARD</c>, 2 buffer,
/// waitable (<see cref="FrameLatencyWaitHandle"/>), <c>SetMaximumFrameLatency(MaxFrameLatency)</c> (mặc định 1: không quá
/// một khung chờ trình bày). Offscreen: bitmap đích premultiplied, WARP (test, G-PIX). GPU thật không có / RDP -&gt; WARP.
/// Mất thiết bị ở EndDraw/Present/Resize -&gt; tạo lại toàn bộ, phát <see cref="DeviceRecreated"/>, trả
/// <see cref="PresentResult.DeviceRecreated"/> (người gọi vẽ lại khung).
/// Chỉ dùng trên luồng đã tạo nó (luồng UI).
/// </summary>
public sealed class D2DRenderSurface : IRenderSurface
{
    private readonly DeviceResources _resources;
    private readonly RenderSurfaceOptions _options;
    private readonly D2DDrawContext _drawContext;
    private readonly int _ownerThreadId;
    private bool _drawing;
    private bool _disposed;

    private D2DRenderSurface(DeviceResources resources, RenderSurfaceOptions options)
    {
        _resources = resources;
        _options = options;
        _drawContext = new D2DDrawContext(this);
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>
    /// WP-20: Present không chờ vblank (sync 0) khi true. Shell bật nó tới khi khung có ảnh đầu tiên đã Present: đo thực tế
    /// <c>Present(1)</c> của khung ảnh đầu chặn UI thread 250-500 ms trên GPU thật (máy dev, cửa sổ vừa bỏ cloak) trong khi sync 0
    /// chỉ ~9 ms; ảnh tĩnh nên không có tearing nhìn thấy. Sau đó trở về <c>Present(1)</c> (pan mượt).
    /// </summary>
    public bool ImmediatePresent { get; set; }

    public event EventHandler? DeviceRecreated;

    public nint Hwnd => _resources.Hwnd;

    public int PixelWidth => _resources.PixelWidth;

    public int PixelHeight => _resources.PixelHeight;

    public double DpiScale => _resources.DpiScale;

    public nint FrameLatencyWaitHandle => _resources.FrameLatencyWaitHandle;

    /// <summary>Có waitable object của swap chain (cửa sổ thật); offscreen/WARP không swap chain: không có.</summary>
    internal bool HasFrameLatencyWaitHandle => _resources.FrameLatencyWaitHandle != 0;

    public bool IsWarp => _resources.IsWarp;

    /// <summary>Adapter của thiết bị D3D (chẩn đoán/báo cáo hiệu năng); rỗng với offscreen.</summary>
    public string AdapterDescription => _resources.AdapterDescription;

    /// <summary>Tăng mỗi lần thiết bị được tạo lại.</summary>
    public long DeviceGeneration => _resources.Generation;

    /// <summary>Cạnh lớn nhất của một texture (ảnh lớn hơn được chia tile).</summary>
    public int MaxBitmapSize => _resources.MaxBitmapSize;

    /// <summary>Tài nguyên thiết bị (cùng một đối tượng qua mọi lần tạo lại; bitmap cũ vẫn được trả qua nó).</summary>
    internal DeviceResources Resources => _resources;

    public static IRenderSurface CreateForWindow(nint hwnd, RenderSurfaceOptions options) => CreateWindowSurface(hwnd, options);

    /// <summary>WARP, cho test (C-09).</summary>
    public static IRenderSurface CreateOffscreen(int pixelWidth, int pixelHeight, double dpiScale) =>
        CreateOffscreenSurface(pixelWidth, pixelHeight, dpiScale);

    /// <summary>Như <see cref="CreateForWindow"/> nhưng trả kiểu cụ thể (cho <see cref="GpuImageCache"/>).</summary>
    public static D2DRenderSurface CreateWindowSurface(nint hwnd, RenderSurfaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new D2DRenderSurface(DeviceResources.CreateForWindow(hwnd, options), options);
    }

    /// <summary>Offscreen; <paramref name="useHardware"/> = true thử GPU thật trước (đo hiệu năng), mặc định WARP.</summary>
    public static D2DRenderSurface CreateOffscreenSurface(int pixelWidth, int pixelHeight, double dpiScale, bool useHardware = false) =>
        new(DeviceResources.CreateOffscreen(pixelWidth, pixelHeight, dpiScale, preferWarp: !useHardware), new RenderSurfaceOptions(PreferWarp: !useHardware));

    public void Resize(int pixelWidth, int pixelHeight, double dpiScale)
    {
        ThrowIfInvalidThreadOrDisposed();
        if (_drawing)
        {
            throw new InvalidOperationException("Resize is not allowed between BeginDraw and EndDrawAndPresent.");
        }

        int hr = _resources.Resize(pixelWidth, pixelHeight, dpiScale);
        if (DeviceLoss.IsDeviceLost(hr))
        {
            RecreateDevice();
        }
        else
        {
            ComInterop.Check(hr);
        }
    }

    public IDrawContext BeginDraw() => BeginDrawContext();

    /// <summary>Như <see cref="BeginDraw"/>, trả kiểu cụ thể (ma trận, vẽ ảnh theo orientation).</summary>
    public D2DDrawContext BeginDrawContext()
    {
        ThrowIfInvalidThreadOrDisposed();
        if (_drawing)
        {
            throw new InvalidOperationException("BeginDraw was already called; call EndDrawAndPresent first.");
        }

        _drawing = true;
        _resources.Context.BeginDraw();
        _drawContext.Begin();
        return _drawContext;
    }

    public PresentResult EndDrawAndPresent()
    {
        ThrowIfInvalidThreadOrDisposed();
        if (!_drawing)
        {
            throw new InvalidOperationException("EndDrawAndPresent without BeginDraw.");
        }

        _drawing = false;
        bool balanced = _drawContext.End();
        long endDrawStart = System.Diagnostics.Stopwatch.GetTimestamp();
        int hr = _resources.EndDraw();
        LastEndDrawTicks = System.Diagnostics.Stopwatch.GetTimestamp() - endDrawStart;
        PresentResult result;
        if (DeviceLoss.IsDeviceLost(hr))
        {
            RecreateDevice();
            result = PresentResult.DeviceRecreated;
        }
        else if (hr < 0)
        {
            result = PresentResult.Failed;
        }
        else
        {
            result = Present();
        }

        if (!balanced)
        {
            throw new InvalidOperationException("Unbalanced PushClip/PopClip or PushOpacity/PopOpacity in the frame.");
        }

        return result;
    }

    /// <summary>
    /// Chờ waitable object của swap chain (khung kế tiếp được phép vẽ). Vòng lặp message (C-05) chờ handle này trong
    /// <c>MsgWaitForMultipleObjectsEx</c>; hàm này cho người gọi không có vòng lặp đó. Offscreen: true ngay.
    /// </summary>
    public bool WaitForNextFrame(int timeoutMilliseconds)
    {
        ThrowIfInvalidThreadOrDisposed();
        nint handle = _resources.FrameLatencyWaitHandle;
        if (handle == 0)
        {
            return true;
        }

        uint result = GraphicsKernel32.WaitForSingleObjectEx(handle, (uint)Math.Max(0, timeoutMilliseconds), alertable: true);
        return result == GraphicsKernel32.WaitObject0;
    }

    /// <summary>Chờ GPU làm xong khung đã gửi (đo "vẽ xong thật"; xem <see cref="DeviceResources.Synchronize"/>).</summary>
    public void WaitForGpu()
    {
        ThrowIfInvalidThreadOrDisposed();
        _resources.Synchronize();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _resources.Dispose();
    }

    internal void RecreateDevice()
    {
        _resources.Recreate();
        DeviceRecreated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Chẩn đoán (WP-21): Stopwatch ticks của <c>ID2D1DeviceContext::EndDraw</c> lần gần nhất.</summary>
    internal long LastEndDrawTicks { get; private set; }

    /// <summary>Chẩn đoán (WP-21): Stopwatch ticks của <c>IDXGISwapChain::Present</c> lần gần nhất.</summary>
    internal long LastPresentTicks { get; private set; }

    private PresentResult Present()
    {
        LastPresentTicks = 0;
        if (!_resources.HasSwapChain)
        {
            return PresentResult.Presented;
        }

        // AllowTearing: không chờ vblank (sync 0). Cờ DXGI_PRESENT_ALLOW_TEARING cần IDXGIFactory5 - chưa dùng (v1).
        long presentStart = System.Diagnostics.Stopwatch.GetTimestamp();
        int hr = _resources.Present(_options.AllowTearing || ImmediatePresent ? 0u : 1u);
        LastPresentTicks = System.Diagnostics.Stopwatch.GetTimestamp() - presentStart;
        if (hr == DxgiConstants.StatusOccluded)
        {
            return PresentResult.Occluded;
        }

        if (DeviceLoss.IsDeviceLost(hr))
        {
            RecreateDevice();
            return PresentResult.DeviceRecreated;
        }

        return hr < 0 ? PresentResult.Failed : PresentResult.Presented;
    }

    private void ThrowIfInvalidThreadOrDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("D2DRenderSurface must be used on the thread that created it.");
        }
    }
}
