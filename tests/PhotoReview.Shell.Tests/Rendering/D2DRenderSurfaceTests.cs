using System.Globalization;
using System.Numerics;
using PhotoReview.App.Input;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Interop.Graphics;
using PhotoReview.Shell.Rendering;
using Xunit.Abstractions;

namespace PhotoReview.Shell.Tests.Rendering;

/// <summary>
/// WP-15 (C-09/C-10): renderer D2D thật trên WARP offscreen - vẽ rồi đọc lại pixel bằng bitmap CPU_READ (texture staging)
/// và so với tham chiếu CPU: 1:1 giống từng byte (mọi interpolation, DPI 1,5, 8 orientation so với PixelOps), thu nhỏ/phóng
/// to theo PSNR (NE-9 a), tile (đường nối), primitive, mất thiết bị.
/// </summary>
[Trait("Category", "Native")]
public sealed class D2DRenderSurfaceTests(ITestOutputHelper output)
{
    private static readonly ColorF Transparent = new(0, 0, 0, 0);

    private static GpuImageCache CacheFor(D2DRenderSurface surface, int? maxTile = null, int gutter = TileLayout.DefaultGutter) =>
        new(new D2DGpuImageBackend(surface, maxTile, gutter), new ManualDispatcher(), long.MaxValue, GpuImageCache.DefaultStripeBytes);

