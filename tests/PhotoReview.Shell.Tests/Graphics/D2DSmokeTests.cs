using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop.Graphics;

namespace PhotoReview.Shell.Tests.Graphics;

/// <summary>
/// WP-13b: smoke test interop D3D11/DXGI/D2D1/DWrite trên WARP offscreen (không cửa sổ, không GPU thật).
/// Sai GUID = QueryInterface/tạo factory thất bại; sai thứ tự/slot vtable = pixel/kích thước đọc lại sai hoặc
/// access violation - đó là lý do test này bắt buộc (NO-WPF-EXEC-PLAN-WP, thẻ WP-13b).
/// </summary>
[Trait("Category", "Native")]
public sealed unsafe class D2DSmokeTests
{
    private const uint Size = 4;

    /// <summary>Bộ D3D11 WARP + DXGI device + D2D factory/device/context; Dispose trả mọi ref COM ngay.</summary>
    private sealed class Rig : IDisposable
    {
        public ID3D11Device D3D { get; }
        public IDxgiDevice Dxgi { get; }
        public ID2D1Factory1 Factory { get; }
        public ID2D1Device Device { get; }
        public ID2D1DeviceContext Context { get; }
        public int FeatureLevel { get; }

        public Rig()
        {
            int hr = D3D11.CreateDevice(D3DDriverType.Warp, D3D11Constants.CreateDeviceBgraSupport, out ID3D11Device? d3d,
                out int level);
            Assert.True(hr >= 0, $"D3D11CreateDevice(WARP) HRESULT 0x{hr:X8}");
            D3D = d3d!;
            FeatureLevel = level;
            Dxgi = (IDxgiDevice)(object)D3D;
            Factory = D2D1.CreateFactory(D2dFactoryType.SingleThreaded);
            ComInterop.Check(Factory.CreateDevice(Dxgi, out nint deviceRaw));
            ID2D1Device device = ComInterop.Wrap<ID2D1Device>(deviceRaw);
            Device = device;
            ComInterop.Check(Device.CreateDeviceContext(0, out nint contextRaw));
            ID2D1DeviceContext context = ComInterop.Wrap<ID2D1DeviceContext>(contextRaw);
            Context = context;
        }

        public void Dispose()
        {
            ComInterop.Release(Context);
            ComInterop.Release(Device);
            ComInterop.Release(Factory);
            ComInterop.Release(Dxgi);
            ComInterop.Release(D3D);
        }
    }

    private static D2dBitmapProperties1 TargetProps(D2dBitmapOptions options) => new()
    {
        PixelFormat = new D2dPixelFormat { Format = DxgiFormat.B8G8R8A8Unorm, AlphaMode = D2dAlphaMode.Premultiplied },
        DpiX = 96,
        DpiY = 96,
        BitmapOptions = options,
    };

    private static ID2D1Bitmap1 CreateTarget(ID2D1DeviceContext context, uint width, uint height)
    {
        D2dBitmapProperties1 props = TargetProps(D2dBitmapOptions.Target);
        ComInterop.Check(context.CreateBitmapEx(new D2dSizeU(width, height), null, 0, &props, out nint bitmapRaw));
        ID2D1Bitmap1 bitmap = ComInterop.Wrap<ID2D1Bitmap1>(bitmapRaw);
        return bitmap;
    }

