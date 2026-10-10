using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Interop.Graphics;

namespace PhotoReview.Shell.Rendering;

/// <summary>
/// WP-15: toàn bộ tài nguyên phụ thuộc thiết bị của một bề mặt vẽ - D3D11 device (BGRA), DXGI device, D2D factory/device/
/// context, đích vẽ (back buffer của swap chain flip-model hoặc bitmap offscreen) và brush dùng chung. Mất thiết bị
/// (<c>D2DERR_RECREATE_TARGET</c>, <c>DXGI_ERROR_DEVICE_REMOVED/RESET</c>) = <see cref="Recreate"/>: bỏ hết, tạo lại cùng
/// kích thước, tăng <see cref="Generation"/> (mọi bitmap của thế hệ cũ vô hiệu).
/// Chỉ dùng trên một luồng (luồng UI của cửa sổ; factory D2D đơn luồng).
/// Quy ước COM theo WP-13b: tham số ra là con trỏ thô bọc bằng <see cref="ComInterop.Wrap{T}"/>, trả bằng <see cref="ComInterop.Release"/>.
/// </summary>
internal sealed unsafe class DeviceResources : IDisposable
{
    private const uint SwapChainBufferCount = 2;
    private const uint SwapChainFlags = DxgiConstants.SwapChainFlagFrameLatencyWaitableObject;

    private readonly nint _hwnd;
    private readonly bool _preferWarp;
    private readonly int _maxFrameLatency;

    private ID3D11Device? _d3d;
    private ID2D1Factory1? _factory;
    private ID2D1Device? _device;
    private ID2D1DeviceContext? _context;
    private ID2D1SolidColorBrush? _brush;
    private IDxgiSwapChain1? _swapChain;
    private ID2D1Bitmap1? _target;
    private ID2D1Bitmap1? _syncProbe;
    private nint _waitHandle;
    private bool _disposed;

    private DeviceResources(nint hwnd, int pixelWidth, int pixelHeight, double dpiScale, bool preferWarp, int maxFrameLatency)
    {
        _hwnd = hwnd;
        _preferWarp = preferWarp;
        _maxFrameLatency = Math.Clamp(maxFrameLatency, 1, 16);
        PixelWidth = Math.Max(1, pixelWidth);
        PixelHeight = Math.Max(1, pixelHeight);
        DpiScale = dpiScale > 0 ? dpiScale : 1;
    }

    public nint Hwnd => _hwnd;

    public int PixelWidth { get; private set; }

    public int PixelHeight { get; private set; }

    public double DpiScale { get; private set; }

    public bool IsWarp { get; private set; }

    /// <summary>Tăng mỗi lần tạo lại thiết bị; bitmap tạo ở thế hệ khác không được vẽ.</summary>
    public long Generation { get; private set; }

    /// <summary>ID2D1RenderTarget::GetMaximumBitmapSize (16384 với FL 11, 4096 với FL 9.3).</summary>
    public int MaxBitmapSize { get; private set; }

    /// <summary>Waitable object của swap chain (0 với offscreen). Thuộc về đối tượng này: đóng khi tạo lại/Dispose.</summary>
    public nint FrameLatencyWaitHandle => _waitHandle;

    /// <summary>Bitmap D2D đang sống do <see cref="CreateBitmap"/> tạo (để test rò rỉ GPU, kể cả qua tạo lại thiết bị).</summary>
    public long LiveBitmapCount { get; private set; }

    /// <summary>Seam test (thẻ WP-15): nhận HRESULT thật của EndDraw, trả HRESULT dùng tiếp (vd. ép D2DERR_RECREATE_TARGET).</summary>
    internal Func<int, int>? EndDrawFault { get; set; }

    /// <summary>Seam test: như <see cref="EndDrawFault"/> cho Present (vd. ép DXGI_ERROR_DEVICE_REMOVED).</summary>
    internal Func<int, int>? PresentFault { get; set; }

    public ID2D1DeviceContext Context => _context ?? throw new ObjectDisposedException(nameof(DeviceResources));

    public ID2D1SolidColorBrush Brush => _brush ?? throw new ObjectDisposedException(nameof(DeviceResources));

    public ID2D1Bitmap1 Target => _target ?? throw new ObjectDisposedException(nameof(DeviceResources));

    public bool HasSwapChain => _swapChain is not null;