    private void Report(string name, double psnr) =>
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"PSNR {name}: {psnr:F2} dB"));

    [Fact]
    public void Offscreen_IsWarpWithoutWindowOrWaitHandle()
    {
        using IRenderSurface surface = D2DRenderSurface.CreateOffscreen(64, 32, 1.25);

        Assert.True(surface.IsWarp);
        Assert.Equal((64, 32, 1.25), (surface.PixelWidth, surface.PixelHeight, surface.DpiScale));
        Assert.Equal(0, surface.Hwnd);
        Assert.Equal(0, surface.FrameLatencyWaitHandle);
        Assert.True(((D2DRenderSurface)surface).MaxBitmapSize >= 4096);
    }

    [Fact]
    public void Clear_DarkCanvas_ReadsBackThemeColor()
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(8, 4, 1);

        using PixelBuffer pixels = RenderTestImages.Render(surface, dc => dc.Clear(RenderColors.DarkCanvas));

        Assert.Equal((8, 4, PixelLayout.Pbgra32), (pixels.Width, pixels.Height, pixels.Layout));
        Assert.Equal(((byte)0x10, (byte)0x10, (byte)0x10, (byte)255), RenderTestImages.Pixel(pixels, 7, 3));
    }

    [Theory]
    [InlineData(ImageInterpolation.NearestNeighbor, PixelLayout.Pbgra32)]
    [InlineData(ImageInterpolation.Linear, PixelLayout.Pbgra32)]
    [InlineData(ImageInterpolation.HighQualityCubic, PixelLayout.Pbgra32)]
    [InlineData(ImageInterpolation.HighQualityCubic, PixelLayout.Bgr32)]
    public void DrawImage_OneToOne_IsByteIdentical_EvenAtFractionalOffset(ImageInterpolation interpolation, PixelLayout layout)
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(80, 60, 1);
        using GpuImageCache cache = CacheFor(surface);
        using PixelBuffer source = layout == PixelLayout.Pbgra32
            ? RenderTestImages.CreateNoise(80, 60, seed: 7)
            : RenderTestImages.CreateSmooth(80, 60, PixelLayout.Bgr32);
        IGpuImage image = cache.GetOrUpload(source);

        // Lệch 0,4 px: renderer phải dời về lưới pixel (không nội suy nửa pixel ở 100 %).
        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(Transparent);
            dc.DrawImage(image, new RectD(0.4, -0.4, 80, 60), null, interpolation);
        });

        using PixelBuffer expected = RenderTestImages.Crop(source, 0, 0, 80, 60);
        RenderTestImages.AssertIdentical(expected, pixels);
    }

    [Fact]
    public void DrawImage_OneToOne_AtDpi150_IsByteIdentical()
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(90, 60, 1.5);
        using GpuImageCache cache = CacheFor(surface);
        using PixelBuffer source = RenderTestImages.CreateNoise(90, 60, seed: 3);
        IGpuImage image = cache.GetOrUpload(source);

        // ADR 0008: 100 % = 1 pixel nguồn / pixel thiết bị = OriginalWidth / DpiScale DIP.
        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(Transparent);
            dc.DrawImage(image, new RectD(0, 0, 90 / 1.5, 60 / 1.5), null, ImageInterpolation.HighQualityCubic);
        });

        using PixelBuffer expected = RenderTestImages.Crop(source, 0, 0, 90, 60);
        RenderTestImages.AssertIdentical(expected, pixels);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void DrawImageOriented_MatchesPixelOpsApplyOrientation(int orientation)
    {
        const int W = 40;
        const int H = 24;
        using PixelBuffer raw = RenderTestImages.CreateNoise(W, H, seed: orientation);
        using PixelBuffer copy = RenderTestImages.Crop(raw, 0, 0, W, H);
        using PixelBuffer reference = PixelOps.ApplyOrientation(copy, orientation);
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(reference.Width + 6, reference.Height + 4, 1);
        using GpuImageCache cache = CacheFor(surface);
        IGpuImage image = cache.GetOrUpload(raw);

        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(Transparent);
            dc.DrawImageOriented(image, new RectD(3, 2, reference.Width, reference.Height), orientation, null,
                ImageInterpolation.HighQualityCubic);
        });

        using PixelBuffer drawn = RenderTestImages.Crop(pixels, 3, 2, reference.Width, reference.Height);
        RenderTestImages.AssertIdentical(reference, drawn);
    }

    [Theory]
    [InlineData(ImageInterpolation.HighQualityCubic, 300, 200, 40)]
    [InlineData(ImageInterpolation.HighQualityCubic, 449, 299, 40)]
    [InlineData(ImageInterpolation.Linear, 600, 400, 32)]
    public void DrawImage_FitDownscale_MatchesAreaReferenceByPsnr(ImageInterpolation interpolation, int width, int height, double minPsnr)
    {
        using PixelBuffer source = RenderTestImages.CreateSmooth(1200, 800);
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(width, height, 1);
        using GpuImageCache cache = CacheFor(surface);
        IGpuImage image = cache.GetOrUpload(source);

        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
            dc.DrawImage(image, new RectD(0, 0, width, height), null, interpolation));

        using PixelBuffer reference = RenderTestImages.ResizeArea(source, width, height);
        double psnr = RenderTestImages.Psnr(reference, pixels);
        Report($"downscale {interpolation} 1200x800->{width}x{height}", psnr);
        Assert.True(psnr >= minPsnr, $"PSNR {psnr:F2} dB < {minPsnr}");
    }

    [Theory]
    [InlineData(ImageInterpolation.Linear, 45)]
    [InlineData(ImageInterpolation.HighQualityCubic, 28)]
    public void DrawImage_Upscale_MatchesBilinearReferenceByPsnr(ImageInterpolation interpolation, double minPsnr)
    {
        using PixelBuffer source = RenderTestImages.CreateSmooth(120, 80);
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(300, 200, 1);
        using GpuImageCache cache = CacheFor(surface);
        IGpuImage image = cache.GetOrUpload(source);

        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
            dc.DrawImage(image, new RectD(0, 0, 300, 200), null, interpolation));

        using PixelBuffer reference = RenderTestImages.ResizeBilinear(source, 300, 200);
        double psnr = RenderTestImages.Psnr(reference, pixels);
        Report($"upscale {interpolation} 2.5x", psnr);
        Assert.True(psnr >= minPsnr, $"PSNR {psnr:F2} dB < {minPsnr}");
    }

    [Fact]
    public void DrawImage_NearestNeighborUpscale_ReplicatesSourcePixels()
    {
        using PixelBuffer source = RenderTestImages.CreateNoise(10, 6, seed: 11, translucent: false);
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(40, 24, 1);
        using GpuImageCache cache = CacheFor(surface);
        IGpuImage image = cache.GetOrUpload(source);

        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
            dc.DrawImage(image, new RectD(0, 0, 40, 24), null, ImageInterpolation.NearestNeighbor));

        for (int y = 0; y < 24; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                Assert.Equal(RenderTestImages.Pixel(source, x / 4, y / 4), RenderTestImages.Pixel(pixels, x, y));
            }
        }
    }

    [Fact]
    public void DrawImage_SourceRectangle_PansAtOneToOne()
    {
        using PixelBuffer source = RenderTestImages.CreateNoise(200, 120, seed: 5);
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(64, 32, 1);
        using GpuImageCache cache = CacheFor(surface);
        IGpuImage image = cache.GetOrUpload(source);

        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(Transparent);
            dc.DrawImage(image, new RectD(0, 0, 64, 32), new RectD(101, 47, 64, 32), ImageInterpolation.Linear);
        });

        using PixelBuffer expected = RenderTestImages.Crop(source, 101, 47, 64, 32);
        RenderTestImages.AssertIdentical(expected, pixels);
    }

    [Fact]
    public void DrawImage_SourceBeyondImage_DrawsOnlyIntersectionInPlace()
    {
        using PixelBuffer source = RenderTestImages.CreateSolid(10, 10, 0, 0, 255);
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(20, 20, 1);
        using GpuImageCache cache = CacheFor(surface);
        IGpuImage image = cache.GetOrUpload(source);

        // Nguồn (-10,-10,20,20) -> đích 20x20: ảnh chỉ phủ nửa dưới-phải.
        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(Transparent);
            dc.DrawImage(image, new RectD(0, 0, 20, 20), new RectD(-10, -10, 20, 20), ImageInterpolation.NearestNeighbor);
        });

        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)0), RenderTestImages.Pixel(pixels, 9, 9));
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), RenderTestImages.Pixel(pixels, 10, 10));
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), RenderTestImages.Pixel(pixels, 19, 19));
    }

    [Theory]
    [InlineData(ImageInterpolation.NearestNeighbor, 1.0)]
    [InlineData(ImageInterpolation.Linear, 1.0)]
    [InlineData(ImageInterpolation.Linear, 0.5)]
    [InlineData(ImageInterpolation.Linear, 1.7)]
    [InlineData(ImageInterpolation.HighQualityCubic, 0.37)]
    [InlineData(ImageInterpolation.HighQualityCubic, 0.5)]
    [InlineData(ImageInterpolation.HighQualityCubic, 1.7)]
    public void TiledImage_ForcedSmallTiles_MatchesSingleTexture(ImageInterpolation interpolation, double scale)
    {
        using PixelBuffer source = RenderTestImages.CreateSmooth(500, 300);
        int width = (int)Math.Round(500 * scale);
        int height = (int)Math.Round(300 * scale);
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(width, height, 1);
        using GpuImageCache whole = CacheFor(surface);
        using GpuImageCache tiled = CacheFor(surface, maxTile: 128, gutter: 16);
        IGpuImage single = whole.GetOrUpload(source);
        IGpuImage tiles = tiled.GetOrUpload(source);
        Assert.True(((TiledGpuImage)tiles).Tiles.Length > 9, "test must actually tile");

        using PixelBuffer expected = RenderTestImages.Render(surface, dc =>
            dc.DrawImage(single, new RectD(0, 0, width, height), null, interpolation));
        using PixelBuffer actual = RenderTestImages.Render(surface, dc =>
            dc.DrawImage(tiles, new RectD(0, 0, width, height), null, interpolation));

        double psnr = RenderTestImages.Psnr(expected, actual);
        Report($"tiled vs single {interpolation} x{scale}", psnr);
        if (scale == 1.0)
        {
            RenderTestImages.AssertIdentical(expected, actual);
        }
        else
        {
            Assert.True(psnr >= 45, $"PSNR {psnr:F2} dB: tile seams visible");
        }
    }

    [Fact]
    public void TiledImage_Panorama30000x2000_SplitsAtDeviceLimit_SeamExactAtOneToOne_FitMatchesReference()
    {
        using PixelBuffer source = RenderTestImages.CreateSmooth(30000, 2000);
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(1500, 100, 1);
        using GpuImageCache cache = CacheFor(surface);
        var image = (TiledGpuImage)cache.GetOrUpload(source);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"max bitmap size {surface.MaxBitmapSize}"));
        Assert.Equal(2, image.Tiles.Length);
        Assert.InRange(image.Tiles[0].Spec.Core.Right, 16000, 16384);
        int seam = image.Tiles[0].Spec.Core.Right;

        // 1:1 qua đường nối: giống từng byte.
        using PixelBuffer oneToOne = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(Transparent);
            dc.DrawImage(image, new RectD(0, 0, 256, 100), new RectD(seam - 128, 900, 256, 100), ImageInterpolation.HighQualityCubic);
        });
        using PixelBuffer drawnCrop = RenderTestImages.Crop(oneToOne, 0, 0, 256, 100);
        using PixelBuffer expectedCrop = RenderTestImages.Crop(source, seam - 128, 900, 256, 100);
        RenderTestImages.AssertIdentical(expectedCrop, drawnCrop);

        // Fit (x0,05) cả ảnh: so với tham chiếu diện tích.
        using PixelBuffer fit = RenderTestImages.Render(surface, dc =>
            dc.DrawImage(image, new RectD(0, 0, 1500, 100), null, ImageInterpolation.HighQualityCubic));
        using PixelBuffer reference = RenderTestImages.ResizeArea(source, 1500, 100);
        double psnr = RenderTestImages.Psnr(reference, fit);
        Report("panorama 30000x2000 fit HQC", psnr);
        Assert.True(psnr >= 40, $"PSNR {psnr:F2} dB");

        // Quanh đường nối (cột đích seam*0,05 +/- 4): không lệch hơn phần còn lại.
        int seamColumn = (int)Math.Round(seam * 0.05);
        using PixelBuffer seamDrawn = RenderTestImages.Crop(fit, seamColumn - 4, 0, 8, 100);
        using PixelBuffer seamReference = RenderTestImages.Crop(reference, seamColumn - 4, 0, 8, 100);
        double seamPsnr = RenderTestImages.Psnr(seamReference, seamDrawn);
        Report("panorama seam columns", seamPsnr);
        Assert.True(seamPsnr >= 40, $"seam PSNR {seamPsnr:F2} dB");
    }

    [Fact]
    public void Primitives_FillDrawRoundedClipAndOpacity()
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(40, 40, 1);
        ColorF white = new(1, 1, 1, 1);
        ColorF red = new(1, 0, 0, 1);

        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(new ColorF(0, 0, 0, 1));
            dc.FillRectangle(new RectD(0, 0, 10, 10), white);
            dc.DrawRectangle(new RectD(20.5, 0.5, 9, 9), red, 1);
            dc.FillRoundedRectangle(new RectD(0, 20, 20, 20), 8, white);
            dc.PushClip(new RectD(30, 30, 5, 5));
            dc.FillRectangle(new RectD(20, 20, 20, 20), red);
            dc.PopClip();
            dc.PushOpacity(0.5f);
            dc.FillRectangle(new RectD(10, 0, 5, 5), white);
            dc.PopOpacity();
        });

        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), RenderTestImages.Pixel(pixels, 5, 5));
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), RenderTestImages.Pixel(pixels, 20, 5));   // cạnh trái khung đỏ
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), RenderTestImages.Pixel(pixels, 25, 5));     // trong khung: trống
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), RenderTestImages.Pixel(pixels, 10, 30)); // giữa hình bo góc
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), RenderTestImages.Pixel(pixels, 0, 20));     // góc bo: không tô
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), RenderTestImages.Pixel(pixels, 32, 32));  // trong clip
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), RenderTestImages.Pixel(pixels, 25, 25));    // ngoài clip
        (byte b, _, _, _) = RenderTestImages.Pixel(pixels, 12, 2);
        Assert.InRange(b, 125, 131);                                                                      // opacity 0,5
    }

    [Fact]
    public void DrawImage_OpacityMultipliesWithPushedOpacity()
    {
        using PixelBuffer source = RenderTestImages.CreateSolid(4, 4, 255, 255, 255);
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(4, 4, 1);
        using GpuImageCache cache = CacheFor(surface);
        IGpuImage image = cache.GetOrUpload(source);

        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(new ColorF(0, 0, 0, 1));
            dc.PushOpacity(0.5f);
            dc.DrawImage(image, new RectD(0, 0, 4, 4), null, ImageInterpolation.Linear, 0.5f);
            dc.PopOpacity();
        });

        (byte b, _, _, byte a) = RenderTestImages.Pixel(pixels, 1, 1);
        Assert.InRange(b, 62, 66);
        Assert.Equal(255, a);
    }

    [Fact]
    public void PushTransform_TranslatesAndPopRestores()
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(20, 10, 1);
        ColorF white = new(1, 1, 1, 1);

        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(new ColorF(0, 0, 0, 1));
            dc.PushTransform(Matrix3x2.CreateTranslation(10, 0));
            dc.FillRectangle(new RectD(0, 0, 5, 5), white);
            dc.PopTransform();
            dc.FillRectangle(new RectD(0, 5, 5, 5), white);
            Assert.Equal(Matrix3x2.Identity, dc.Transform);
        });

        Assert.Equal((byte)255, RenderTestImages.Pixel(pixels, 12, 2).B);
        Assert.Equal((byte)0, RenderTestImages.Pixel(pixels, 2, 2).B);
        Assert.Equal((byte)255, RenderTestImages.Pixel(pixels, 2, 7).B);
    }

    [Fact]
    public void DrawText_NativeLayout_ProducesInk_OtherLayoutRejected()
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(120, 32, 1);
        using var layout = new DWriteTestLayout("Tiếng Việt", 18f);

        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(new ColorF(1, 1, 1, 1));
            dc.DrawText(layout, new PointD(2, 2), new ColorF(0, 0, 0, 1));
            Assert.Throws<ArgumentException>(() => dc.DrawText(new ForeignLayout(), new PointD(0, 0), new ColorF(0, 0, 0, 1)));
        });

        int dark = 0;
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 120; x++)
            {
                if (RenderTestImages.Pixel(pixels, x, y).R < 128)
                {
                    dark++;
                }
            }
        }

        Assert.True(dark > 30, $"only {dark} dark pixels");
    }

    [Fact]
    public void EndDraw_UnbalancedClip_ThrowsButSurfaceStaysUsable()
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(8, 8, 1);
        IDrawContext dc = surface.BeginDraw();
        dc.PushClip(new RectD(0, 0, 4, 4));

        Assert.Throws<InvalidOperationException>(() => surface.EndDrawAndPresent());

        using PixelBuffer pixels = RenderTestImages.Render(surface, d => d.Clear(new ColorF(0, 1, 0, 1)));
        Assert.Equal((byte)255, RenderTestImages.Pixel(pixels, 7, 7).G);
    }

    [Fact]
    public void Misuse_IsRejected()
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(8, 8, 1);
        Assert.Throws<InvalidOperationException>(() => surface.EndDrawAndPresent());

        IDrawContext dc = surface.BeginDraw();
        Assert.Throws<InvalidOperationException>(() => surface.BeginDraw());
        Assert.Throws<InvalidOperationException>(() => surface.Resize(4, 4, 1));
        Assert.Throws<InvalidOperationException>(() => dc.PopClip());
        Assert.Throws<InvalidOperationException>(() => dc.PopOpacity());
        Assert.Throws<InvalidOperationException>(() => ((D2DDrawContext)dc).PopTransform());
        Exception? crossThread = null;
        var thread = new Thread(() => crossThread = Record.Exception(() => surface.EndDrawAndPresent()));
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(crossThread);
        Assert.Equal(PresentResult.Presented, surface.EndDrawAndPresent());

        Assert.Throws<InvalidOperationException>(() => dc.Clear(Transparent));
        surface.Dispose();
        Assert.Throws<ObjectDisposedException>(() => surface.BeginDraw());
    }

    [Fact]
    public void Resize_Offscreen_ChangesTargetSizeAndDpi()
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(8, 8, 1);

        surface.Resize(30, 12, 2);

        Assert.Equal((30, 12, 2.0), (surface.PixelWidth, surface.PixelHeight, surface.DpiScale));
        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(new ColorF(0, 0, 0, 1));
            dc.FillRectangle(new RectD(0, 0, 5, 5), new ColorF(1, 1, 1, 1));   // 5 DIP = 10 px
        });
        Assert.Equal((30, 12), (pixels.Width, pixels.Height));
        Assert.Equal((byte)255, RenderTestImages.Pixel(pixels, 9, 9).B);
        Assert.Equal((byte)0, RenderTestImages.Pixel(pixels, 10, 10).B);
    }

    [Fact]
    public void DeviceLost_RecreateTarget_RecreatesDevice_EmptiesCache_NextFrameDraws()
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(16, 16, 1);
        using var cache = new GpuImageCache(surface, new ManualDispatcher());
        using PixelBuffer source = RenderTestImages.CreateNoise(16, 16, seed: 9);
        IGpuImage before = cache.GetOrUpload(source);
        int recreated = 0;
        surface.DeviceRecreated += (_, _) => recreated++;
        bool injected = false;
        surface.Resources.EndDrawFault = hr =>
        {
            if (injected)
            {
                return hr;
            }

            injected = true;
            return D2dConstants.ErrorRecreateTarget;
        };

        D2DDrawContext dc = surface.BeginDrawContext();
        dc.DrawImage(before, new RectD(0, 0, 16, 16), null, ImageInterpolation.Linear);
        Assert.Equal(PresentResult.DeviceRecreated, surface.EndDrawAndPresent());

        Assert.Equal(1, recreated);
        Assert.Equal(1, surface.DeviceGeneration);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, surface.Resources.LiveBitmapCount);

        // Ảnh cũ (thiết bị cũ) bị bỏ qua, không crash; upload lại thì vẽ đúng.
        using PixelBuffer pixels = RenderTestImages.Render(surface, d =>
        {
            d.Clear(Transparent);
            d.DrawImage(before, new RectD(0, 0, 16, 16), null, ImageInterpolation.Linear);
            Assert.Equal(1, ((D2DDrawContext)d).SkippedImageDraws);
            d.DrawImage(cache.GetOrUpload(source), new RectD(0, 0, 16, 16), null, ImageInterpolation.Linear);
        });
        RenderTestImages.AssertIdentical(source, pixels);
    }

    [Fact]
    public void EndDraw_OtherFailure_ReturnsFailedWithoutRecreating()
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(4, 4, 1);
        surface.Resources.EndDrawFault = _ => unchecked((int)0x80004005); // E_FAIL

        surface.BeginDraw().Clear(Transparent);

        Assert.Equal(PresentResult.Failed, surface.EndDrawAndPresent());
        Assert.Equal(0, surface.DeviceGeneration);
    }

    [Fact]
    public void EvictedImage_IsSkippedNotDrawn()
    {
        using D2DRenderSurface surface = D2DRenderSurface.CreateOffscreenSurface(4, 4, 1);
        using GpuImageCache cache = CacheFor(surface);
        using PixelBuffer source = RenderTestImages.CreateSolid(4, 4, 255, 0, 0);
        IGpuImage image = cache.GetOrUpload(source);
        cache.Clear();

        using PixelBuffer pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(Transparent);
            dc.DrawImage(image, new RectD(0, 0, 4, 4), null, ImageInterpolation.Linear);
            Assert.Equal(1, ((D2DDrawContext)dc).SkippedImageDraws);
        });

        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)0), RenderTestImages.Pixel(pixels, 2, 2));
    }

    /// <summary>Layout DWrite tối giản cho test (renderer chữ thật là WP-17).</summary>
    private sealed class DWriteTestLayout : ITextLayout, INativeTextLayout
    {
        private readonly IDWriteFactory _factory;
        private readonly IDWriteTextFormat _format;

        public DWriteTestLayout(string text, float size)
        {
            Text = text;
            _factory = DWrite.CreateFactory();
            ComInterop.Check(_factory.CreateTextFormat("Segoe UI", 0, DWriteFontWeight.Bold, DWriteFontStyle.Normal,
                DWriteFontStretch.Normal, size, "vi-VN", out nint formatRaw));
            _format = ComInterop.Wrap<IDWriteTextFormat>(formatRaw);
            ComInterop.Check(_factory.CreateTextLayout(text, (uint)text.Length, _format, 200f, 40f, out nint layoutRaw));
            NativeLayout = ComInterop.Wrap<IDWriteTextLayout>(layoutRaw);
        }

        public SizeD Size => new(200, 40);

        public string Text { get; }

        public IDWriteTextLayout NativeLayout { get; }

        public void Dispose()
        {
            ComInterop.Release(NativeLayout);
            ComInterop.Release(_format);
            ComInterop.Release(_factory);
        }
    }

    private sealed class ForeignLayout : ITextLayout
    {
        public SizeD Size => new(1, 1);

        public string Text => "x";

        public void Dispose()
        {
        }
    }
}