    /// <summary>Đọc lại pixel BGRA (premultiplied) của <paramref name="source"/> qua bitmap CPU-read.</summary>
    private static byte[] ReadPixels(ID2D1DeviceContext context, ID2D1Bitmap1 source, uint width, uint height)
    {
        D2dBitmapProperties1 props = TargetProps(D2dBitmapOptions.CpuRead | D2dBitmapOptions.CannotDraw);
        ComInterop.Check(context.CreateBitmapEx(new D2dSizeU(width, height), null, 0, &props, out nint cpuRaw));
        ID2D1Bitmap1 cpu = ComInterop.Wrap<ID2D1Bitmap1>(cpuRaw);
        try
        {
            ComInterop.Check(cpu.CopyFromBitmap(null, source, null));
            D2dMappedRect mapped;
            ComInterop.Check(cpu.Map(D2dMapOptions.Read, &mapped));
            try
            {
                byte[] result = new byte[width * height * 4];
                for (uint y = 0; y < height; y++)
                {
                    new ReadOnlySpan<byte>(mapped.Bits + (y * mapped.Pitch), (int)(width * 4))
                        .CopyTo(result.AsSpan((int)(y * width * 4)));
                }

                return result;
            }
            finally
            {
                ComInterop.Check(cpu.Unmap());
            }
        }
        finally
        {
            ComInterop.Release(cpu);
        }
    }

    /// <summary>SetTarget + BeginDraw ... EndDraw (kiểm HRESULT) + SetTarget(null).</summary>
    private readonly struct DrawScope : IDisposable
    {
        private readonly ID2D1DeviceContext _context;

        public DrawScope(ID2D1DeviceContext context, ID2D1Image target)
        {
            _context = context;
            context.SetTarget(target);
            context.BeginDraw();
        }

        public void Dispose()
        {
            int hr = _context.EndDraw(null, null);
            _context.SetTarget(null);
            ComInterop.Check(hr);
        }
    }

    private static void AssertPixel(byte[] bgra, uint width, uint x, uint y, byte b, byte g, byte r, byte a)
    {
        int offset = (int)(((y * width) + x) * 4);
        Assert.Equal((b, g, r, a), (bgra[offset], bgra[offset + 1], bgra[offset + 2], bgra[offset + 3]));
    }

    private static void DrawBlueWithGreenQuarter(ID2D1DeviceContext context, ID2D1Bitmap1 target)
    {
        D2dColorF blue = new(0, 0, 1, 1);
        D2dColorF green = new(0, 1, 0, 1);
        D2dRectF quarter = new(0, 0, 2, 2);
        ComInterop.Check(context.CreateSolidColorBrush(&green, 0, out nint brushRaw));
        ID2D1SolidColorBrush brush = ComInterop.Wrap<ID2D1SolidColorBrush>(brushRaw);
        try
        {
            using (new DrawScope(context, target))
            {
                context.Clear(&blue);
                context.FillRectangle(&quarter, brush);
            }
        }
        finally
        {
            ComInterop.Release(brush);
        }
    }

    [Fact]
    public void Warp_Device_IsCreatedWithFeatureLevelAndAlive()
    {
        using Rig rig = new();

        Assert.True(rig.FeatureLevel >= D3D11Constants.FeatureLevel93, $"feature level 0x{rig.FeatureLevel:X}");
        Assert.Equal(rig.FeatureLevel, rig.D3D.GetFeatureLevel());
        Assert.Equal(0, rig.D3D.GetDeviceRemovedReason());
    }

    [Fact]
    public void Offscreen_Bitmap_DrawAndReadBackPixels()
    {
        using Rig rig = new();
        ID2D1Bitmap1 target = CreateTarget(rig.Context, Size, Size);
        try
        {
            DrawBlueWithGreenQuarter(rig.Context, target);

            byte[] pixels = ReadPixels(rig.Context, target, Size, Size);
            AssertPixel(pixels, Size, 0, 0, 0, 255, 0, 255);   // xanh lá
            AssertPixel(pixels, Size, 1, 1, 0, 255, 0, 255);
            AssertPixel(pixels, Size, 2, 2, 255, 0, 0, 255);   // xanh dương (BGRA: B=255)
            AssertPixel(pixels, Size, 3, 3, 255, 0, 0, 255);
            AssertPixel(pixels, Size, 3, 0, 255, 0, 0, 255);
        }
        finally
        {
            ComInterop.Release(target);
        }
    }