    /// <summary>IDXGISwapChain2::GetMaximumFrameLatency (0 với offscreen) - để test kiểm "không quá 1 khung đang xử lý".</summary>
    internal uint QueryMaximumFrameLatency()
    {
        if (_swapChain is null)
        {
            return 0;
        }

        ComInterop.Check(((IDxgiSwapChain2)(object)_swapChain).GetMaximumFrameLatency(out uint latency));
        return latency;
    }

    public static DeviceResources CreateForWindow(nint hwnd, RenderSurfaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (hwnd == 0)
        {
            throw new ArgumentException("A window handle is required.", nameof(hwnd));
        }

        Rect client;
        int width = 1;
        int height = 1;
        if (User32.GetClientRect(hwnd, &client))
        {
            width = client.Right - client.Left;
            height = client.Bottom - client.Top;
        }

        uint dpi = User32.GetDpiForWindow(hwnd);
        var resources = new DeviceResources(hwnd, width, height, dpi == 0 ? 1 : dpi / 96.0, options.PreferWarp, options.MaxFrameLatency);
        resources.CreateAll();
        return resources;
    }

    public static DeviceResources CreateOffscreen(int pixelWidth, int pixelHeight, double dpiScale, bool preferWarp)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);
        var resources = new DeviceResources(0, pixelWidth, pixelHeight, dpiScale, preferWarp, 1);
        resources.CreateAll();
        return resources;
    }

    /// <summary>
    /// Bitmap BGRA 96 DPI (DIP của bitmap = pixel). <paramref name="data"/> null = nội dung chưa khởi tạo (ghi sau bằng
    /// CopyFromMemory). <paramref name="alphaMode"/>: Premultiplied cho Pbgra32, Ignore cho Bgr32 (byte X bị bỏ qua).
    /// </summary>
    public ID2D1Bitmap1 CreateBitmap(int width, int height, void* data, uint pitch, D2dAlphaMode alphaMode, D2dBitmapOptions options)
    {
        D2dBitmapProperties1 props = new()
        {
            PixelFormat = new D2dPixelFormat { Format = DxgiFormat.B8G8R8A8Unorm, AlphaMode = alphaMode },
            DpiX = 96,
            DpiY = 96,
            BitmapOptions = options,
        };
        ComInterop.Check(Context.CreateBitmapEx(new D2dSizeU((uint)width, (uint)height), data, pitch, &props, out nint raw));
        ID2D1Bitmap1 bitmap = ComInterop.Wrap<ID2D1Bitmap1>(raw);
        LiveBitmapCount++;
        return bitmap;
    }

    /// <summary>Trả bitmap do <see cref="CreateBitmap"/> tạo (kể cả bitmap của thế hệ thiết bị cũ).</summary>
    public void ReleaseBitmap(ID2D1Bitmap1? bitmap)
    {
        if (bitmap is null)
        {
            return;
        }

        ComInterop.Release(bitmap);
        LiveBitmapCount--;
    }

    /// <summary>Đổi kích thước đích. Trả HRESULT của ResizeBuffers (mất thiết bị -&gt; người gọi tạo lại).</summary>
    public int Resize(int pixelWidth, int pixelHeight, double dpiScale)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PixelWidth = Math.Max(1, pixelWidth);
        PixelHeight = Math.Max(1, pixelHeight);
        DpiScale = dpiScale > 0 ? dpiScale : 1;
        ReleaseTarget();
        if (_swapChain is not null)
        {
            D3D11.ClearStateAndFlush(_d3d!);
            int hr = _swapChain.ResizeBuffers(0, (uint)PixelWidth, (uint)PixelHeight, DxgiFormat.Unknown, SwapChainFlags);
            if (hr < 0)
            {
                return hr;
            }
        }

        CreateTarget();
        return 0;
    }

    /// <summary>EndDraw qua seam lỗi. Bỏ trạng thái vẽ dở dang của context cũng được (BeginDraw kế tiếp làm lại).</summary>
    public int EndDraw()
    {
        int hr = Context.EndDraw(null, null);
        return EndDrawFault is { } fault ? fault(hr) : hr;
    }

    /// <summary>Present(sync, 0) qua seam lỗi. Offscreen: 0.</summary>
    public int Present(uint syncInterval)
    {
        if (_swapChain is null)
        {
            return 0;
        }

        int hr = _swapChain.Present(syncInterval, 0);
        return PresentFault is { } fault ? fault(hr) : hr;
    }

    /// <summary>Bỏ mọi tài nguyên của thiết bị hiện tại và tạo lại (cùng HWND/kích thước/DPI); <see cref="Generation"/> + 1.</summary>
    public void Recreate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReleaseAll();
        Generation++;
        CreateAll();
    }

    /// <summary>
    /// Chờ GPU làm xong mọi lệnh đã gửi (sao 1 pixel của đích sang bitmap CPU-read rồi Map). Chỉ dùng để đo thời gian
    /// "vẽ xong thật" và trước khi đọc lại pixel; không gọi trên đường vẽ bình thường.
    /// </summary>
    public void Synchronize()
    {
        _syncProbe ??= CreateBitmap(1, 1, null, 0, D2dAlphaMode.Premultiplied, D2dBitmapOptions.CpuRead | D2dBitmapOptions.CannotDraw);
        D2dPointU origin = default;
        D2dRectU source = new() { Left = 0, Top = 0, Right = 1, Bottom = 1 };
        ComInterop.Check(_syncProbe.CopyFromBitmap(&origin, Target, &source));
        D2dMappedRect mapped;
        ComInterop.Check(_syncProbe.Map(D2dMapOptions.Read, &mapped));
        ComInterop.Check(_syncProbe.Unmap());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseAll();
    }

    private void CreateAll()
    {
        CreateDevice();
        if (_hwnd != 0)
        {
            CreateSwapChain();
        }

        CreateTarget();
    }

    private void CreateDevice()
    {
        ID3D11Device? d3d = null;
        int hr = -1;
        if (!_preferWarp)
        {
            hr = D3D11.CreateDevice(D3DDriverType.Hardware, D3D11Constants.CreateDeviceBgraSupport, out d3d, out _);
        }

        IsWarp = false;
        if (hr < 0 || d3d is null)
        {
            // Không có GPU / RDP / test: WARP (rasterizer CPU của D3D11).
            ComInterop.Check(D3D11.CreateDevice(D3DDriverType.Warp, D3D11Constants.CreateDeviceBgraSupport, out d3d, out _));
            IsWarp = true;
        }

        _d3d = d3d!;
        _factory = D2D1.CreateFactory(D2dFactoryType.SingleThreaded);

        // Cùng một wrapper COM với _d3d (cast = QueryInterface); không Release riêng.
        var dxgi = (IDxgiDevice)(object)_d3d;
        ComInterop.Check(_factory.CreateDevice(dxgi, out nint deviceRaw));
        _device = ComInterop.Wrap<ID2D1Device>(deviceRaw);
        ComInterop.Check(_device.CreateDeviceContext(0, out nint contextRaw));
        _context = ComInterop.Wrap<ID2D1DeviceContext>(contextRaw);
        MaxBitmapSize = (int)Math.Min(_context.GetMaximumBitmapSize(), int.MaxValue);

        D2dColorF white = new(1, 1, 1, 1);
        ComInterop.Check(_context.CreateSolidColorBrush(&white, 0, out nint brushRaw));
        _brush = ComInterop.Wrap<ID2D1SolidColorBrush>(brushRaw);
    }

    private void CreateSwapChain()
    {
        var dxgi = (IDxgiDevice)(object)_d3d!;
        ComInterop.Check(dxgi.GetAdapter(out nint adapterRaw));
        IDxgiAdapter adapter = ComInterop.Wrap<IDxgiAdapter>(adapterRaw);
        try
        {
            ComInterop.Check(adapter.GetParent(GraphicsGuids.IidDxgiFactory2, out nint factoryRaw));
            IDxgiFactory2 factory = ComInterop.Wrap<IDxgiFactory2>(factoryRaw);
            try
            {
                DxgiSwapChainDesc1 desc = new()
                {
                    Width = (uint)PixelWidth,
                    Height = (uint)PixelHeight,
                    Format = DxgiFormat.B8G8R8A8Unorm,
                    SampleDesc = new DxgiSampleDesc { Count = 1, Quality = 0 },
                    BufferUsage = DxgiConstants.UsageRenderTargetOutput,
                    BufferCount = SwapChainBufferCount,
                    Scaling = DxgiScaling.Stretch,
                    SwapEffect = DxgiSwapEffect.FlipDiscard,
                    AlphaMode = DxgiAlphaMode.Ignore,
                    Flags = SwapChainFlags,
                };
                ComInterop.Check(factory.CreateSwapChainForHwnd(_d3d!, _hwnd, &desc, 0, 0, out nint swapChainRaw));
                _swapChain = ComInterop.Wrap<IDxgiSwapChain1>(swapChainRaw);
            }
            finally
            {
                ComInterop.Release(factory);
            }
        }
        finally
        {
            ComInterop.Release(adapter);
        }

        // Không quá MaxFrameLatency khung chờ trình bày; vòng lặp message (C-05) chờ waitable object trước mỗi khung.
        var swapChain2 = (IDxgiSwapChain2)(object)_swapChain;
        ComInterop.Check(swapChain2.SetMaximumFrameLatency((uint)_maxFrameLatency));
        _waitHandle = swapChain2.GetFrameLatencyWaitableObject();
    }

    private void CreateTarget()
    {
        float dpi = (float)(96 * DpiScale);
        D2dBitmapProperties1 props = new()
        {
            PixelFormat = new D2dPixelFormat
            {
                Format = DxgiFormat.B8G8R8A8Unorm,
                AlphaMode = _swapChain is null ? D2dAlphaMode.Premultiplied : D2dAlphaMode.Ignore,
            },
            DpiX = dpi,
            DpiY = dpi,
            BitmapOptions = D2dBitmapOptions.Target | (_swapChain is null ? D2dBitmapOptions.None : D2dBitmapOptions.CannotDraw),
        };

        nint raw;
        if (_swapChain is null)
        {
            ComInterop.Check(Context.CreateBitmapEx(new D2dSizeU((uint)PixelWidth, (uint)PixelHeight), null, 0, &props, out raw));
        }
        else
        {
            ComInterop.Check(_swapChain.GetBuffer(0, GraphicsGuids.IidDxgiSurface, out nint surfaceRaw));
            IDxgiSurface surface = ComInterop.Wrap<IDxgiSurface>(surfaceRaw);
            try
            {
                ComInterop.Check(Context.CreateBitmapFromDxgiSurface(surface, &props, out raw));
            }
            finally
            {
                ComInterop.Release(surface);
            }
        }

        _target = ComInterop.Wrap<ID2D1Bitmap1>(raw);
        Context.SetTarget(_target);
        Context.SetDpi(dpi, dpi);
    }

    private void ReleaseTarget()
    {
        _context?.SetTarget(null);
        ComInterop.Release(_target);
        _target = null;
    }

    private void ReleaseAll()
    {
        ReleaseTarget();
        if (_syncProbe is not null)
        {
            ReleaseBitmap(_syncProbe);
            _syncProbe = null;
        }

        if (_waitHandle != 0)
        {
            GraphicsKernel32.CloseHandle(_waitHandle);
            _waitHandle = 0;
        }

        ComInterop.Release(_swapChain);
        _swapChain = null;
        ComInterop.Release(_brush);
        _brush = null;
        ComInterop.Release(_context);
        _context = null;
        ComInterop.Release(_device);
        _device = null;
        ComInterop.Release(_factory);
        _factory = null;
        if (_d3d is not null)
        {
            D3D11.ClearStateAndFlush(_d3d);
            ComInterop.Release(_d3d);
            _d3d = null;
        }
    }
}

/// <summary>HRESULT mà renderer xử lý bằng cách tạo lại thiết bị.</summary>
internal static class DeviceLoss
{
    public static bool IsDeviceLost(int hr) =>
        hr == D2dConstants.ErrorRecreateTarget || hr == DxgiConstants.ErrorDeviceRemoved || hr == DxgiConstants.ErrorDeviceReset;

    public static string Describe(int hr) => "0x" + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Ánh xạ C-09 -&gt; D2D1_INTERPOLATION_MODE.</summary>
internal static class InterpolationModes
{
    public static D2dInterpolationMode ToD2D(ImageInterpolation interpolation) => interpolation switch
    {
        ImageInterpolation.NearestNeighbor => D2dInterpolationMode.NearestNeighbor,
        ImageInterpolation.Linear => D2dInterpolationMode.Linear,
        ImageInterpolation.HighQualityCubic => D2dInterpolationMode.HighQualityCubic,
        _ => throw new ArgumentOutOfRangeException(nameof(interpolation), interpolation, null),
    };
}
