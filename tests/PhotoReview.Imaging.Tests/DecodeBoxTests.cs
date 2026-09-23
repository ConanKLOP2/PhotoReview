namespace PhotoReview.Imaging.Tests;

[Trait("Category", "HotPath")]
public sealed class DecodeBoxTests
{
    [Theory(DisplayName = "Fit picks the constraining side and preserves aspect")]
    // 3:2 landscape in a 16:9-ish box: height constrains.
    [InlineData(6000, 4000, 2304, 1280, 1920, 1280)]
    // 2:3 portrait in the same box: height constrains hard.
    [InlineData(4000, 6000, 2304, 1280, 853, 1280)]
    // Panorama: width constrains.
    [InlineData(9000, 2000, 2304, 1280, 2304, 512)]
    // Exact aspect match: both sides hit the box.
    [InlineData(3600, 2000, 1800, 1000, 1800, 1000)]
    public void Fit_LandscapePortraitPanorama_FitsInsideBox(int srcW, int srcH, int boxW, int boxH, int expectedW, int expectedH)
    {
        var (w, h) = new DecodeBox(boxW, boxH).Fit(srcW, srcH);

        Assert.Equal((expectedW, expectedH), (w, h));
        Assert.True(w <= boxW && h <= boxH);
    }

    [Theory(DisplayName = "Fit never upscales an image already inside the box")]
    [InlineData(800, 600)]
    [InlineData(2304, 1280)]
    [InlineData(100, 1280)]
    public void Fit_SourceInsideBox_ReturnsSourceUnchanged(int srcW, int srcH)
    {
        Assert.Equal((srcW, srcH), new DecodeBox(2304, 1280).Fit(srcW, srcH));
    }

    [Fact(DisplayName = "Unbounded box (0 x 0) keeps full size")]
    public void Fit_Unbounded_ReturnsSource()
    {
        Assert.True(DecodeBox.Unbounded.IsUnbounded);
        Assert.Equal((6000, 4000), DecodeBox.Unbounded.Fit(6000, 4000));
    }

    [Fact(DisplayName = "Width-only box (height 0) matches the legacy width-only math")]
    public void Fit_WidthOnly_MatchesLegacyFloor()
    {
        // Legacy decoders: targetH = origH * TargetWidth / origW (integer floor).
        Assert.Equal((2190, 1460), new DecodeBox(2190, 0).Fit(6000, 4000));
        Assert.Equal((2190, 3285), new DecodeBox(2190, 0).Fit(4000, 6000));
    }

    [Fact(DisplayName = "Height-only box (width 0) constrains the height")]
    public void Fit_HeightOnly_ConstrainsHeight()
    {
        Assert.Equal((1500, 1000), new DecodeBox(0, 1000).Fit(6000, 4000));
    }

    [Theory(DisplayName = "FitStored applies the box to the rotated size for EXIF 5-8")]
    // Stored 6000x4000 landscape with EXIF 6/8 displays as a 4000x6000 portrait: the displayed
    // result is 853x1280, i.e. 1280x853 on the stored grid (scaled before rotation).
    [InlineData(true, 1280, 853)]
    // Same stored frame without rotation displays as a landscape: 1920x1280.
    [InlineData(false, 1920, 1280)]
    public void FitStored_Transposed_UsesRotatedDimensions(bool transposed, int expectedStoredW, int expectedStoredH)
    {
        var (w, h) = new DecodeBox(2304, 1280).FitStored(6000, 4000, transposed);

        Assert.Equal((expectedStoredW, expectedStoredH), (w, h));
    }

    [Fact(DisplayName = "Degenerate source sizes are returned unchanged")]
    public void Fit_DegenerateSource_ReturnsSource()
    {
        Assert.Equal((0, 10), new DecodeBox(100, 100).Fit(0, 10));
    }
}