    [Fact]
    public void Bitmap_StructReturningMethods_ReportSizeAndFormat()
    {
        using Rig rig = new();
        ID2D1Bitmap1 bitmap = CreateTarget(rig.Context, 6, 3);
        try
        {
            D2dSizeU pixels = bitmap.GetPixelSize();
            D2dSizeF dips = bitmap.GetSize();
            D2dPixelFormat format = bitmap.GetPixelFormat();
            bitmap.GetDpi(out float dpiX, out float dpiY);

            Assert.Equal((6u, 3u), (pixels.Width, pixels.Height));
            Assert.Equal((6f, 3f), (dips.Width, dips.Height));
            Assert.Equal(DxgiFormat.B8G8R8A8Unorm, format.Format);
            Assert.Equal(D2dAlphaMode.Premultiplied, format.AlphaMode);
            Assert.Equal((96f, 96f), (dpiX, dpiY));
            Assert.Equal(D2dBitmapOptions.Target, bitmap.GetOptions());
        }
        finally
        {
            ComInterop.Release(bitmap);
        }
    }

    [Fact]
    public void DeviceContext_StateSetters_RoundTrip()
    {
        using Rig rig = new();
        ID2D1DeviceContext context = rig.Context;

        D2dMatrix3x2F matrix = new() { M11 = 2, M12 = 0, M21 = 0, M22 = 3, Dx = 5, Dy = 7 };
        context.SetTransform(&matrix);
        D2dMatrix3x2F read;
        context.GetTransform(&read);
        Assert.Equal((2f, 3f, 5f, 7f), (read.M11, read.M22, read.Dx, read.Dy));

        context.SetAntialiasMode(D2dAntialiasMode.Aliased);
        Assert.Equal(D2dAntialiasMode.Aliased, context.GetAntialiasMode());
        context.SetTextAntialiasMode(D2dTextAntialiasMode.Grayscale);
        Assert.Equal(D2dTextAntialiasMode.Grayscale, context.GetTextAntialiasMode());
        context.SetUnitMode(D2dUnitMode.Pixels);
        Assert.Equal(D2dUnitMode.Pixels, context.GetUnitMode());
        context.SetPrimitiveBlend(D2dPrimitiveBlend.Copy);
        Assert.Equal(D2dPrimitiveBlend.Copy, context.GetPrimitiveBlend());
        context.SetDpi(120, 144);
        context.GetDpi(out float dpiX, out float dpiY);
        Assert.Equal((120f, 144f), (dpiX, dpiY));
        Assert.True(context.GetMaximumBitmapSize() >= 4096);
    }

    [Fact]
    public void DrawBitmap_Scales_FromSourceToDestination()
    {
        using Rig rig = new();
        ID2D1DeviceContext context = rig.Context;
        ID2D1Bitmap1 source = CreateTarget(context, 2, 2);
        ID2D1Bitmap1 destination = CreateTarget(context, Size, Size);
        try
        {
            D2dColorF red = new(1, 0, 0, 1);
            D2dColorF clear = new(0, 0, 0, 0);
            using (new DrawScope(context, source))
            {
                context.Clear(&red);
            }

            D2dRectF dest = new(1, 1, 3, 3);
            using (new DrawScope(context, destination))
            {
                context.Clear(&clear);
                context.DrawBitmapEx(source, &dest, 1f, D2dInterpolationMode.NearestNeighbor, null, 0);
            }

            byte[] pixels = ReadPixels(context, destination, Size, Size);
            AssertPixel(pixels, Size, 0, 0, 0, 0, 0, 0);
            AssertPixel(pixels, Size, 1, 1, 0, 0, 255, 255);   // đỏ (BGRA: R=255)
            AssertPixel(pixels, Size, 2, 2, 0, 0, 255, 255);
            AssertPixel(pixels, Size, 3, 3, 0, 0, 0, 0);
        }
        finally
        {
            ComInterop.Release(destination);
            ComInterop.Release(source);
        }
    }

