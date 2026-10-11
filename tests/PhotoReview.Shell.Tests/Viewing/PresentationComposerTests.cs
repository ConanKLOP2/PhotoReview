using PhotoReview.App.Input;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Overlay;
using PhotoReview.Shell.Tests.Rendering;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>
/// WP-21: <see cref="PresentationComposer"/> trên backend GPU giả + dispatcher thủ công + đồng hồ khung giả (1 tick = 1 ms):
/// thứ tự lớp, không nhảy layout khi nâng cấp preview -&gt; full, INV-1 (ảnh cuối luôn đúng), bộ lọc theo chuyển động.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PresentationComposerTests : IDisposable
{
    private static readonly SizeD Client = new(1000, 700);
    private static readonly RectD Box = new(100, 50, 800, 600);

    private readonly FakeFrameClock _clock = new();
    private readonly FakeGpuBackend _backend = new();
    private readonly ManualDispatcher _dispatcher = new();
    private readonly List<PixelBuffer> _buffers = [];
    private readonly List<(TimeSpan Delay, Action Action)> _scheduled = [];
    private readonly GpuImageCache _cache;
    private readonly PresentationComposer _composer;
    private long _now = 1_000_000;
    private int _invalidations;

    public PresentationComposerTests()
    {
        // Dải 4000 byte = 10 dòng của ảnh 100 px: ảnh 100x100 cần 10 việc nền.
        _cache = new GpuImageCache(_backend, _dispatcher, 1_000_000_000, 4000);
        _composer = new PresentationComposer(_cache, _dispatcher, _clock, () => _invalidations++, () => _now, 1000, 120,
            (delay, action) => _scheduled.Add((delay, action)));
    }

    public void Dispose()
    {
        _composer.Dispose();
        _cache.Dispose();
        foreach (var buffer in _buffers)
        {
            buffer.Dispose();
        }
    }

    private PixelBuffer Image(int width = 100, int height = 100, byte shade = 10)
    {
        var buffer = ViewingImages.Solid(width, height, shade, shade, shade);
        _buffers.Add(buffer);
        return buffer;
    }

    private static PresentationInputs Inputs(RectD? box = null, bool compare = false, CompareFrame frame = default,
        ScalingQuality quality = ScalingQuality.HighQuality, double dpi = 1.0) =>
        new(Client, box ?? Box, quality, quality, dpi, compare, frame);

    private ComposedFrame Draw(out RecordingImageContext context, PresentationInputs? inputs = null)
    {
        context = new RecordingImageContext();
        return _composer.Draw(context, inputs ?? Inputs());
    }

    [Fact]
    public void Draw_MainImage_UsesTheLayoutBoxUnderAViewportClip()
    {
        var image = Image();
        _composer.Set(image);

        var frame = Draw(out var context);

        Assert.True(frame.MainDrawn);
        Assert.False(frame.CrossfadeDrawn);
        Assert.Equal(["PushClip", "DrawImage", "PopClip"], context.Calls);
        Assert.Equal(new RectD(0, 0, 1000, 700), Assert.Single(context.Clips));
        var drawn = Assert.Single(context.Images);
        Assert.Equal((image.Id, Box, 1f), (drawn.BufferId, drawn.Destination, drawn.Opacity));
        Assert.Equal(ImageInterpolation.HighQualityCubic, drawn.Interpolation);
    }

    [Fact]
    public void Draw_UsesTheEffectiveScalingQuality_ForTheMainImage()
    {
        _composer.Set(Image());

        Draw(out var context, Inputs(quality: ScalingQuality.Linear));

        Assert.Equal(ImageInterpolation.Linear, Assert.Single(context.Images).Interpolation);
    }

    [Fact]
    public void Draw_WithNothingToShow_DrawsNothing()
    {
        var frame = Draw(out var context);

        Assert.False(frame.MainDrawn);
        Assert.Empty(context.Calls);
    }

    [Fact]
    public void Draw_DisposedCurrent_DrawsNothingAndDoesNotThrow()
    {
        var image = Image();
        _composer.Set(image);
        image.Dispose();

        var frame = Draw(out var context);

        Assert.False(frame.MainDrawn);
        Assert.Empty(context.Images);
    }

    [Fact]
    public void LayersAreDrawnInXamlOrder_MainThenOutgoingThenComparePanel()
    {
        var outgoing = Image(shade: 1);
        var incoming = Image(shade: 2);
        var left = Image(shade: 3);
        var right = Image(shade: 4);
        _composer.Set(outgoing);
        Draw(out _);                                              // khung đã thấy
        Assert.True(_composer.BeginCrossfade(TimeSpan.FromMilliseconds(200)));
        _composer.SetIncoming(incoming, isFileChange: true);

        var frame = Draw(out var context, Inputs(compare: true, frame: new CompareFrame(left, right, LeftSelected: true, RightSelected: false)));

        Assert.True(frame.MainDrawn);
        Assert.True(frame.CrossfadeDrawn);
        Assert.Equal(2, frame.CompareImages);
        Assert.Equal([incoming.Id, outgoing.Id, left.Id, right.Id], context.Images.Select(i => i.BufferId));
        Assert.Equal(1f, context.Images[1].Opacity);              // lớp ảnh cũ bắt đầu ở 1
        var firstFill = context.Calls.IndexOf("FillRectangle");
        Assert.True(firstFill > context.Calls.LastIndexOf("DrawImage", firstFill), "panel compare phải vẽ SAU ảnh chính và lớp ảnh cũ");
    }

    [Fact]
    public void Compare_DrawsCanvasPanel_ThenSelectionBorders_ThenImagesInsideTheirCells()
    {
        var left = Image(200, 100, shade: 3);
        var right = Image(100, 200, shade: 4);
        var frame = new CompareFrame(left, right, LeftSelected: false, RightSelected: true);

        var composed = Draw(out var context, Inputs(compare: true, frame: frame, quality: ScalingQuality.Linear));

        Assert.Equal(2, composed.CompareImages);
        var panel = Assert.IsType<RectD>(context.Fills[0].Rect);
        Assert.Equal(CompareLayout.Panel(Client), panel);
        Assert.Equal(CompareLayer.Canvas, context.Fills[0].Color);
        // Bốn viền của ô trái (chưa chọn: #555), rồi bốn viền của ô phải (đã chọn: LimeGreen).
        Assert.All(context.Fills.Skip(1).Take(4), fill => Assert.Equal(CompareLayer.Unselected, fill.Color));
        Assert.All(context.Fills.Skip(5).Take(4), fill => Assert.Equal(CompareLayer.Selected, fill.Color));
        Assert.Equal(CompareLayout.Image(Client, CompareSide.Left, 200, 100), context.Images[0].Destination);
        Assert.Equal(CompareLayout.Image(Client, CompareSide.Right, 100, 200), context.Images[1].Destination);
        Assert.All(context.Images, image => Assert.Equal(ImageInterpolation.Linear, image.Interpolation));
    }

    [Fact]
    public void Compare_NotVisible_DrawsNoPanel()
    {
        var left = Image();
        _composer.Set(Image());

        var frame = Draw(out var context, Inputs(compare: false, frame: new CompareFrame(left, left, true, false)));

        Assert.Equal(0, frame.CompareImages);
        Assert.Empty(context.Fills);
    }

    [Fact]
    public void Compare_MissingOrDisposedImage_StillDrawsPanelAndBorders()
    {
        var disposed = Image();
        disposed.Dispose();

        var frame = Draw(out var context, Inputs(compare: true, frame: new CompareFrame(null, disposed, true, false)));

        Assert.Equal(0, frame.CompareImages);
        Assert.Equal(9, context.Fills.Count);          // panel + 2 x 4 cạnh viền
        Assert.Empty(context.Images);
    }

    // ---- nâng cấp cùng ảnh (ADR 0008) --------------------------------------------------------

    [Fact]
    public void Upgrade_KeepsTheOldBitmapOnScreenUntilTheNewOneIsUploaded_ThenDrawsItInTheSameBox()
    {
        var preview = Image(shade: 1);
        var full = Image(shade: 2);
        _composer.Set(preview);
        Draw(out _);
        var changed = 0;
        _composer.CurrentChanged += (_, _) => changed++;

        var replacedNow = _composer.SetIncoming(full, isFileChange: false, deferUploadBytes: 1);

        Assert.False(replacedNow);
        Assert.Same(preview, _composer.Current);
        Assert.Same(full, _composer.Pending);
        Draw(out var before);
        Assert.Equal(preview.Id, Assert.Single(before.Images).BufferId);

        Assert.True(_dispatcher.RunAll() > 0);                 // các dải upload nền chạy
        Assert.All(_dispatcher.PostedPriorities, priority => Assert.Equal(PhotoReview.Core.Abstractions.UiPriority.Background, priority));
        Assert.Same(full, _composer.Current);
        Assert.Null(_composer.Pending);
        Assert.Equal(1, changed);
        Assert.True(_cache.IsFullyUploaded(full.Id));
        Draw(out var after);
        var drawn = Assert.Single(after.Images);
        Assert.Equal(full.Id, drawn.BufferId);
        Assert.Equal(Assert.Single(before.Images).Destination, drawn.Destination);   // không nhảy layout
        Assert.True(_invalidations > 0);                      // khung kế tiếp được yêu cầu khi chuyển
    }

    [Fact]
    public void Upgrade_PrefetchedAlready_ReplacesImmediately()
    {
        var preview = Image();
        var full = Image();
        _composer.Set(preview);
        _cache.GetOrUpload(full);                              // đã nằm trọn trên GPU

        Assert.True(_composer.SetIncoming(full, isFileChange: false, deferUploadBytes: 1));

        Assert.Same(full, _composer.Current);
        Assert.Null(_composer.Pending);
    }

    [Fact]
    public void Upgrade_BelowTheDeferThreshold_ReplacesImmediately()
    {
        var thumbnail = Image(10, 10);
        var preview = Image(100, 100);                       // 40.000 byte
        _composer.Set(thumbnail);

        Assert.True(_composer.SetIncoming(preview, isFileChange: false, deferUploadBytes: 40_001));
        Assert.Same(preview, _composer.Current);

        var larger = Image(100, 101);                        // 40.400 byte
        Assert.False(_composer.SetIncoming(larger, isFileChange: false, deferUploadBytes: 40_400));
        Assert.Same(preview, _composer.Current);
    }

    [Fact]
    public void FileChange_ReplacesImmediately_AndDropsAPendingUpgrade()
    {
        var first = Image(shade: 1);
        var upgrade = Image(shade: 2);
        var next = Image(shade: 3);
        _composer.Set(first);
        _composer.SetIncoming(upgrade, isFileChange: false, deferUploadBytes: 1);
        Assert.Same(upgrade, _composer.Pending);

        Assert.True(_composer.SetIncoming(next, isFileChange: true, deferUploadBytes: 1));
        _dispatcher.RunAll();                                // dải của bản nâng cấp bị bỏ cũng không đổi được gì

        Assert.Same(next, _composer.Current);
        Assert.Null(_composer.Pending);
        Assert.Equal(0, _composer.PromotionCount);
    }

    [Fact]
    public void ClearingTheImage_ReplacesImmediately_EvenWithAPendingUpgrade()
    {
        _composer.Set(Image());
        _composer.SetIncoming(Image(), isFileChange: false, deferUploadBytes: 1);

        Assert.True(_composer.SetIncoming(null, isFileChange: false, deferUploadBytes: 1));

        Assert.Null(_composer.Current);
        Assert.Null(_composer.Pending);
    }

    [Fact]
    public void FirstImage_WithNothingToUpgradeFrom_ReplacesImmediately()
    {
        Assert.True(_composer.SetIncoming(Image(), isFileChange: false, deferUploadBytes: 1));
        Assert.NotNull(_composer.Current);
    }

    [Fact]
    public void PendingUpgrade_AfterTheDeadline_IsPromotedEvenIfTheUploadNeverCompletes()
    {
        var preview = Image();
        var full = Image();
        _composer.Set(preview);
        _composer.SetIncoming(full, isFileChange: false, deferUploadBytes: 1);
        var (delay, deadline) = Assert.Single(_scheduled);
        Assert.Equal(TimeSpan.FromMilliseconds(PresentationComposer.MaxDeferMs), delay);

        _now += 500;
        Assert.False(_composer.TryPromotePending());          // chưa tới hạn
        _now += 600;
        deadline();                                           // hạn chót (bộ đệm bị bỏ đói)

        Assert.Same(full, _composer.Current);
        Assert.Equal(1, _composer.PromotionCount);
    }

    [Fact]
    public void StaleDeadline_OfASupersededUpgrade_DoesNothing()
    {
        var preview = Image();
        var first = Image(shade: 2);
        var second = Image(shade: 3);
        _composer.Set(preview);
        _composer.SetIncoming(first, isFileChange: false, deferUploadBytes: 1);
        var staleDeadline = _scheduled[0].Action;
        _composer.SetIncoming(second, isFileChange: false, deferUploadBytes: 1);

        _now += 2000;
        staleDeadline();

        Assert.Same(preview, _composer.Current);              // hạn chót của bản cũ không kéo bản mới đi trước
        Assert.Same(second, _composer.Pending);
    }

    [Fact]
    public void PendingUpgrade_DisposedByItsOwner_IsDropped()
    {
        var preview = Image();
        var full = Image();
        _composer.Set(preview);
        _composer.SetIncoming(full, isFileChange: false, deferUploadBytes: 1);

        full.Dispose();
        _dispatcher.RunAll();

        Assert.False(_composer.TryPromotePending());
        Assert.Same(preview, _composer.Current);
        Assert.Null(_composer.Pending);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void RandomNavigation_AlwaysEndsOnTheLastAssignedImage(int seed)
    {
        // INV-1: dù upload xong theo thứ tự nào, hạn chót nào bắn, ảnh cuối cùng presenter gán luôn là ảnh chính.
        var rng = new Random(seed);
        PixelBuffer? last = null;
        for (var step = 0; step < 60; step++)
        {
            switch (rng.Next(5))
            {
                case 0:
                    last = Image(shade: (byte)rng.Next(256));
                    _composer.SetIncoming(last, isFileChange: true, deferUploadBytes: 1);
                    break;
                case 1:
                    if (last is not null)
                    {
                        last = Image(shade: (byte)rng.Next(256));
                        _composer.SetIncoming(last, isFileChange: false, deferUploadBytes: 1);
                    }

                    break;
                case 2:
                    for (var i = rng.Next(1, 6); i > 0; i--)
                    {
                        _dispatcher.RunOne();
                    }

                    break;
                case 3:
                    _now += rng.Next(0, 700);
                    _composer.TryPromotePending();
                    break;
                default:
                    Draw(out _);
                    _composer.BeginCrossfade(TimeSpan.FromMilliseconds(120));
                    break;
            }

            if (_composer.Current is { } current && last is not null)
            {
                Assert.False(current.IsDisposed);
            }
        }

        _dispatcher.RunAll();
        _now += 5000;
        foreach (var (_, action) in _scheduled.ToArray())
        {
            action();
        }

        _composer.TryPromotePending();
        Assert.Same(last, _composer.Current);
        Assert.Null(_composer.Pending);
    }

    // ---- khung đã thấy, crossfade, retain ----------------------------------------------------

    [Fact]
    public void BeginCrossfade_FreezesTheFrameThatWasActuallyDrawn()
    {
        var first = Image(shade: 1);
        var second = Image(shade: 2);
        _composer.Set(first);
        Assert.False(_composer.BeginCrossfade(TimeSpan.FromMilliseconds(100)));   // chưa vẽ khung nào: không có gì để mờ
        var oldBox = new RectD(30, 20, 500, 400);
        Draw(out _, Inputs(box: oldBox, quality: ScalingQuality.Linear));

        Assert.True(_composer.BeginCrossfade(TimeSpan.FromMilliseconds(100)));
        _composer.SetIncoming(second, isFileChange: true);
        Draw(out var context, Inputs(box: new RectD(0, 0, 10, 10)));              // layout mới không ảnh hưởng lớp cũ

        Assert.Same(first, _composer.Crossfade.Outgoing);
        var outgoing = context.Images.Single(i => i.BufferId == first.Id);
        Assert.Equal((oldBox, ImageInterpolation.Linear), (outgoing.Destination, outgoing.Interpolation));
    }

    [Fact]
    public void BeginCrossfade_AfterTheDrawnBufferWasDisposed_HasNothingToFreeze()
    {
        var image = Image();
        _composer.Set(image);
        Draw(out _);
        image.Dispose();

        Assert.Null(_composer.LastDrawn);
        Assert.False(_composer.BeginCrossfade(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void CollectRetainedIds_CoversCurrentPendingAndOutgoing()
    {
        var current = Image(shade: 1);
        var pending = Image(shade: 2);
        var outgoing = Image(shade: 3);
        _composer.Set(outgoing);
        Draw(out _);
        _composer.BeginCrossfade(TimeSpan.FromMilliseconds(200));
        _composer.SetIncoming(current, isFileChange: true);
        _composer.SetIncoming(pending, isFileChange: false, deferUploadBytes: 1);

        var ids = new List<long>();
        _composer.CollectRetainedIds(ids);

        Assert.Equal([current.Id, pending.Id, outgoing.Id], ids.Order());
    }

    // ---- bộ lọc theo chuyển động -------------------------------------------------------------

    [Fact]
    public void HeavyDownscale_WhileMoving_UsesLinear_ThenHighQualityOnceSettled()
    {
        var image = Image(1000, 667);                         // 0,67 MP vào hộp ~100x67: thu nhỏ x100
        _composer.Set(image);
        var boxA = new RectD(10, 10, 100, 66.7);
        var boxB = new RectD(40, 10, 100, 66.7);

        var rest = Draw(out var restContext, Inputs(box: boxA));       // ảnh mới: không phải chuyển động
        var moved = Draw(out var movedContext, Inputs(box: boxB));     // cùng ảnh, hộp đổi: chuyển động
        var stillMoving = Draw(out _, Inputs(box: boxB));              // cùng hộp nhưng chưa đủ SettleMs

        Assert.False(rest.Degraded);
        Assert.Equal(ImageInterpolation.HighQualityCubic, restContext.Images[0].Interpolation);
        Assert.True(moved.Degraded);
        Assert.Equal(ImageInterpolation.Linear, movedContext.Images[0].Interpolation);
        Assert.True(stillMoving.Degraded);
        Assert.True(_composer.IsSettleArmed);
        Assert.Equal(1, _clock.SubscriberCount);

        var before = _invalidations;
        _now += 50;
        _clock.Tick(_now);                                    // chưa đủ 120 ms: vẫn chờ
        Assert.Equal(before, _invalidations);
        _now += 100;
        _clock.Tick(_now);                                    // đã đứng yên đủ lâu: xin một khung HQ
        Assert.Equal(before + 1, _invalidations);

        var settled = Draw(out var settledContext, Inputs(box: boxB));
        Assert.False(settled.Degraded);
        Assert.Equal(ImageInterpolation.HighQualityCubic, settledContext.Images[0].Interpolation);
        Assert.False(_composer.IsSettleArmed);
        Assert.Equal(0, _clock.SubscriberCount);
    }

    [Fact]
    public void SettleClock_AsksForTheHighQualityFrame_ExactlyWhenSettleMsHaveElapsed()
    {
        var image = Image(1000, 667);
        _composer.Set(image);
        Draw(out _, Inputs(box: new RectD(10, 10, 100, 66.7)));
        Draw(out _, Inputs(box: new RectD(40, 10, 100, 66.7)));     // chuyển động tại _now
        Assert.True(_composer.IsSettleArmed);
        var before = _invalidations;

        _now += 119;
        _clock.Tick(_now);                                          // còn 1 ms: vẫn đang chuyển động, chưa xin khung
        Assert.Equal(before, _invalidations);

        _now += 1;                                                  // đúng 120 ms = SettleMs: đã đứng yên
        _clock.Tick(_now);
        Assert.Equal(before + 1, _invalidations);

        var settled = Draw(out var context, Inputs(box: new RectD(40, 10, 100, 66.7)));
        Assert.False(settled.Degraded);                             // cùng ranh giới với Draw: không còn "đang chuyển động"
        Assert.Equal(ImageInterpolation.HighQualityCubic, context.Images[0].Interpolation);
    }
    [Fact]
    public void LightDownscaleWhileMoving_KeepsHighQuality_AndNeverArmsTheSettleClock()
    {
        var image = Image(200, 133);
        _composer.Set(image);
        Draw(out _, Inputs(box: new RectD(0, 0, 150, 100)));

        var moved = Draw(out var context, Inputs(box: new RectD(30, 0, 150, 100)));

        Assert.False(moved.Degraded);
        Assert.Equal(ImageInterpolation.HighQualityCubic, context.Images[0].Interpolation);
        Assert.False(_composer.IsSettleArmed);
        Assert.Equal(0, _clock.SubscriberCount);
    }

    [Fact]
    public void DpiScale_CountsPhysicalPixelsWhenJudgingTheDownscale()
    {
        var image = Image(400, 300);
        _composer.Set(image);
        Draw(out _, Inputs(box: new RectD(0, 0, 100, 75), dpi: 1.0));

        // 100x75 DIP là x16 diện tích ở 100 %, nhưng ở 200 % chỉ x4 (không nặng).
        var moved = Draw(out var context, Inputs(box: new RectD(10, 0, 100, 75), dpi: 2.0));

        Assert.False(moved.Degraded);
        Assert.Equal(ImageInterpolation.HighQualityCubic, context.Images[0].Interpolation);
    }

    [Fact]
    public void Dispose_StopsListeningToTheClockAndTheCache()
    {
        var image = Image(1000, 667);
        _composer.Set(image);
        Draw(out _, Inputs(box: new RectD(0, 0, 100, 67)));
        Draw(out _, Inputs(box: new RectD(10, 0, 100, 67)));
        Assert.Equal(1, _clock.SubscriberCount);

        _composer.Dispose();

        Assert.Equal(0, _clock.SubscriberCount);
        Assert.Null(_composer.Current);
        Assert.Throws<ObjectDisposedException>(() => _composer.SetIncoming(Image(), false));
    }
}
