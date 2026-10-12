using System.Globalization;
using PhotoReview.App.Input;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Overlay;
using PhotoReview.Shell.Tests.Rendering;
using PhotoReview.Shell.Win32.Viewing;
using PhotoReview.TestSupport.PixelGolden;

namespace PhotoReview.Shell.Tests.Golden;

/// <summary>
/// WP-28b: the crossfade frame at t = 0 / 25 / 50 / 75 / 100 % with the frame clock injected (<see cref="FakeFrameClock"/>, 1 tick =
/// 1 ms). WPF has NO equivalent frame to compare with (the WPF <c>OutgoingImage</c> opacity animation cannot be sampled in a
/// RenderTargetBitmap without a running render thread), so there is no WPF golden here and the bar is NOT a 7.3 bar: each frame
/// is compared with a blend computed INDEPENDENTLY from the two end frames the shell itself draws without any fade -
/// <c>out = round(a * outgoing + (1 - a) * incoming)</c> per channel, with <c>a = (1 - t)^2</c> (QuadraticOut, WP-21 / Q-R37) -
/// within 2 levels (the D2D blend rounds on 8-bit premultiplied data; measured max 1). t = 0 and t = 1 must be exact.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PixelGoldenCrossfadeTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private const int DurationMs = 200;
    private const int BlendTolerance = 2;

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

    private sealed class Stage
    {
        public required D2DRenderSurface Surface { get; init; }

        public required PresentationComposer Composer { get; init; }

        public required FakeFrameClock Clock { get; init; }

        public required SizeD Client { get; init; }

        public required double Dpi { get; init; }
    }

    private Stage NewStage(int clientWidth, int clientHeight, double dpi)
    {
        var surface = Own(D2DRenderSurface.CreateOffscreenSurface((int)Math.Round(clientWidth * dpi), (int)Math.Round(clientHeight * dpi), dpi));
        var dispatcher = new ManualDispatcher();
        var cache = Own(new GpuImageCache(surface, dispatcher));
        var clock = new FakeFrameClock();
        var composer = Own(new PresentationComposer(cache, dispatcher, clock, () => { }, ticksPerSecond: 1000, timestamp: () => 0));
        return new Stage { Surface = surface, Composer = composer, Clock = clock, Client = new SizeD(clientWidth, clientHeight), Dpi = dpi };
    }

    private PixelBuffer Picture(string scene, int width, int height) =>
        Own(PixelGoldenShellRenderer.ToPixelBuffer(PixelGoldenScenes.Create(scene, width, height)));

    private static PixelFrame Draw(Stage stage, RectD box, CompareFrame? compare = null)
    {
        var inputs = new PresentationInputs(stage.Client, box, ScalingQuality.HighQuality, ScalingQuality.HighQuality, stage.Dpi,
            compare is not null, compare ?? default);
        using var pixels = RenderTestImages.Render(stage.Surface, dc =>
        {
            dc.Clear(RenderColors.DarkCanvas);
            stage.Composer.Draw(dc, inputs);
        });
        return PixelGoldenShellRenderer.ToFrame(pixels);
    }

    /// <summary>The frame of a picture with no fade at all (what the fade starts from / ends on).</summary>
    private PixelFrame Still(int clientWidth, int clientHeight, double dpi, PixelBuffer picture, RectD box, CompareFrame? compare = null)
    {
        var stage = NewStage(clientWidth, clientHeight, dpi);
        stage.Composer.Set(picture);
        return Draw(stage, box, compare);
    }

    /// <summary>Largest per-channel difference between <paramref name="actual"/> and <c>a*first + (1-a)*second</c> over the region.</summary>
    private static (int Max, int Count) BlendError(PixelFrame actual, PixelFrame outgoing, PixelFrame incoming, double alpha, FrameRect region)
    {
        var max = 0;
        var count = 0;
        for (var y = region.Y; y < region.Bottom; y++)
        {
            for (var x = region.X; x < region.Right; x++)
            {
                var o = outgoing.At(x, y);
                var n = incoming.At(x, y);
                var a = actual.At(x, y);
                var worst = 0;
                worst = Math.Max(worst, Math.Abs(a.B - Mix(o.B, n.B, alpha)));
                worst = Math.Max(worst, Math.Abs(a.G - Mix(o.G, n.G, alpha)));
                worst = Math.Max(worst, Math.Abs(a.R - Mix(o.R, n.R, alpha)));
                if (worst > 0)
                {
                    count++;
                }

                max = Math.Max(max, worst);
            }
        }

        return (max, count);
    }

    private static int Mix(int outgoing, int incoming, double alpha) =>
        (int)Math.Round((alpha * outgoing) + ((1 - alpha) * incoming), MidpointRounding.AwayFromZero);

    private static double QuadraticOutOpacity(double progress) => (1 - progress) * (1 - progress);

    private static bool SameBytes(PixelFrame a, PixelFrame b) => a.Bgra.AsSpan().SequenceEqual(b.Bgra);

    // Outgoing: 3:2 landscape filling the client. Incoming: a portrait pillarboxed in the middle (so the blend also crosses the canvas).
    private static RectD BoxOf(int clientWidth, int clientHeight, double dpiIndependentWidth = 1.0)
    {
        var width = clientHeight * 0.75 * dpiIndependentWidth;
        return new RectD((clientWidth - width) / 2, 0, width, clientHeight);
    }

    private (Stage Stage, PixelBuffer Outgoing, PixelBuffer Incoming, RectD OutBox, RectD InBox, PixelFrame OutFrame, PixelFrame InFrame)
        Prepare(double dpi)
    {
        const int W = 480;
        const int H = 320;
        var outgoing = Picture(PixelGoldenScenes.Photo, 1200, 800);
        var incoming = Picture(PixelGoldenScenes.Photo, 900, 1200); // 3:4
        var outBox = new RectD(0, 0, W, H);
        var inBox = BoxOf(W, H);
        var stage = NewStage(W, H, dpi);
        var outFrame = Still(W, H, dpi, outgoing, outBox);
        var inFrame = Still(W, H, dpi, incoming, inBox);

        stage.Composer.Set(outgoing);
        Draw(stage, outBox); // the frame the viewer last showed: the fade freezes it
        Assert.True(stage.Composer.BeginCrossfade(TimeSpan.FromMilliseconds(DurationMs)));
        stage.Composer.SetIncoming(incoming, isFileChange: true);
        return (stage, outgoing, incoming, outBox, inBox, outFrame, inFrame);
    }

    private static void Advance(Stage stage, long elapsedMs)
    {
        stage.Clock.Tick(10_000); // first frame of the tween = t0
        stage.Clock.Tick(10_000 + elapsedMs);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Crossfade_AtStart_IsExactlyTheOutgoingFrame_AndAtEnd_ExactlyTheIncomingFrame(double dpi)
    {
        var (stage, _, _, _, inBox, outFrame, inFrame) = Prepare(dpi);

        stage.Clock.Tick(10_000); // the first frame of the tween: t = 0
        var atStart = Draw(stage, inBox);
        Assert.True(SameBytes(outFrame, atStart), "t = 0: the frame must be the outgoing picture exactly");

        stage.Clock.Tick(10_000 + DurationMs);
        var atEnd = Draw(stage, inBox);

        Assert.True(SameBytes(inFrame, atEnd), "t = 100 %: the frame must be the incoming picture exactly");
        Assert.False(stage.Composer.Crossfade.IsActive);
        Assert.Equal(0, stage.Clock.SubscriberCount);
    }

    [Theory]
    [InlineData(1.0, 50)]
    [InlineData(1.0, 100)]
    [InlineData(1.0, 150)]
    [InlineData(1.5, 100)]
    public void Crossfade_InTheMiddle_IsTheIndependentBlendOfTheTwoEndFrames(double dpi, long elapsedMs)
    {
        var (stage, _, _, _, inBox, outFrame, inFrame) = Prepare(dpi);
        var progress = elapsedMs / (double)DurationMs;
        var alpha = QuadraticOutOpacity(progress);

        Advance(stage, elapsedMs);
        var frame = Draw(stage, inBox);

        var whole = new FrameRect(0, 0, frame.Width, frame.Height);
        var (max, count) = BlendError(frame, outFrame, inFrame, alpha, whole);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"dpi {dpi} t={progress:0.00} alpha={alpha:0.0000}: max error {max} ({count} px differ)"));
        Assert.True(max <= BlendTolerance,
            string.Create(CultureInfo.InvariantCulture,
                $"t={progress:0.00} alpha={alpha:0.0000}: max channel error {max} ({count} px differ) > {BlendTolerance}"));
        // The blend is not the incoming picture (a missing fade) nor the outgoing one (a fade that never advanced).
        Assert.False(SameBytes(frame, inFrame));
        Assert.False(SameBytes(frame, outFrame));
        Assert.True(stage.Composer.Crossfade.IsActive);
    }

    [Fact]
    public void Crossfade_RestartedInTheMiddle_DropsTheOldFadeAndStartsAgainFromTheFrameThatWasShown()
    {
        var (stage, _, _, _, inBox, outFrame, inFrame) = Prepare(1.0);
        Advance(stage, 100); // half way: alpha 0.25 of the first picture still over the second
        Draw(stage, inBox);

        var third = Picture(PixelGoldenScenes.Chart, 960, 640);
        var thirdBox = new RectD(0, 0, 480, 320);
        var thirdFrame = Still(480, 320, 1.0, third, thirdBox);
        Assert.True(stage.Composer.BeginCrossfade(TimeSpan.FromMilliseconds(DurationMs)));
        stage.Composer.SetIncoming(third, isFileChange: true);
        stage.Clock.Tick(20_000);
        var first = Draw(stage, thirdBox);

        // New fade at alpha 1: the layer is the picture that was main (the portrait) over the third picture; nothing of the very
        // first picture may survive (a leftover old layer would tint the whole frame).
        var portraitArea = new FrameRect((int)inBox.X + 1, 1, (int)inBox.Width - 2, 318);
        Assert.Equal(0, PixelMetrics.MaxAbsoluteError(inFrame, first, portraitArea));
        var left = new FrameRect(1, 1, (int)inBox.X - 2, 318);
        Assert.Equal(0, PixelMetrics.MaxAbsoluteError(thirdFrame, first, left));
        Assert.NotEqual(0, PixelMetrics.MaxAbsoluteError(outFrame, first, left)); // the first picture is gone
        Assert.Equal(2, stage.Composer.Crossfade.StartCount);
    }

    [Fact]
    public void Crossfade_UnderACompareFrame_IsHiddenByThePanelAndStillBlendsInTheMargin()
    {
        var (stage, _, incoming, _, inBox, outFrame, _) = Prepare(1.0);
        var left = Picture(PixelGoldenScenes.Orient, 240, 160);
        var right = Picture(PixelGoldenScenes.Photo, 300, 200);
        var compare = new CompareFrame(left, right, LeftSelected: true, RightSelected: false);
        var inFrameWithCompare = Still(480, 320, 1.0, incoming, inBox, compare);
        var client = new SizeD(480, 320);
        var panel = CompareLayout.Panel(client);

        Advance(stage, 100);
        var frame = Draw(stage, inBox, compare);

        // Order of the layers (XAML): main < outgoing < compare. The panel is the compare frame alone ...
        var inside = new FrameRect((int)panel.X, (int)panel.Y, (int)panel.Width, (int)panel.Height);
        Assert.Equal(0, PixelMetrics.MaxAbsoluteError(inFrameWithCompare, frame, inside));
        // ... and the 8 DIP margin around it is still the blend (alpha 0.25) of the two pictures.
        var alpha = QuadraticOutOpacity(0.5);
        var top = new FrameRect(0, 0, 480, 8);
        var (max, _) = BlendError(frame, outFrame, inFrameWithCompare, alpha, top);
        Assert.True(max <= BlendTolerance, $"margin blend error {max} > {BlendTolerance}");
        Assert.NotEqual(0, PixelMetrics.MaxAbsoluteError(inFrameWithCompare, frame, top)); // the fade is visible there
    }
}