    [Fact]
    public void BitmapFromD3DTexture_ViaDxgiSurface_DrawAndReadBack()
    {
        using Rig rig = new();
        D3D11Texture2DDesc desc = new()
        {
            Width = Size,
            Height = Size,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.B8G8R8A8Unorm,
            SampleDesc = new DxgiSampleDesc { Count = 1, Quality = 0 },
            Usage = D3D11Constants.UsageDefault,
            BindFlags = D3D11Constants.BindRenderTarget | D3D11Constants.BindShaderResource,
        };
        ComInterop.Check(rig.D3D.CreateTexture2D(&desc, 0, out nint texture));
        nint surfaceRaw = ComInterop.QueryInterface(texture, GraphicsGuids.IidDxgiSurface);
        Marshal.Release(texture);
        IDxgiSurface surface = ComInterop.Wrap<IDxgiSurface>(surfaceRaw);
        D2dBitmapProperties1 props = TargetProps(D2dBitmapOptions.Target | D2dBitmapOptions.CannotDraw);
        ComInterop.Check(rig.Context.CreateBitmapFromDxgiSurface(surface, &props, out nint bitmapRaw));
        ID2D1Bitmap1 bitmap = ComInterop.Wrap<ID2D1Bitmap1>(bitmapRaw);
        try
        {
            ComInterop.Check(surface.GetDesc(out DxgiSurfaceDesc surfaceDesc));
            Assert.Equal((Size, Size, DxgiFormat.B8G8R8A8Unorm), (surfaceDesc.Width, surfaceDesc.Height, surfaceDesc.Format));

            DrawBlueWithGreenQuarter(rig.Context, bitmap);
            byte[] pixels = ReadPixels(rig.Context, bitmap, Size, Size);
            AssertPixel(pixels, Size, 0, 0, 0, 255, 0, 255);
            AssertPixel(pixels, Size, 3, 3, 255, 0, 0, 255);
        }
        finally
        {
            ComInterop.Release(bitmap);
            ComInterop.Release(surface);
        }
    }

    [Fact]
    public void SwapChain_ForComposition_PresentsAndResizesWithWaitableObject()
    {
        using Rig rig = new();
        ComInterop.Check(rig.Dxgi.GetAdapter(out nint adapterRaw));
        IDxgiAdapter adapter = ComInterop.Wrap<IDxgiAdapter>(adapterRaw);
        ComInterop.Check(adapter.GetParent(GraphicsGuids.IidDxgiFactory2, out nint factoryRaw));
        IDxgiFactory2 factory = ComInterop.Wrap<IDxgiFactory2>(factoryRaw);
        DxgiSwapChainDesc1 desc = new()
        {
            Width = 16,
            Height = 16,
            Format = DxgiFormat.B8G8R8A8Unorm,
            SampleDesc = new DxgiSampleDesc { Count = 1, Quality = 0 },
            BufferUsage = DxgiConstants.UsageRenderTargetOutput,
            BufferCount = 2,
            Scaling = DxgiScaling.Stretch,
            SwapEffect = DxgiSwapEffect.FlipSequential,
            AlphaMode = DxgiAlphaMode.Premultiplied,
            Flags = DxgiConstants.SwapChainFlagFrameLatencyWaitableObject,
        };
        ComInterop.Check(factory.CreateSwapChainForComposition(rig.D3D, &desc, 0, out nint swapChainRaw));
        IDxgiSwapChain1 swapChain = ComInterop.Wrap<IDxgiSwapChain1>(swapChainRaw);
        try
        {
            IDxgiSwapChain2 swapChain2 = (IDxgiSwapChain2)(object)swapChain;
            ComInterop.Check(swapChain2.SetMaximumFrameLatency(1));
            ComInterop.Check(swapChain2.GetMaximumFrameLatency(out uint latency));
            Assert.Equal(1u, latency);
            Assert.NotEqual(0, swapChain2.GetFrameLatencyWaitableObject());

            ComInterop.Check(swapChain.GetDesc1(out DxgiSwapChainDesc1 actual));
            Assert.Equal((16u, 16u, 2u), (actual.Width, actual.Height, actual.BufferCount));

            DrawIntoBackBufferAndPresent(rig, swapChain, 16);
            DxgiPresentParameters presentParameters = default;
            Assert.True(swapChain.Present1(0, 0, &presentParameters) >= 0);

            // Back buffer phải được trả hết ref (wrapper D2D đã Release) VÀ pipeline D3D đã flush thì
            // ResizeBuffers mới thành công.
            Assert.Equal(0u, D3D11.GetImmediateContextType(rig.D3D));
            D3D11.ClearStateAndFlush(rig.D3D);
            ComInterop.Check(swapChain.ResizeBuffers(0, 32, 8, DxgiFormat.Unknown,
                DxgiConstants.SwapChainFlagFrameLatencyWaitableObject));
            ComInterop.Check(swapChain.GetDesc1(out actual));
            Assert.Equal((32u, 8u), (actual.Width, actual.Height));

            DrawIntoBackBufferAndPresent(rig, swapChain, 32);
        }
        finally
        {
            ComInterop.Release(swapChain);
            ComInterop.Release(factory);
            ComInterop.Release(adapter);
        }
    }

