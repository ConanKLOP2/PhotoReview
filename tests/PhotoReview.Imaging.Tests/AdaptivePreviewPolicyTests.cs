using PhotoReview.Imaging;

namespace PhotoReview.Imaging.Tests;

[Trait("Category", "Slow")]
public sealed class AdaptivePreviewPolicyTests
{
    [Fact(DisplayName = "Decode width clamped between MinimumDecodeWidth and MaximumDecodeWidth")]
    public void CalculateTargetDecodeWidth_Clamped()
    {
        // Very small request clamped to 1200
        var small = AdaptivePreviewPolicy.CalculateTargetDecodeWidth(500, 1.0, 1.0);
        Assert.Equal(AdaptivePreviewPolicy.MinimumDecodeWidth, small);

        // Very large request clamped to 4000
        var large = AdaptivePreviewPolicy.CalculateTargetDecodeWidth(5000, 2.0, 1.5);
        Assert.Equal(AdaptivePreviewPolicy.MaximumDecodeWidth, large);

        // Normal request: 1920 * 1.25 * 1.15 = 2760
        var normal = AdaptivePreviewPolicy.CalculateTargetDecodeWidth(1920, 1.25, 1.15);
        Assert.Equal(2760, normal);
    }

    [Fact(DisplayName = "Invalid non-positive or non-finite inputs throw ArgumentOutOfRangeException")]
    public void CalculateTargetDecodeWidth_ThrowsOnInvalid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptivePreviewPolicy.CalculateTargetDecodeWidth(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptivePreviewPolicy.CalculateTargetDecodeWidth(-10));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptivePreviewPolicy.CalculateTargetDecodeWidth(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptivePreviewPolicy.CalculateTargetDecodeWidth(1920, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptivePreviewPolicy.CalculateTargetDecodeWidth(1920, 1.0, -1));
    }

    [Fact(DisplayName = "Box for a 1920x1080 viewport: x1.15 quality, rounded up to 128 px")]
    public void CalculateTargetDecodeBox_FullHd_QuantizedUp()
    {
        // 1920 * 1.15 = 2208 -> 2304; 1080 * 1.15 = 1242 -> 1280.
        var box = AdaptivePreviewPolicy.CalculateTargetDecodeBox(1920, 1080, 1.0, 1.15);

        Assert.Equal(new DecodeBox(2304, 1280), box);
    }

    [Fact(DisplayName = "Box is computed in device pixels (DPI scale applies to both sides)")]
    public void CalculateTargetDecodeBox_DpiScale_AppliesToBothSides()
    {
        // 1536x864 DIPs at 125% is the same 1920x1080 device-pixel viewport.
        Assert.Equal(
            AdaptivePreviewPolicy.CalculateTargetDecodeBox(1920, 1080, 1.0, 1.15),
            AdaptivePreviewPolicy.CalculateTargetDecodeBox(1536, 864, 1.25, 1.15));
    }

    [Fact(DisplayName = "Small window resizes keep the same box (no cache-key thrash)")]
    public void CalculateTargetDecodeBox_SmallResize_SameBox()
    {
        var before = AdaptivePreviewPolicy.CalculateTargetDecodeBox(1920, 1080, 1.0, 1.15);

        Assert.Equal(before, AdaptivePreviewPolicy.CalculateTargetDecodeBox(1930, 1090, 1.0, 1.15));
        Assert.Equal(before, AdaptivePreviewPolicy.CalculateTargetDecodeBox(1900, 1050, 1.0, 1.15));
    }

    [Fact(DisplayName = "A clearly bigger window yields a bigger box")]
    public void CalculateTargetDecodeBox_BiggerWindow_BiggerBox()
    {
        var fullHd = AdaptivePreviewPolicy.CalculateTargetDecodeBox(1920, 1080, 1.0, 1.15);
        var qhd = AdaptivePreviewPolicy.CalculateTargetDecodeBox(2560, 1440, 1.0, 1.15);

        // 2560 * 1.15 = 2944 (exactly 23 quanta, no extra step); 1440 * 1.15 = 1656 -> 1664.
        Assert.Equal(new DecodeBox(2944, 1664), qhd);
        Assert.True(qhd.Width > fullHd.Width && qhd.Height > fullHd.Height);
    }

