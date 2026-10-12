using PhotoReview.TestSupport.PixelGolden;

namespace PhotoReview.Shell.Tests.Golden;

/// <summary>
/// WP-28: the measuring instruments of G-PIX (PNG codec, PSNR/SSIM/MAE, shift and edge detection, the verdict of
/// <see cref="PixelGoldenComparer"/>, the diff image) tested on tiny hand-made frames. A golden test is only as good as these.
/// </summary>
public sealed class PixelGoldenSupportTests
{
    private static PixelFrame Gradient(int width = 32, int height = 24) =>
        PixelGoldenScenes.Create(PixelGoldenScenes.Photo, width, height);

    private static FrameRect All(PixelFrame frame) => new(0, 0, frame.Width, frame.Height);

    // ---- PNG ----

    [Fact]
    public void Png_RoundTripsEveryPixel_ForAllRowFilters()
    {
        // The chart has vertical, horizontal and diagonal structure: the writer picks every filter type across its rows.
        var frame = PixelGoldenScenes.Create(PixelGoldenScenes.Chart, 97, 61);

        var decoded = PngFile.Decode(PngFile.Encode(frame));

        Assert.Equal((frame.Width, frame.Height), (decoded.Width, decoded.Height));
        Assert.Equal(frame.Bgra, decoded.Bgra);
    }

    [Fact]
    public void Png_EncodingIsDeterministic()
    {
        var frame = Gradient();

        Assert.Equal(PngFile.Encode(frame), PngFile.Encode(frame));
    }

