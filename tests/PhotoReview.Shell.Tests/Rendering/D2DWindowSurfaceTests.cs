using PhotoReview.App.Input;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Interop.Graphics;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Tests.Rendering;

/// <summary>
/// WP-15: bề mặt theo cửa sổ (swap chain <c>CreateSwapChainForHwnd</c> - slot [15] của IDXGIFactory2 chưa có test chạy thật
/// ở WP-13b) trên một HWND "STATIC" ẩn (không ShowWindow, không SetForegroundWindow/SendInput), WARP. Cùng file: cache GPU
/// trên D2D thật (prefetch theo dải, rò rỉ bitmap sau 1000 lần upload/evict).
/// </summary>
[Trait("Category", "Native")]
public sealed class D2DWindowSurfaceTests
{
    private sealed class HiddenWindow : IDisposable
    {
        public HiddenWindow(int width, int height)
        {
            Handle = User32.CreateWindowEx(0, "STATIC", string.Empty, WindowMessages.WsPopup, 0, 0, width, height, 0, 0, 0, 0);
            Assert.NotEqual(0, Handle);
            Assert.False(User32.IsWindowVisible(Handle));
        }

        public nint Handle { get; }

        public void Dispose() => User32.DestroyWindow(Handle);
    }

    [Fact]
    public void WindowSurface_FlipModelWaitableSwapChain_LatencyOne_PresentsAndResizes()
    {
        using var window = new HiddenWindow(160, 90);
        using D2DRenderSurface surface = D2DRenderSurface.CreateWindowSurface(window.Handle, new RenderSurfaceOptions(PreferWarp: true));

        Assert.Equal(window.Handle, surface.Hwnd);
        Assert.True(surface.IsWarp);
        Assert.Equal((160, 90), (surface.PixelWidth, surface.PixelHeight));
        Assert.NotEqual(0, surface.FrameLatencyWaitHandle);
        Assert.Equal(1u, surface.Resources.QueryMaximumFrameLatency());
        Assert.True(surface.WaitForNextFrame(1000));

        for (int frame = 0; frame < 3; frame++)
        {
            IDrawContext dc = surface.BeginDraw();
            dc.Clear(RenderColors.DarkCanvas);
            dc.FillRectangle(new RectD(10, 10, 20, 20), new ColorF(1, 1, 1, 1));
            Assert.False(dc.TryReadPixels(out _));
            Assert.Contains(surface.EndDrawAndPresent(), new[] { PresentResult.Presented, PresentResult.Occluded });
        }

        surface.Resize(320, 200, 1);
        Assert.Equal((320, 200), (surface.PixelWidth, surface.PixelHeight));
        surface.BeginDraw().Clear(RenderColors.DarkCanvas);
        Assert.Contains(surface.EndDrawAndPresent(), new[] { PresentResult.Presented, PresentResult.Occluded });
    }

    [Fact]
    public void WindowSurface_MaxFrameLatencyOption_IsApplied()
    {
        using var window = new HiddenWindow(32, 32);
        using D2DRenderSurface surface = D2DRenderSurface.CreateWindowSurface(window.Handle,
            new RenderSurfaceOptions(PreferWarp: true, MaxFrameLatency: 2));

        Assert.Equal(2u, surface.Resources.QueryMaximumFrameLatency());
    }

    [Fact]
    public void WindowSurface_DeviceRemovedOnPresent_RecreatesSwapChainAndRaisesEvent()
    {
        using var window = new HiddenWindow(64, 64);
        using D2DRenderSurface surface = D2DRenderSurface.CreateWindowSurface(window.Handle, new RenderSurfaceOptions(PreferWarp: true));
        int recreated = 0;
        surface.DeviceRecreated += (_, _) => recreated++;
        bool injected = false;
        surface.Resources.PresentFault = hr =>
        {
            if (injected)
            {
                return hr;
            }

            injected = true;
            return DxgiConstants.ErrorDeviceRemoved;
        };

        surface.BeginDraw().Clear(RenderColors.DarkCanvas);
        Assert.Equal(PresentResult.DeviceRecreated, surface.EndDrawAndPresent());

        Assert.Equal(1, recreated);
        Assert.Equal(1, surface.DeviceGeneration);
        Assert.NotEqual(0, surface.FrameLatencyWaitHandle);
        Assert.Equal(1u, surface.Resources.QueryMaximumFrameLatency());
        surface.BeginDraw().Clear(RenderColors.DarkCanvas);
        Assert.Contains(surface.EndDrawAndPresent(), new[] { PresentResult.Presented, PresentResult.Occluded });
    }

