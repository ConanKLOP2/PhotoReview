using PhotoReview.App.Input;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Overlay;
using PhotoReview.Shell.Tests.Rendering;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>
/// WP-21 (Q-R37): lớp ảnh cũ của crossfade - đóng băng khung cũ, opacity 1 -&gt; 0 theo QuadraticOut trong thời lượng, huỷ khi điều
/// hướng mới, không đăng ký đồng hồ khi không có hiệu ứng. Đồng hồ giả: 1 tick = 1 ms.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class CrossfadeLayerTests : IDisposable
{
    private const long Ms = 1000;
    private static readonly RectD Viewport = new(0, 0, 800, 600);
    private static readonly RectD Frame = new(100, 50, 600, 400);

    private readonly FakeFrameClock _clock = new();
    private readonly FakeGpuBackend _backend = new();
    private readonly ManualDispatcher _dispatcher = new();
    private readonly List<PixelBuffer> _buffers = [];
    private readonly GpuImageCache _cache;
    private readonly CrossfadeLayer _layer;
    private int _invalidations;

    public CrossfadeLayerTests()
    {
        _cache = new GpuImageCache(_backend, _dispatcher, 1_000_000_000, 1 << 20);
        _layer = new CrossfadeLayer(_clock, () => _invalidations++, Ms);
    }

    public void Dispose()
    {
        _layer.Dispose();
        _cache.Dispose();
        foreach (var buffer in _buffers)
        {
            buffer.Dispose();
        }
    }

    private PixelBuffer Image(byte shade = 10)
    {
        var buffer = ViewingImages.Solid(8, 8, shade, shade, shade);
        _buffers.Add(buffer);
        return buffer;
    }

    private float DrawOpacity(out RecordingImageContext context)
    {
        context = new RecordingImageContext();
        Assert.True(_layer.Draw(_cache, context, Viewport));
        return Assert.Single(context.Images).Opacity;
    }

    [Fact]
    public void Start_FreezesTheOutgoingFrame_AtOpacityOne_UnderAViewportClip()
    {
        var outgoing = Image();

        Assert.True(_layer.Start(outgoing, Frame, ImageInterpolation.Linear, TimeSpan.FromMilliseconds(200)));
        Assert.True(_layer.IsActive);
        var opacity = DrawOpacity(out var context);

        Assert.Equal(1f, opacity);
        var drawn = Assert.Single(context.Images);
        Assert.Equal((outgoing.Id, Frame, ImageInterpolation.Linear), (drawn.BufferId, drawn.Destination, drawn.Interpolation));
        Assert.Equal(["PushClip", "DrawImage", "PopClip"], context.Calls);
        Assert.Equal(Viewport, Assert.Single(context.Clips));
        Assert.Equal(0, context.ClipDepth);
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(50, 0.5625)]    // p = 0,25: eased = 1 - 0,75^2 = 0,4375 -> opacity 0,5625
    [InlineData(100, 0.25)]     // p = 0,5 : eased 0,75
    [InlineData(150, 0.0625)]   // p = 0,75: eased 0,9375
    public void Opacity_FollowsQuadraticOutOverTheDuration(long elapsedMs, double expected)
    {
        _layer.Start(Image(), Frame, ImageInterpolation.Linear, TimeSpan.FromMilliseconds(200));

        _clock.Tick(10_000);                  // khung đầu = t0
        _clock.Tick(10_000 + elapsedMs);

        Assert.Equal(expected, DrawOpacity(out _), 4);
    }

    [Fact]
    public void FadeEnd_ReleasesTheOutgoingImage_AndStopsListeningToTheClock()
    {
        var outgoing = Image();
        _layer.Start(outgoing, Frame, ImageInterpolation.Linear, TimeSpan.FromMilliseconds(200));
        Assert.Equal(1, _clock.SubscriberCount);

        _clock.Tick(10_000);
        _clock.Tick(10_200);

        Assert.False(_layer.IsActive);
        Assert.Null(_layer.Outgoing);
        Assert.Null(_layer.OutgoingBufferId);
        Assert.Equal(0, _clock.SubscriberCount);
        var context = new RecordingImageContext();
        Assert.False(_layer.Draw(_cache, context, Viewport));
        Assert.Empty(context.Calls);
    }

    [Fact]
    public void EveryChangedFrame_AsksTheHostToRedraw()
    {
        _layer.Start(Image(), Frame, ImageInterpolation.Linear, TimeSpan.FromMilliseconds(200));

        _clock.Tick(10_000);
        var afterFirst = _invalidations;
        _clock.Tick(10_050);
        _clock.Tick(10_100);

        Assert.Equal(afterFirst + 2, _invalidations);
    }

    [Fact]
    public void NewNavigationMidFade_RestartsFromOpacityOneWithTheNewOutgoing_NotQueued()
    {
        var first = Image(10);
        var second = Image(20);
        var secondFrame = new RectD(0, 0, 800, 600);
        _layer.Start(first, Frame, ImageInterpolation.Linear, TimeSpan.FromMilliseconds(200));
        _clock.Tick(10_000);
        _clock.Tick(10_100);
        Assert.Equal(0.25, DrawOpacity(out _), 4);

        Assert.True(_layer.Start(second, secondFrame, ImageInterpolation.HighQualityCubic, TimeSpan.FromMilliseconds(200)));
        _clock.Tick(10_150);                  // khung đầu của tween mới = t0 mới
        var restarted = DrawOpacity(out var context);
        var drawn = Assert.Single(context.Images);
        _clock.Tick(10_250);                  // 100 ms sau t0 mới

        Assert.Equal(1f, restarted);
        Assert.Equal((second.Id, secondFrame, ImageInterpolation.HighQualityCubic), (drawn.BufferId, drawn.Destination, drawn.Interpolation));
        Assert.Equal(0.25, DrawOpacity(out _), 4);   // tính từ t0 mới, không từ tween cũ
        Assert.Equal(2, _layer.StartCount);
        Assert.Equal(1, _clock.SubscriberCount);     // một tween duy nhất
    }

    [Fact]
    public void Start_WithNothingToFade_ReturnsFalseAndSubscribesNothing()
    {
        var disposed = Image();
        disposed.Dispose();

        Assert.False(_layer.Start(disposed, Frame, ImageInterpolation.Linear, TimeSpan.FromMilliseconds(100)));
        Assert.False(_layer.Start(Image(), Frame, ImageInterpolation.Linear, TimeSpan.Zero));
        Assert.False(_layer.Start(Image(), new RectD(0, 0, 0, 10), ImageInterpolation.Linear, TimeSpan.FromMilliseconds(100)));
        Assert.False(_layer.Start(Image(), new RectD(double.NaN, 0, 10, 10), ImageInterpolation.Linear, TimeSpan.FromMilliseconds(100)));

        Assert.False(_layer.IsActive);
        Assert.Equal(0, _clock.SubscriberCount);
        Assert.Equal(0, _layer.StartCount);
    }

    [Fact]
    public void OutgoingBufferDisposedMidFade_StopsWithoutThrowing()
    {
        var outgoing = Image();
        _layer.Start(outgoing, Frame, ImageInterpolation.Linear, TimeSpan.FromMilliseconds(200));
        _clock.Tick(10_000);
        _clock.Tick(10_050);

        outgoing.Dispose();
        var context = new RecordingImageContext();

        Assert.False(_layer.Draw(_cache, context, Viewport));
        Assert.False(_layer.IsActive);
        Assert.Empty(context.Images);
    }

    [Fact]
    public void OutgoingBufferDisposedMidFade_IsReleasedBeforeTheContextIsTouched()
    {
        var outgoing = Image();
        _layer.Start(outgoing, Frame, ImageInterpolation.Linear, TimeSpan.FromMilliseconds(200));
        _clock.Tick(10_000);
        _clock.Tick(10_050);

        outgoing.Dispose();
        var context = new RecordingImageContext();

        Assert.False(_layer.Draw(_cache, context, Viewport));

        // Nhánh "buffer đã giải phóng" nhả ảnh ngay: không có PushClip/PopClip hay DrawImage nào được phát cho ảnh chết.
        Assert.Empty(context.Calls);
        Assert.Null(_layer.OutgoingBufferId);
        Assert.False(_layer.IsActive);
    }
    [Fact]
    public void Cancel_ReleasesTheOutgoingImageAndTheClock()
    {
        _layer.Start(Image(), Frame, ImageInterpolation.Linear, TimeSpan.FromMilliseconds(200));

        _layer.Cancel();

        Assert.False(_layer.IsActive);
        Assert.Equal(0, _clock.SubscriberCount);
        Assert.Equal(0f, _layer.Opacity);
    }

    [Fact]
    public void Dispose_ReleasesEverythingAndRejectsNewFades()
    {
        _layer.Start(Image(), Frame, ImageInterpolation.Linear, TimeSpan.FromMilliseconds(200));

        _layer.Dispose();

        Assert.False(_layer.IsActive);
        Assert.Equal(0, _clock.SubscriberCount);
        Assert.Throws<ObjectDisposedException>(() =>
            _layer.Start(Image(), Frame, ImageInterpolation.Linear, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void OutgoingBufferId_IsReportedWhileFading_SoTheCacheKeepsItsBitmap()
    {
        var outgoing = Image();

        _layer.Start(outgoing, Frame, ImageInterpolation.Linear, TimeSpan.FromMilliseconds(200));

        Assert.Equal(outgoing.Id, _layer.OutgoingBufferId);
    }
}