    [Fact]
    public void Png_WritesTheStandardSignatureAndChunks()
    {
        var bytes = PngFile.Encode(PixelFrame.Solid(2, 3, 10, 20, 30));

        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], bytes[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(bytes, bytes.Length - 8, 4));
        // IEND's CRC is the well-known constant: proves the CRC table and byte order.
        Assert.Equal([0xAE, 0x42, 0x60, 0x82], bytes[^4..]);
    }

    [Fact]
    public void Png_RejectsGarbage()
    {
        Assert.Throws<InvalidDataException>(() => PngFile.Decode([1, 2, 3, 4, 5, 6, 7, 8, 9]));
    }

    [Fact]
    public void Png_ReadsColourChannelsInTheRightOrder()
    {
        var frame = PixelFrame.Solid(1, 1, 11, 22, 33); // B, G, R

        var decoded = PngFile.Decode(PngFile.Encode(frame));

        Assert.Equal((11, 22, 33), (decoded.At(0, 0).B, decoded.At(0, 0).G, decoded.At(0, 0).R));
        Assert.NotEqual(PngFile.Encode(PixelFrame.Solid(1, 1, 33, 22, 11)), PngFile.Encode(frame));
    }

    // ---- scenes ----

    [Theory]
    [InlineData(PixelGoldenScenes.Photo)]
    [InlineData(PixelGoldenScenes.Chart)]
    [InlineData(PixelGoldenScenes.Orient)]
    public void Scenes_AreDeterministicAndOpaque(string scene)
    {
        var first = PixelGoldenScenes.Create(scene, 120, 80);
        var second = PixelGoldenScenes.Create(scene, 120, 80);

        Assert.Equal(first.Bgra, second.Bgra);
        for (var i = 3; i < first.Bgra.Length; i += 4)
        {
            Assert.Equal(255, first.Bgra[i]);
        }
    }

    [Fact]
    public void OrientScene_IsAsymmetricUnderEveryFlipAndRotation()
    {
        var scene = PixelGoldenScenes.Create(PixelGoldenScenes.Orient, 240, 160);

        // Corner colours are distinct, so any of the 7 non-identity orientations changes the picture.
        Assert.NotEqual(scene.At(2, 2), scene.At(237, 2));
        Assert.NotEqual(scene.At(2, 2), scene.At(2, 157));
        Assert.NotEqual(scene.At(237, 2), scene.At(237, 157));
        Assert.NotEqual(scene.At(2, 157), scene.At(237, 157));
    }

    // ---- metrics ----

    [Fact]
    public void Psnr_OfIdenticalFrames_IsInfinite_AndOfOneLevelEverywhere_Is48Point13()
    {
        var a = PixelFrame.Solid(8, 8, 100, 100, 100);
        var b = PixelFrame.Solid(8, 8, 101, 101, 101);

        Assert.True(double.IsPositiveInfinity(PixelMetrics.Psnr(a, a, All(a))));
        Assert.Equal(48.1308, PixelMetrics.Psnr(a, b, All(a)), 3);   // 10 log10(255^2 / 1)
    }

    [Fact]
    public void Psnr_ComparesOnlyTheRegion()
    {
        var a = PixelFrame.Solid(8, 8, 50, 50, 50);
        var b = a.With(7, 7, 0, 0, 0);

        Assert.True(double.IsPositiveInfinity(PixelMetrics.Psnr(a, b, new FrameRect(0, 0, 4, 4))));
        Assert.False(double.IsPositiveInfinity(PixelMetrics.Psnr(a, b, new FrameRect(4, 4, 4, 4))));
    }

    [Fact]
    public void Psnr_UsesAllThreeChannels()
    {
        var a = PixelFrame.Solid(4, 4, 10, 10, 10);
        var blueOff = PixelFrame.Solid(4, 4, 20, 10, 10);
        var redOff = PixelFrame.Solid(4, 4, 10, 10, 20);

        Assert.Equal(PixelMetrics.Psnr(a, blueOff, All(a)), PixelMetrics.Psnr(a, redOff, All(a)), 6);
        Assert.True(PixelMetrics.Psnr(a, blueOff, All(a)) < 100);
    }

    [Fact]
    public void MeanAbsoluteError_AndMaxAndCount_AreExactOnAKnownDifference()
    {
        var a = PixelFrame.Solid(4, 4, 0, 0, 0);
        var b = a.With(1, 1, 30, 0, 0).With(2, 2, 0, 6, 0); // 36 total / (16 px * 3 channels)

        Assert.Equal(36.0 / 48.0, PixelMetrics.MeanAbsoluteError(a, b, All(a)), 9);
        Assert.Equal(30, PixelMetrics.MaxAbsoluteError(a, b, All(a)));
        Assert.Equal(2, PixelMetrics.DifferingPixels(a, b, All(a), 0));
        Assert.Equal(1, PixelMetrics.DifferingPixels(a, b, All(a), 10));
    }

    [Fact]
    public void Ssim_IsOneForIdentical_AndDropsForAOnePixelShiftOfFinePatterns()
    {
        var scene = PixelGoldenScenes.Create(PixelGoldenScenes.Chart, 64, 48);
        var moved = Translate(scene, 1, 0);

        Assert.Equal(1.0, PixelMetrics.Ssim(scene, scene, All(scene)), 9);
        Assert.True(PixelMetrics.Ssim(scene, moved, All(scene)) < 0.9);
    }

    [Fact]
    public void Ssim_OfASlightlyDarkerCopy_StaysHigh_ButBelowOne()
    {
        var scene = Gradient(64, 48);
        var darker = new PixelFrame(64, 48, [.. scene.Bgra.Select((v, i) => i % 4 == 3 ? v : (byte)Math.Max(0, v - 3))]);

        var ssim = PixelMetrics.Ssim(scene, darker, All(scene));

        Assert.InRange(ssim, 0.95, 0.99999);
    }

    [Fact]
    public void BestShift_FindsAKnownOffset_AndPrefersZeroOnTies()
    {
        var scene = PixelGoldenScenes.Create(PixelGoldenScenes.Chart, 80, 60);
        var shifted = Translate(scene, 2, -1);
        var region = new FrameRect(10, 10, 60, 40);

        Assert.Equal((0, 0), PixelMetrics.BestShift(scene, scene, region, 3));
        // The picture moved right 2 and up 1 (Translate(2, -1)): actual(x + 2, y - 1) == expected(x, y).
        Assert.Equal((2, -1), PixelMetrics.BestShift(scene, shifted, region, 3));
        var flat = PixelFrame.Solid(80, 60, 7, 7, 7);
        Assert.Equal((0, 0), PixelMetrics.BestShift(flat, flat, region, 3));
    }

    [Fact]
    public void ContentBounds_FindsThePictureOnTheCanvas_OrNothing()
    {
        var canvas = PixelFrame.Solid(40, 30, 0x10, 0x10, 0x10);
        var data = (byte[])canvas.Bgra.Clone();
        for (var y = 5; y < 20; y++)
        {
            for (var x = 8; x < 30; x++)
            {
                var i = ((y * 40) + x) * 4;
                data[i] = 200;
                data[i + 1] = 100;
                data[i + 2] = 50;
            }
        }

        var frame = new PixelFrame(40, 30, data);

        Assert.Equal(new FrameRect(8, 5, 22, 15), PixelMetrics.ContentBounds(frame, All(frame), PixelGoldenCatalog.Canvas, 5));
        Assert.True(PixelMetrics.ContentBounds(canvas, All(canvas), PixelGoldenCatalog.Canvas, 5).IsEmpty);
    }

    [Fact]
    public void ChangedRegions_ReturnsOneBoxPerPanel_AndBridgesAntiAliasGaps()
    {
        var before = PixelFrame.Solid(100, 60, 20, 20, 20);
        var after = before;
        after = Paint(after, new FrameRect(5, 5, 30, 12), 90);
        after = Paint(after, new FrameRect(60, 40, 30, 12), 90);
        after = after.With(20, 10, 20, 20, 20); // a hole inside the first panel

        var regions = PixelMetrics.ChangedRegions(before, after, 6);

        Assert.Equal([new FrameRect(5, 5, 30, 12), new FrameRect(60, 40, 30, 12)], regions);
    }

    [Fact]
    public void ChangedRegions_IgnoresChangesBelowTheToleranceAndSpecks()
    {
        var before = PixelFrame.Solid(50, 50, 20, 20, 20);
        var faint = Paint(before, new FrameRect(5, 5, 20, 20), 24);       // +4 < tolerance 6
        var speck = before.With(40, 40, 255, 255, 255);                    // one pixel < minArea

        Assert.Empty(PixelMetrics.ChangedRegions(before, faint, 6));
        Assert.Empty(PixelMetrics.ChangedRegions(before, speck, 6));
    }

    // ---- verdicts ----

    private static PixelGoldenCase CaseOf(PixelGoldenExpectation expectation) =>
        new("t", PixelGoldenScenes.Photo, 64, 48, 64, 48, 1.0, 0, 0, 0, false, 1, false, expectation);

    private static PixelGoldenMeasure Measure(PixelGoldenExpectation expectation, PixelFrame golden, PixelFrame actual) =>
        PixelGoldenComparer.Measure(CaseOf(expectation), golden, actual, All(golden), All(golden));

    [Fact]
    public void Verdict_Identical_AcceptsEqualAndRejectsASingleLevel()
    {
        var golden = Gradient(64, 48);

        Assert.Empty(PixelGoldenComparer.Violations(Measure(PixelGoldenExpectation.Identical, golden, golden)));
        var off = golden.With(30, 20, (byte)(golden.At(30, 20).B ^ 1), golden.At(30, 20).G, golden.At(30, 20).R);
        var violations = PixelGoldenComparer.Violations(Measure(PixelGoldenExpectation.Identical, golden, off));
        Assert.Contains(violations, v => v.Contains("max channel delta 1"));
    }

    [Fact]
    public void Verdict_NearIdentical_AllowsOneLevelOnAFifthOfThePixelsButNotTwo()
    {
        var golden = Gradient(64, 48);
        var one = Nudge(golden, 1);
        var two = Nudge(golden, 2);

        Assert.Empty(PixelGoldenComparer.Violations(Measure(PixelGoldenExpectation.NearIdentical, golden, one)));
        Assert.NotEmpty(PixelGoldenComparer.Violations(Measure(PixelGoldenExpectation.NearIdentical, golden, two)));
    }

    // Every fifth pixel gets +delta on all channels (the shape of WPF's own +/-1 noise at DPI > 1).
    private static PixelFrame Nudge(PixelFrame frame, int delta)
    {
        var data = (byte[])frame.Bgra.Clone();
        for (var p = 0; p < frame.Width * frame.Height; p += 5)
        {
            for (var c = 0; c < 3; c++)
            {
                data[(p * 4) + c] = (byte)Math.Min(250, data[(p * 4) + c] + delta);
            }
        }

        return new PixelFrame(frame.Width, frame.Height, data);
    }

    [Theory]
    [InlineData(PixelGoldenExpectation.Fit, 32.0)]
    [InlineData(PixelGoldenExpectation.Zoom, 28.0)]
    public void Verdict_PsnrBar_IsTheCardsBar(PixelGoldenExpectation expectation, double bar)
    {
        var golden = PixelFrame.Solid(64, 48, 100, 100, 100);

        // Uniform error e gives PSNR = 20 log10(255 / e): pick e just under and just over the bar.
        var tooBad = (int)Math.Ceiling(255 / Math.Pow(10, (bar - 0.3) / 20));
        var fine = (int)Math.Floor(255 / Math.Pow(10, (bar + 0.3) / 20));
        var worse = PixelFrame.Solid(64, 48, (byte)(100 + tooBad), (byte)(100 + tooBad), (byte)(100 + tooBad));
        var better = PixelFrame.Solid(64, 48, (byte)(100 + fine), (byte)(100 + fine), (byte)(100 + fine));

        Assert.Contains(PixelGoldenComparer.Violations(Measure(expectation, golden, worse)), v => v.Contains("PSNR"));
        Assert.DoesNotContain(PixelGoldenComparer.Violations(Measure(expectation, golden, better)), v => v.Contains("PSNR"));
    }

    [Fact]
    public void Verdict_Geometry_RejectsATwoPixelShiftOfARealPicture()
    {
        var golden = PixelGoldenScenes.Create(PixelGoldenScenes.Chart, 96, 72);
        var moved = Translate(golden, 2, 0);
        var viewport = new FrameRect(0, 0, 96, 72);

        var measure = PixelGoldenComparer.Measure(CaseOf(PixelGoldenExpectation.HighFrequency), golden, moved, viewport, viewport);

        Assert.Equal(2, measure.ShiftX);
        Assert.Contains(PixelGoldenComparer.Violations(measure), v => v.StartsWith("geometry", StringComparison.Ordinal));
    }

    [Fact]
    public void Verdict_Geometry_AcceptsAOnePixelShift()
    {
        var golden = PixelGoldenScenes.Create(PixelGoldenScenes.Photo, 96, 72);
        var moved = Translate(golden, 0, 1);
        var viewport = new FrameRect(0, 0, 96, 72);

        var measure = PixelGoldenComparer.Measure(CaseOf(PixelGoldenExpectation.HalfPixel), golden, moved, viewport, viewport);

        Assert.DoesNotContain(PixelGoldenComparer.Violations(measure), v => v.StartsWith("geometry", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(10, 8, 40, 20, 0)]    // exact
    [InlineData(13, 8, 37, 20, 3)]    // left edge only
    [InlineData(10, 11, 40, 17, 3)]   // top edge only
    [InlineData(10, 8, 37, 20, 3)]    // right edge only
    [InlineData(10, 8, 40, 17, 3)]    // bottom edge only
    [InlineData(10, 8, 41, 20, 1)]    // one pixel: within the bar
    public void Verdict_EdgeError_IsMeasuredPerEdgeFromTheShellLayoutAgainstTheDetectedPictureEdge(int x, int y, int w, int h, int expected)
    {
        // A 40x20 picture at (10, 8) on the canvas of a 100x60 viewport; the shell claims another box.
        var canvas = PixelFrame.Solid(100, 60, 0x10, 0x10, 0x10);
        var golden = Paint(canvas, new FrameRect(10, 8, 40, 20), 200);
        var viewport = new FrameRect(0, 0, 100, 60);

        var measure = PixelGoldenComparer.Measure(CaseOf(PixelGoldenExpectation.Zoom), golden, golden, viewport, new FrameRect(x, y, w, h));

        Assert.Equal(expected, measure.EdgeError);
        Assert.Equal(expected > PixelGoldenThresholds.GeometryPixels,
            PixelGoldenComparer.Violations(measure).Any(v => v.Contains("edge", StringComparison.Ordinal)));
    }

    [Fact]
    public void Verdict_EdgeError_IgnoresEdgesClippedByTheViewport()
    {
        // The picture is larger than the viewport: nothing of its edges is visible, so nothing can be off.
        var golden = PixelFrame.Solid(100, 60, 200, 200, 200);
        var viewport = new FrameRect(0, 0, 100, 60);

        var measure = PixelGoldenComparer.Measure(CaseOf(PixelGoldenExpectation.Zoom), golden, golden, viewport, new FrameRect(-30, -20, 300, 200));

        Assert.Equal(0, measure.EdgeError);
    }

    [Fact]
    public void Verdict_ScrollBarStrips_AreHeldToTheirOwnFloor()
    {
        var golden = PixelFrame.Solid(60, 40, 22, 22, 22);
        var actual = Paint(golden, new FrameRect(50, 0, 10, 40), 30); // a strip 8 levels off (33.6 dB), outside the 50x30 viewport
        var viewport = new FrameRect(0, 0, 50, 30);

        var measure = PixelGoldenComparer.Measure(CaseOf(PixelGoldenExpectation.Zoom), golden, actual, viewport, viewport);

        Assert.True(measure.ChromePsnr < PixelGoldenThresholds.ChromePsnr);
        Assert.Contains(PixelGoldenComparer.Violations(measure), v => v.Contains("scroll bar"));
    }

    [Fact]
    public void Thresholds_AreTheSection73Bars_AndThePinnedMeasuredFloors()
    {
        Assert.Equal(1, PixelGoldenThresholds.GeometryPixels);
        Assert.Equal(2, PixelGoldenThresholds.PanelBoxPixels);
        Assert.Equal(45.0, PixelGoldenThresholds.ChromePsnr);
        // (ImagePsnr, ImageSsim, ImageMae, FramePsnr, MaxChannelDelta)
        Assert.Equal(new PixelGoldenBar(double.PositiveInfinity, 1.0, 0.0, 60.0, 0), PixelGoldenThresholds.For(PixelGoldenExpectation.Identical));
        Assert.Equal(new PixelGoldenBar(50.0, 0.999, 0.5, 50.0, 1), PixelGoldenThresholds.For(PixelGoldenExpectation.NearIdentical));
        Assert.Equal(new PixelGoldenBar(32.0, 0.93, 4.0, 32.0, -1), PixelGoldenThresholds.For(PixelGoldenExpectation.Fit));
        Assert.Equal(new PixelGoldenBar(28.0, 0.88, 6.0, 28.0, -1), PixelGoldenThresholds.For(PixelGoldenExpectation.Zoom));
        Assert.Equal(new PixelGoldenBar(19.0, 0.40, 17.0, 19.0, -1), PixelGoldenThresholds.For(PixelGoldenExpectation.HighFrequency));
        Assert.Equal(new PixelGoldenBar(9.0, 0.05, 60.0, 9.0, -1), PixelGoldenThresholds.For(PixelGoldenExpectation.HalfPixel));
    }

    [Fact]
    public void Verdict_ZoomFloors_SsimAndMae_AreEnforcedIndependentlyOfPsnr()
    {
        // Structure lost but PSNR still fine: a fine picture replaced by its own mean colour per 8x8 block. MAE and SSIM must object.
        var golden = PixelGoldenScenes.Create(PixelGoldenScenes.Chart, 96, 72);
        var flat = new byte[golden.Bgra.Length];
        for (var by = 0; by < 72; by += 8)
        {
            for (var bx = 0; bx < 96; bx += 8)
            {
                var sum = new int[3];
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        for (var c = 0; c < 3; c++)
                        {
                            sum[c] += golden.Bgra[((((by + y) * 96) + bx + x) * 4) + c];
                        }
                    }
                }

                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        for (var c = 0; c < 3; c++)
                        {
                            flat[((((by + y) * 96) + bx + x) * 4) + c] = (byte)(sum[c] / 64);
                        }

                        flat[((((by + y) * 96) + bx + x) * 4) + 3] = 255;
                    }
                }
            }
        }

        var blurred = new PixelFrame(96, 72, flat);
        var viewport = new FrameRect(0, 0, 96, 72);

        var measure = PixelGoldenComparer.Measure(CaseOf(PixelGoldenExpectation.Zoom), golden, blurred, viewport, viewport);
        var violations = PixelGoldenComparer.Violations(measure);

        Assert.Contains(violations, v => v.Contains("SSIM", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("MAE", StringComparison.Ordinal));
    }

    // ---- diff image ----

    [Fact]
    public void DiffImage_HasThreePanelsAndAmplifiesTheDifference()
    {
        var expected = PixelFrame.Solid(6, 4, 10, 10, 10);
        var actual = expected.With(2, 1, 20, 10, 10);

        var composed = PixelDiffReport.Compose(expected, actual);

        Assert.Equal((6 * 3) + 8, composed.Width);
        Assert.Equal(4, composed.Height);
        Assert.Equal((10, 10, 10), composed.At(0, 0));                 // expected panel
        Assert.Equal((20, 10, 10), composed.At(6 + 4 + 2, 1));         // actual panel
        Assert.Equal((40, 0, 0), composed.At(6 + 4 + 6 + 4 + 2, 1));   // |10| x 4 on blue
        Assert.Equal((0, 0, 0), composed.At(6 + 4 + 6 + 4 + 3, 1));    // no difference elsewhere
    }

    [Fact]
    public void DiffImage_IsWrittenAsAPngThatReadsBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wp28-diff-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = PixelDiffReport.Write(directory, "a/b:c", PixelFrame.Solid(4, 4, 1, 2, 3), PixelFrame.Solid(4, 4, 4, 5, 6));

            Assert.True(File.Exists(path));
            Assert.EndsWith("-diff.png", path, StringComparison.Ordinal);
            Assert.Equal(4 * 3 + 8, PngFile.Read(path).Width);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PixelFrame Paint(PixelFrame frame, FrameRect rect, byte value)
    {
        var data = (byte[])frame.Bgra.Clone();
        for (var y = rect.Y; y < rect.Bottom; y++)
        {
            for (var x = rect.X; x < rect.Right; x++)
            {
                var i = ((y * frame.Width) + x) * 4;
                data[i] = value;
                data[i + 1] = value;
                data[i + 2] = value;
            }
        }

        return new PixelFrame(frame.Width, frame.Height, data);
    }

    /// <summary>The picture moved by (dx, dy) (content that leaves is dropped; the vacated edge repeats the border).</summary>
    private static PixelFrame Translate(PixelFrame frame, int dx, int dy)
    {
        var data = new byte[frame.Bgra.Length];
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                var sx = Math.Clamp(x - dx, 0, frame.Width - 1);
                var sy = Math.Clamp(y - dy, 0, frame.Height - 1);
                Buffer.BlockCopy(frame.Bgra, ((sy * frame.Width) + sx) * 4, data, ((y * frame.Width) + x) * 4, 4);
            }
        }

        return new PixelFrame(frame.Width, frame.Height, data);
    }
}