    [Fact(DisplayName = "Tiny window: long side raised to the minimum, aspect kept")]
    public void CalculateTargetDecodeBox_TinyWindow_LongSideRaisedToMinimum()
    {
        // 500x300 -> x2.4 -> 1200x720 -> quantized 1280x768.
        var box = AdaptivePreviewPolicy.CalculateTargetDecodeBox(500, 300, 1.0, 1.0);

        Assert.Equal(new DecodeBox(1280, 768), box);
        Assert.True(Math.Max(box.Width, box.Height) >= AdaptivePreviewPolicy.MinimumBoxLongSide);
    }

    [Fact(DisplayName = "Huge viewport: each side capped at MaximumBoxSide")]
    public void CalculateTargetDecodeBox_Huge_Capped()
    {
        var box = AdaptivePreviewPolicy.CalculateTargetDecodeBox(5000, 3000, 2.0, 1.5);

        Assert.Equal(new DecodeBox(AdaptivePreviewPolicy.MaximumBoxSide, AdaptivePreviewPolicy.MaximumBoxSide), box);
    }

    [Theory(DisplayName = "Quantize rounds up to a multiple of 128 and caps at 4000")]
    [InlineData(1.0, 128)]
    [InlineData(128.0, 128)]
    [InlineData(128.5, 256)]
    [InlineData(1242.0, 1280)]
    [InlineData(1280.0000000001, 1280)]
    [InlineData(3999.0, 4000)]
    [InlineData(99999.0, 4000)]
    public void Quantize_RoundsUpAndCaps(double side, int expected)
    {
        Assert.Equal(expected, AdaptivePreviewPolicy.Quantize(side));
    }

    [Fact(DisplayName = "Box: invalid non-positive or non-finite inputs throw")]
    public void CalculateTargetDecodeBox_ThrowsOnInvalid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptivePreviewPolicy.CalculateTargetDecodeBox(0, 1080));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptivePreviewPolicy.CalculateTargetDecodeBox(1920, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptivePreviewPolicy.CalculateTargetDecodeBox(1920, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptivePreviewPolicy.CalculateTargetDecodeBox(1920, 1080, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptivePreviewPolicy.CalculateTargetDecodeBox(1920, 1080, 1.0, 0));
    }

    // The pixel-count reduction this change is for, pinned so it cannot silently regress:
    // 1920x1080 window, x1.15 quality. Width-only decoded to 2208 px wide regardless of shape.
    [Theory(DisplayName = "Box decode vs width-only decode: pixel counts for 24 MP landscape and portrait")]
    [InlineData(6000, 4000, 2208 * 1472, 1920 * 1280)]   // 3.25 MP -> 2.46 MP (-24%)
    [InlineData(4000, 6000, 2208 * 3312, 853 * 1280)]    // 7.31 MP -> 1.09 MP (-85%)
    public void BoxDecode_24MpInFullHdWindow_PixelCounts(int srcW, int srcH, int widthOnlyPixels, int boxPixels)
    {
        var width = AdaptivePreviewPolicy.CalculateTargetDecodeWidth(1920, 1.0, 1.15);
        var box = AdaptivePreviewPolicy.CalculateTargetDecodeBox(1920, 1080, 1.0, 1.15);

        var (ow, oh) = new DecodeBox(width, 0).Fit(srcW, srcH);
        var (bw, bh) = box.Fit(srcW, srcH);

        Assert.Equal(widthOnlyPixels, ow * oh);
        Assert.Equal(boxPixels, bw * bh);
        Assert.True(bh <= box.Height && bw <= box.Width);
    }
}