    private static void DrawIntoBackBufferAndPresent(Rig rig, IDxgiSwapChain1 swapChain, uint expectedWidth)
    {
        ComInterop.Check(swapChain.GetBuffer(0, GraphicsGuids.IidDxgiSurface, out nint surfaceRaw));
        IDxgiSurface surface = ComInterop.Wrap<IDxgiSurface>(surfaceRaw);
        D2dBitmapProperties1 props = TargetProps(D2dBitmapOptions.Target | D2dBitmapOptions.CannotDraw);
        ComInterop.Check(rig.Context.CreateBitmapFromDxgiSurface(surface, &props, out nint bitmapRaw));
        ID2D1Bitmap1 bitmap = ComInterop.Wrap<ID2D1Bitmap1>(bitmapRaw);
        try
        {
            Assert.Equal(expectedWidth, bitmap.GetPixelSize().Width);
            D2dColorF color = new(1, 1, 0, 1);
            using (new DrawScope(rig.Context, bitmap))
            {
                rig.Context.Clear(&color);
            }
        }
        finally
        {
            ComInterop.Release(bitmap);
            ComInterop.Release(surface);
        }

        int hr = swapChain.Present(0, 0);
        Assert.True(hr >= 0, $"Present HRESULT 0x{hr:X8}");
    }