    [Fact]
    public void WindowSurface_OccludedStatus_IsReported()
    {
        using var window = new HiddenWindow(16, 16);
        using D2DRenderSurface surface = D2DRenderSurface.CreateWindowSurface(window.Handle, new RenderSurfaceOptions(PreferWarp: true));
        surface.Resources.PresentFault = _ => DxgiConstants.StatusOccluded;

        surface.BeginDraw().Clear(RenderColors.DarkCanvas);

        Assert.Equal(PresentResult.Occluded, surface.EndDrawAndPresent());
    }

    [Fact]
    public void CreateForWindow_RejectsNullHandle()
    {
        Assert.Throws<ArgumentException>(() => D2DRenderSurface.CreateForWindow(0, new RenderSurfaceOptions()));
    }

    [Fact]
    public void Prefetch_StripedUpload_OnRealDevice_DrawsIdenticalPixels()
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(300, 200, 1);
        var dispatcher = new ManualDispatcher();
        using var cache = new GpuImageCache(new D2DGpuImageBackend(surface, maxTileSize: 128, gutter: 16), dispatcher,
            long.MaxValue, stripeBytes: 300 * 4 * 7);   // 7 dòng mỗi dải, cắt ngang ranh giới tile
        using PixelBuffer source = RenderTestImages.CreateNoise(300, 200, seed: 21);

        cache.Prefetch(source);
        int stripes = dispatcher.RunAll();

        Assert.Equal((200 + 6) / 7, stripes);
        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(new ColorF(0, 0, 0, 0));
            dc.DrawImage(cache.GetOrUpload(source), new RectD(0, 0, 300, 200), null, ImageInterpolation.HighQualityCubic);
        });
        RenderTestImages.AssertIdentical(source, pixels);
    }

    [Fact]
    public void Cache_ThousandUploadsAndEvictions_DoNotLeakBitmaps_AndStayInBudget()
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(64, 64, 1);
        var dispatcher = new ManualDispatcher();
        using var cache = new GpuImageCache(surface, dispatcher) { BudgetBytes = 5L * 64 * 64 * 4 };
        long baseline = surface.Resources.LiveBitmapCount;

        for (int i = 0; i < 1000; i++)
        {
            using PixelBuffer px = PixelBuffer.Allocate(64, 64, PixelLayout.Pbgra32);
            if (i % 2 == 0)
            {
                cache.Prefetch(px);
                dispatcher.RunAll();
            }

            cache.GetOrUpload(px);
            Assert.True(cache.TotalGpuBytes <= cache.BudgetBytes);
        }

        Assert.Equal(5, cache.Count);
        Assert.Equal(baseline + 5, surface.Resources.LiveBitmapCount);
        cache.Clear();
        Assert.Equal(baseline, surface.Resources.LiveBitmapCount);
        Assert.Equal(0, cache.TotalGpuBytes);
    }

    [Fact]
    public void Cache_DefaultBudget_IsSixScreensOfAtLeastFullHd()
    {
        using D2DRenderSurface small = D2DRenderSurface.CreateOffscreenSurface(64, 64, 1);
        using D2DRenderSurface large = D2DRenderSurface.CreateOffscreenSurface(2560, 1440, 1);

        using var smallCache = new GpuImageCache(small, new ManualDispatcher());
        using var largeCache = new GpuImageCache(large, new ManualDispatcher());

        Assert.Equal(6L * 1920 * 1080 * 4, smallCache.BudgetBytes);
        Assert.Equal(6L * 2560 * 1440 * 4, largeCache.BudgetBytes);
    }

    [Fact]
    public void Cache_Bgr32_IgnoresUnusedByte()
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(4, 4, 1);
        using var cache = new GpuImageCache(surface, new ManualDispatcher());
        using PixelBuffer source = PixelBuffer.Allocate(4, 4, PixelLayout.Bgr32);
        for (int y = 0; y < 4; y++)
        {
            Span<byte> row = source.GetRow(y);
            for (int i = 0; i < row.Length; i += 4)
            {
                (row[i], row[i + 1], row[i + 2], row[i + 3]) = ((byte)10, (byte)20, (byte)30, (byte)0);   // X = 0, không phải alpha
            }
        }

        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(new ColorF(0, 0, 0, 0));
            dc.DrawImage(cache.GetOrUpload(source), new RectD(0, 0, 4, 4), null, ImageInterpolation.Linear);
        });

        Assert.Equal(((byte)10, (byte)20, (byte)30, (byte)255), RenderTestImages.Pixel(pixels, 3, 3));
    }
}
