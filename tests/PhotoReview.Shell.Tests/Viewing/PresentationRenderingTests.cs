using PhotoReview.App.Input;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Overlay;
using PhotoReview.Shell.Tests.Rendering;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>
/// WP-21 trên renderer D2D THẬT (WARP offscreen, đọc lại pixel): ảnh 1:1 giống từng byte, crossfade pha trộn đúng opacity theo đồng hồ
/// giả, compare (viền chọn, ảnh Uniform trong ô, nền canvas), orientation, ảnh lớn chia tile không đường nối, mất thiết bị giữa lúc mờ.
/// </summary>
[Trait("Category", "Native")]
public sealed class PresentationRenderingTests : IDisposable
{
    private static readonly ColorF Canvas = CompareLayer.Canvas;

    private readonly FakeFrameClock _clock = new();
    private readonly ManualDispatcher _dispatcher = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var item in Enumerable.Reverse(_owned))
        {
            item.Dispose();
        }
    }

    private T Own<T>(T item)
        where T : IDisposable
    {
        _owned.Add(item);
        return item;
    }

    private (D2DRenderSurface Surface, GpuImageCache Cache, PresentationComposer Composer) Create(int width, int height)
    {
        var surface = Own(D2DRenderSurface.CreateOffscreenSurface(width, height, 1));
        var cache = Own(new GpuImageCache(surface, _dispatcher));
        var composer = Own(new PresentationComposer(cache, _dispatcher, _clock, () => { }, ticksPerSecond: 1000, timestamp: () => 0));
        return (surface, cache, composer);
    }

    private PixelBuffer Solid(int width, int height, byte b, byte g, byte r) => Own(ViewingImages.Solid(width, height, b, g, r));

    private static PresentationInputs Inputs(RectD box, SizeD client, bool compare = false, CompareFrame frame = default,
        ScalingQuality quality = ScalingQuality.Linear) =>
        new(client, box, quality, quality, 1.0, compare, frame);

    private static PixelBuffer Frame(D2DRenderSurface surface, PresentationComposer composer, in PresentationInputs inputs)
    {
        var captured = inputs;
        return RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(Canvas);
            composer.Draw(dc, captured);
        });
    }

    [Fact]
    public void MainImage_At1To1_IsByteIdenticalToTheSource()
    {
        var (surface, _, composer) = Create(64, 48);
        using var source = RenderTestImages.CreateSmooth(40, 30);
        composer.Set(source);

        using var pixels = Frame(surface, composer, Inputs(new RectD(12, 9, 40, 30), new SizeD(64, 48)));

        using var region = RenderTestImages.Crop(pixels, 12, 9, 40, 30);
        RenderTestImages.AssertIdentical(RenderTestImages.Crop(source, 0, 0, 40, 30), region);
        Assert.Equal(CanvasPixel(), RenderTestImages.Pixel(pixels, 2, 2));       // ngoài ảnh = canvas
    }

    [Fact]
    public void MainImage_IsClippedToTheViewport()
    {
        var (surface, _, composer) = Create(40, 30);
        var image = Solid(100, 100, 200, 100, 50);
        composer.Set(image);

        // Hộp đích tràn ra ngoài viewport 40x30 (ảnh đang cuộn): chỉ vùng trong viewport có pixel ảnh, khung không lỗi.
        using var pixels = Frame(surface, composer, Inputs(new RectD(-30, -30, 100, 100), new SizeD(40, 30)));

        Assert.Equal((50, 100, 200), Rgb(RenderTestImages.Pixel(pixels, 0, 0)));
        Assert.Equal((50, 100, 200), Rgb(RenderTestImages.Pixel(pixels, 39, 29)));
    }

    [Theory]
    [InlineData(0, 200, 0)]      // opacity 1: chỉ lớp ảnh cũ (đỏ 200)
    [InlineData(100, 50, 150)]   // p = 0,5: opacity (1 - p)^2 = 0,25
    [InlineData(150, 13, 188)]   // p = 0,75: opacity 0,0625
    public void Crossfade_BlendsTheOutgoingLayerByItsOpacityOverTheIncomingImage(long elapsedMs, int expectedRed, int expectedBlue)
    {
        var (surface, _, composer) = Create(32, 24);
        var outgoing = Solid(32, 24, 0, 0, 200);          // đỏ
        var incoming = Solid(32, 24, 200, 0, 0);          // xanh dương
        var client = new SizeD(32, 24);
        var box = new RectD(0, 0, 32, 24);
        composer.Set(outgoing);
        using (Frame(surface, composer, Inputs(box, client)))
        {
        }

        Assert.True(composer.BeginCrossfade(TimeSpan.FromMilliseconds(200)));
        composer.SetIncoming(incoming, isFileChange: true);
        _clock.Tick(50_000);                               // khung đầu = t0
        _clock.Tick(50_000 + elapsedMs);

        using var pixels = Frame(surface, composer, Inputs(box, client));

        var (r, _, b) = Rgb(RenderTestImages.Pixel(pixels, 16, 12));
        Assert.InRange(r, expectedRed - 3, expectedRed + 3);
        Assert.InRange(b, expectedBlue - 3, expectedBlue + 3);
    }

    [Fact]
    public void Crossfade_Finished_ShowsOnlyTheIncomingImage()
    {
        var (surface, _, composer) = Create(32, 24);
        var outgoing = Solid(32, 24, 0, 0, 200);
        var incoming = Solid(32, 24, 200, 0, 0);
        var box = new RectD(0, 0, 32, 24);
        var client = new SizeD(32, 24);
        composer.Set(outgoing);
        using (Frame(surface, composer, Inputs(box, client)))
        {
        }

        composer.BeginCrossfade(TimeSpan.FromMilliseconds(100));
        composer.SetIncoming(incoming, isFileChange: true);
        _clock.Tick(10_000);
        _clock.Tick(10_100);

        using var pixels = Frame(surface, composer, Inputs(box, client));

        Assert.Equal((0, 0, 200), Rgb(RenderTestImages.Pixel(pixels, 5, 5)));
        Assert.False(composer.Crossfade.IsActive);
    }

    [Fact]
    public void Compare_DrawsBordersCanvasAndUniformImagesInTheirCells()
    {
        var client = new SizeD(400, 200);
        var (surface, _, composer) = Create(400, 200);
        var left = Solid(100, 50, 0, 0, 255);              // đỏ, ngang
        var right = Solid(50, 100, 255, 0, 0);             // xanh dương, dọc
        var frame = new CompareFrame(left, right, LeftSelected: true, RightSelected: false);

        using var pixels = Frame(surface, composer, Inputs(new RectD(0, 0, 400, 200), client, compare: true, frame: frame));

        // Hộp: panel (8,8,384,184); cột 192; ô trái viền (12,12,184,176), nội dung (14,14,180,172).
        // Ảnh trái 100x50 -> x1.8 = 180x90, căn giữa dọc: y = 14 + (172 - 90) / 2 = 55.
        var leftImage = CompareLayout.Image(client, CompareSide.Left, 100, 50);
        Assert.Equal((14, 55, 180, 90), ((int)leftImage.X, (int)leftImage.Y, (int)leftImage.Width, (int)leftImage.Height));
        Assert.Equal((255, 0, 0), Rgb(RenderTestImages.Pixel(pixels, 100, 100)));         // giữa ảnh trái: đỏ
        Assert.Equal((0, 0, 255), Rgb(RenderTestImages.Pixel(pixels, 300, 100)));         // giữa ảnh phải: xanh dương
        Assert.Equal((50, 205, 50), Rgb(RenderTestImages.Pixel(pixels, 12, 100)));        // viền ô trái đã chọn = LimeGreen
        Assert.Equal((0x55, 0x55, 0x55), Rgb(RenderTestImages.Pixel(pixels, 204, 100)));  // viền ô phải chưa chọn = #555
        Assert.Equal(CanvasPixel(), RenderTestImages.Pixel(pixels, 3, 100));              // lề ngoài panel: canvas
        Assert.Equal(CanvasPixel(), RenderTestImages.Pixel(pixels, 100, 30));             // trong ô nhưng ngoài ảnh (letterbox): canvas
    }

    [Fact]
    public void Compare_HidesTheMainImageBeneathItsPanel()
    {
        var client = new SizeD(100, 80);
        var (surface, _, composer) = Create(100, 80);
        composer.Set(Solid(100, 80, 10, 200, 10));          // ảnh chính xanh lá phủ toàn client

        using var pixels = Frame(surface, composer,
            Inputs(new RectD(0, 0, 100, 80), client, compare: true, frame: new CompareFrame(null, null, false, true)));

        Assert.Equal(CanvasPixel(), RenderTestImages.Pixel(pixels, 50, 40));              // panel che ảnh chính
        Assert.Equal((10, 200, 10), Rgb(RenderTestImages.Pixel(pixels, 3, 3)));        // lề 8 DIP ngoài panel: ảnh chính còn thấy
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    public void Orientation_RawBufferDrawsLikeApplyOrientation(int orientation)
    {
        var (surface, cache, _) = Create(40, 40);
        using var raw = RenderTestImages.CreateSmooth(20, 12);
        using var reference = PixelOps.ApplyOrientation(RenderTestImages.Crop(raw, 0, 0, 20, 12), orientation);
        var destination = new RectD(0, 0, reference.Width, reference.Height);

        using var pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(Canvas);
            Assert.True(ImageLayer.TryDraw(cache, dc, raw, destination, ImageInterpolation.NearestNeighbor, 1f, orientation));
        });

        RenderTestImages.AssertIdentical(reference, RenderTestImages.Crop(pixels, 0, 0, reference.Width, reference.Height));
    }

    [Fact]
    public void LargeImage_SplitIntoTiles_HasNoSeamAtFit()
    {
        // 17000 > 16384: hai tile. Ảnh một màu thu về 1000 px: mọi pixel đích phải đúng màu (đường nối = pixel lệch/khe).
        var (surface, _, composer) = Create(1000, 80);
        var image = Solid(17000, 1200, 90, 160, 220);
        composer.Set(image);

        using var pixels = Frame(surface, composer, Inputs(new RectD(0, 0, 1000, 70.588), new SizeD(1000, 80)));

        for (var x = 0; x < 1000; x++)
        {
            Assert.Equal((220, 160, 90), Rgb(RenderTestImages.Pixel(pixels, x, 20)));
        }
    }

    [Fact]
    public void ImageTooLargeToUploadAndDisposedBuffers_AreSkippedWithoutThrowing()
    {
        var (surface, _, composer) = Create(20, 20);
        var image = Solid(10, 10, 1, 2, 3);
        composer.Set(image);
        image.Dispose();

        using var pixels = Frame(surface, composer, Inputs(new RectD(0, 0, 10, 10), new SizeD(20, 20)));

        Assert.Equal(CanvasPixel(), RenderTestImages.Pixel(pixels, 5, 5));
    }

    [Fact]
    public void DeviceLostDuringACrossfade_ReuploadsBothImagesAndTheFadeContinues()
    {
        var (surface, cache, composer) = Create(32, 24);
        var outgoing = Solid(32, 24, 0, 0, 200);
        var incoming = Solid(32, 24, 200, 0, 0);
        var box = new RectD(0, 0, 32, 24);
        var client = new SizeD(32, 24);
        composer.Set(outgoing);
        using (Frame(surface, composer, Inputs(box, client)))
        {
        }

        composer.BeginCrossfade(TimeSpan.FromMilliseconds(200));
        composer.SetIncoming(incoming, isFileChange: true);
        using (Frame(surface, composer, Inputs(box, client)))
        {
        }

        Assert.Equal(2, cache.Count);
        var generation = surface.DeviceGeneration;
        surface.RecreateDevice();                              // như D2DERR_RECREATE_TARGET / DEVICE_REMOVED
        Assert.True(surface.DeviceGeneration > generation);
        Assert.Equal(0, cache.Count);                          // cache GPU đã xoá hết

        _clock.Tick(20_000);
        _clock.Tick(20_100);                                   // nửa thời lượng: opacity 0,25
        using var pixels = Frame(surface, composer, Inputs(box, client));

        Assert.Equal(2, cache.Count);                          // cả hai ảnh được upload lại từ PixelBuffer
        var (r, _, b) = Rgb(RenderTestImages.Pixel(pixels, 16, 12));
        Assert.InRange(r, 50 - 3, 50 + 3);
        Assert.InRange(b, 150 - 3, 150 + 3);
    }

    /// <summary>(B, G, R, A) của <c>RenderTestImages.Pixel</c> -&gt; (R, G, B) để so với màu khai báo theo RGB.</summary>
    private static (int R, int G, int B) Rgb((byte B, byte G, byte R, byte A) pixel) => (pixel.R, pixel.G, pixel.B);

    private static (byte B, byte G, byte R, byte A) CanvasPixel() => (0x10, 0x10, 0x10, 0xFF);
}