    [Fact]
    public void DirectWrite_VietnameseLayout_MeasuresAndHitTests()
    {
        IDWriteFactory factory = DWrite.CreateFactory();
        try
        {
            const string Text = "Tiếng Việt";
            ComInterop.Check(factory.CreateTextFormat("Segoe UI", 0, DWriteFontWeight.Regular, DWriteFontStyle.Normal,
                DWriteFontStretch.Normal, 16f, "vi-VN", out nint formatRaw));
            IDWriteTextFormat format = ComInterop.Wrap<IDWriteTextFormat>(formatRaw);
            Assert.Equal(16f, format.GetFontSize());
            Assert.Equal(DWriteFontWeight.Regular, format.GetFontWeight());
            ComInterop.Check(format.SetTextAlignment(DWriteTextAlignment.Center));
            Assert.Equal(DWriteTextAlignment.Center, format.GetTextAlignment());

            ComInterop.Check(factory.CreateTextLayout(Text, (uint)Text.Length, format, 1000f, 100f, out nint layoutRaw));
            IDWriteTextLayout layout = ComInterop.Wrap<IDWriteTextLayout>(layoutRaw);
            try
            {
                ComInterop.Check(layout.GetMetrics(out DWriteTextMetrics metrics));
                Assert.True(metrics.Width > 0 && metrics.Height > 0, $"{metrics.Width}x{metrics.Height}");
                Assert.Equal(1u, metrics.LineCount);
                Assert.Equal(1000f, layout.GetMaxWidth());

                DWriteLineMetrics line;
                ComInterop.Check(layout.GetLineMetrics(&line, 1, out uint lineCount));
                Assert.Equal(1u, lineCount);
                Assert.Equal((uint)Text.Length, line.Length);
                Assert.True(line.Baseline > 0);

                ComInterop.Check(layout.HitTestTextPosition((uint)Text.Length, 0, out float endX, out _, out _));
                Assert.True(endX > 0);
                ComInterop.Check(layout.HitTestPoint(endX + 500f, 1f, out _, out int inside, out DWriteHitTestMetrics hit));
                Assert.Equal(0, inside);
                Assert.True(hit.TextPosition <= Text.Length);

                // Hẹp lại thì phải xuống dòng, và đổi cỡ chữ cho một đoạn phải tăng chiều rộng.
                float narrow = metrics.Width / 3f;
                ComInterop.Check(layout.SetMaxWidth(narrow));
                ComInterop.Check(layout.GetMetrics(out DWriteTextMetrics wrapped));
                Assert.True(wrapped.LineCount > 1, $"lineCount={wrapped.LineCount}");

                ComInterop.Check(layout.SetMaxWidth(1000f));
                ComInterop.Check(layout.SetFontSize(32f, new DWriteTextRange(0, (uint)Text.Length)));
                ComInterop.Check(layout.GetMetrics(out DWriteTextMetrics bigger));
                Assert.True(bigger.Width > metrics.Width * 1.5f);
            }
            finally
            {
                ComInterop.Release(layout);
                ComInterop.Release(format);
            }
        }
        finally
        {
            ComInterop.Release(factory);
        }
    }

    [Fact]
    public void DirectWrite_TextDrawnWithD2D_ProducesInk()
    {
        using Rig rig = new();
        IDWriteFactory factory = DWrite.CreateFactory();
        ID2D1Bitmap1 target = CreateTarget(rig.Context, 96, 32);
        try
        {
            const string Text = "Tiếng Việt";
            ComInterop.Check(factory.CreateTextFormat("Segoe UI", 0, DWriteFontWeight.Bold, DWriteFontStyle.Normal,
                DWriteFontStretch.Normal, 18f, "vi-VN", out nint formatRaw));
            IDWriteTextFormat format = ComInterop.Wrap<IDWriteTextFormat>(formatRaw);
            ComInterop.Check(factory.CreateTextLayout(Text, (uint)Text.Length, format, 96f, 32f, out nint layoutRaw));
            IDWriteTextLayout layout = ComInterop.Wrap<IDWriteTextLayout>(layoutRaw);
            D2dColorF black = new(0, 0, 0, 1);
            D2dColorF white = new(1, 1, 1, 1);
            ComInterop.Check(rig.Context.CreateSolidColorBrush(&black, 0, out nint brushRaw));
            ID2D1SolidColorBrush brush = ComInterop.Wrap<ID2D1SolidColorBrush>(brushRaw);
            try
            {
                using (new DrawScope(rig.Context, target))
                {
                    rig.Context.Clear(&white);
                    rig.Context.DrawTextLayout(new D2dPointF(0, 0), layout, brush, D2dDrawTextOptions.None);
                }

                byte[] pixels = ReadPixels(rig.Context, target, 96, 32);
                int dark = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i + 2] < 128)
                    {
                        dark++;
                    }
                }

                Assert.True(dark > 30, $"chỉ {dark} pixel tối - chữ không được vẽ");
            }
            finally
            {
                ComInterop.Release(brush);
                ComInterop.Release(layout);
                ComInterop.Release(format);
            }
        }
        finally
        {
            ComInterop.Release(target);
            ComInterop.Release(factory);
        }
    }

    [Fact]
    public void Dxgi_CreateFactory2_ReturnsFactory2()
    {
        int hr = Dxgi.CreateDxgiFactory2(0, GraphicsGuids.IidDxgiFactory2, out nint raw);
        Assert.True(hr >= 0, $"CreateDXGIFactory2 0x{hr:X8}");
        IDxgiFactory2 factory = ComInterop.Wrap<IDxgiFactory2>(raw);
        try
        {
            Assert.NotNull(factory);
            // IsCurrent của IDXGIFactory1 (slot 13) chạm vào đúng vtable cơ sở.
            Assert.True(((IDxgiFactory1)factory).IsCurrent() != 0);
        }
        finally
        {
            ComInterop.Release(factory);
        }
    }

    [Fact]
    public void Brush_Primitives_ClipAndCopyFromMemory_Work()
    {
        using Rig rig = new();
        ID2D1DeviceContext context = rig.Context;
        D2dColorF black = new(0, 0, 0, 1);
        D2dColorF white = new(1, 1, 1, 1);
        ComInterop.Check(context.CreateSolidColorBrush(&black, 0, out nint brushRaw));
        ID2D1SolidColorBrush brush = ComInterop.Wrap<ID2D1SolidColorBrush>(brushRaw);
        ID2D1Bitmap1 target = CreateTarget(context, 8, 8);
        try
        {
            brush.SetOpacity(0.5f);
            Assert.Equal(0.5f, brush.GetOpacity());
            brush.SetOpacity(1f);
            D2dColorF green = new(0, 1, 0, 1);
            brush.SetColor(&green);
            D2dColorF readColor;
            brush.GetColor(&readColor);
            Assert.Equal((0f, 1f, 0f, 1f), (readColor.R, readColor.G, readColor.B, readColor.A));
            brush.SetColor(&black);

            D2dRectF outline = new(0.5f, 0.5f, 7.5f, 7.5f);
            D2dRectF clip = new(0, 0, 2, 2);
            D2dRectF all = new(0, 0, 8, 8);
            using (new DrawScope(context, target))
            {
                context.Clear(&white);
                context.DrawRectangle(&outline, brush, 1f, 0);
                context.DrawLine(new D2dPointF(0, 4.5f), new D2dPointF(8, 4.5f), brush, 1f, 0);
                ComInterop.Check(context.Flush(null, null));
            }

            byte[] pixels = ReadPixels(context, target, 8, 8);
            AssertPixel(pixels, 8, 0, 0, 0, 0, 0, 255);       // góc khung
            AssertPixel(pixels, 8, 3, 3, 255, 255, 255, 255); // trong khung, trống
            AssertPixel(pixels, 8, 3, 4, 0, 0, 0, 255);       // đường ngang y=4.5

            using (new DrawScope(context, target))
            {
                context.Clear(&white);
                context.PushAxisAlignedClip(&clip, D2dAntialiasMode.Aliased);
                context.FillRectangle(&all, brush);
                context.PopAxisAlignedClip();
            }

            pixels = ReadPixels(context, target, 8, 8);
            AssertPixel(pixels, 8, 1, 1, 0, 0, 0, 255);
            AssertPixel(pixels, 8, 2, 2, 255, 255, 255, 255);

            // CreateBitmap của ID2D1RenderTarget (slot 4) + CopyFromMemory (slot 10).
            D2dBitmapProperties legacy = new()
            {
                PixelFormat = new D2dPixelFormat { Format = DxgiFormat.B8G8R8A8Unorm, AlphaMode = D2dAlphaMode.Premultiplied },
                DpiX = 96,
                DpiY = 96,
            };
            ComInterop.Check(context.CreateBitmap(new D2dSizeU(2, 2), null, 0, &legacy, out nint legacyRaw));
            ID2D1Bitmap legacyBitmap = ComInterop.Wrap<ID2D1Bitmap>(legacyRaw);
            try
            {
                byte* data = stackalloc byte[16] { 1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255 };
                ComInterop.Check(legacyBitmap.CopyFromMemory(null, data, 8));
                using (new DrawScope(context, target))
                {
                    D2dRectF dest = new(0, 0, 2, 2);
                    context.Clear(&white);
                    context.DrawBitmapEx(legacyBitmap, &dest, 1f, D2dInterpolationMode.NearestNeighbor, null, 0);
                }

                pixels = ReadPixels(context, target, 8, 8);
                AssertPixel(pixels, 8, 0, 0, 1, 2, 3, 255);
                AssertPixel(pixels, 8, 1, 0, 4, 5, 6, 255);
                AssertPixel(pixels, 8, 0, 1, 7, 8, 9, 255);
                AssertPixel(pixels, 8, 1, 1, 10, 11, 12, 255);
            }
            finally
            {
                ComInterop.Release(legacyBitmap);
            }
        }
        finally
        {
            ComInterop.Release(target);
            ComInterop.Release(brush);
        }
    }

    [Fact]
    public void DirectWrite_TextFormat_WrappingTrimmingAndParagraphAlignment_RoundTrip()
    {
        IDWriteFactory factory = DWrite.CreateFactory();
        try
        {
            ComInterop.Check(factory.CreateTextFormat("Segoe UI", 0, DWriteFontWeight.Regular, DWriteFontStyle.Italic,
                DWriteFontStretch.Normal, 12f, "en-us", out nint formatRaw));
            IDWriteTextFormat format = ComInterop.Wrap<IDWriteTextFormat>(formatRaw);
            try
            {
                ComInterop.Check(format.SetWordWrapping(DWriteWordWrapping.NoWrap));
                Assert.Equal(DWriteWordWrapping.NoWrap, format.GetWordWrapping());
                ComInterop.Check(format.SetParagraphAlignment(DWriteParagraphAlignment.Center));
                Assert.Equal(DWriteParagraphAlignment.Center, format.GetParagraphAlignment());
                Assert.Equal(DWriteFontStyle.Italic, format.GetFontStyle());
                DWriteTrimming trimming = new() { Granularity = DWriteTrimmingGranularity.Character };
                ComInterop.Check(format.SetTrimming(&trimming, 0));
            }
            finally
            {
                ComInterop.Release(format);
            }
        }
        finally
        {
            ComInterop.Release(factory);
        }
    }

    [Fact]
    public void ComInterop_Release_ReturnsEveryWrapperReferenceImmediately()
    {
        ComInterop.Check(Dxgi.CreateDxgiFactory2(0, GraphicsGuids.IidDxgiFactory2, out nint raw));
        Marshal.AddRef(raw);            // ref "quan sát" của test
        int baseline = Marshal.AddRef(raw) - 1;
        Marshal.Release(raw);
        IDxgiFactory2 wrapper = ComInterop.Wrap<IDxgiFactory2>(raw); // trả ref của raw
        Assert.True(wrapper.IsCurrent() >= 0);

        ComInterop.Release(wrapper);
        ComInterop.Release(wrapper);    // gọi lại là no-op

        int after = Marshal.AddRef(raw) - 1;
        Marshal.Release(raw);
        Marshal.Release(raw);           // ref quan sát cuối
        // baseline gồm ref gốc + ref quan sát; sau Wrap+Release chỉ còn ref quan sát (= baseline - 1).
        Assert.Equal(baseline - 1, after);
    }

    [Fact]
    public void StructLayouts_MatchWin32Sdk()
    {
        Assert.Equal(48, Marshal.SizeOf<DxgiSwapChainDesc1>());
        Assert.Equal(44, Marshal.SizeOf<D3D11Texture2DDesc>());
        Assert.Equal(24, Marshal.SizeOf<D2dMatrix3x2F>());
        Assert.Equal(32, Marshal.SizeOf<D2dBitmapProperties1>());
        Assert.Equal(16, Marshal.SizeOf<D2dColorF>());
        Assert.Equal(36, Marshal.SizeOf<DWriteTextMetrics>());
        Assert.Equal(24, Marshal.SizeOf<DWriteLineMetrics>());
        Assert.Equal(36, Marshal.SizeOf<DWriteHitTestMetrics>());
    }
}
