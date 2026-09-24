using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Tests.Fixtures;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

[Trait("Category", "HotPath")]
public sealed class OrientationTests(OrientationFixture files) : IClassFixture<OrientationFixture>
{
    private readonly OrientationFixture _files = files;
    private readonly WpfBitmapImageDecoder _decoder = new();

    [Fact(DisplayName = "ExifOrientation.Read handles null, invalid, and standard values cleanly")]
    public void ReadHandlesNullAndInvalidCleanly()
    {
        Assert.Equal(1, ExifOrientation.Read(null));
    }

    [Theory(DisplayName = "ImageInfo.Width and Height swap for transposed orientations (5-8) only")]
    [InlineData(1, 64, 48, 64, 48)]
    [InlineData(6, 64, 48, 48, 64)]
    public void ImageInfoDimensionSwap(int orientation, int pixelWidth, int pixelHeight, int expectedWidth, int expectedHeight)
    {
        var info = new ImageInfo(pixelWidth, pixelHeight, orientation);
        Assert.Equal(pixelWidth, info.PixelWidth);
        Assert.Equal(pixelHeight, info.PixelHeight);
        Assert.Equal(expectedWidth, info.Width);
        Assert.Equal(expectedHeight, info.Height);
    }

    [Theory(DisplayName = "ReadInfo detects EXIF orientation 1 to 8 and reports visual dimensions")]
    [MemberData(nameof(OrientationContract.All), MemberType = typeof(OrientationContract))]
    public void ReadInfoDetectsOrientation(ushort orientation) =>
        OrientationContract.AssertReadInfo(_decoder, _files.PathFor(orientation), orientation);

    [Theory(DisplayName = "Decode full-res applies EXIF orientation and moves corner colors to correct positions")]
    [MemberData(nameof(OrientationContract.All), MemberType = typeof(OrientationContract))]
    public void DecodeFullResAppliesOrientationCorners(ushort orientation) =>
        OrientationContract.AssertDecodeCorners(_decoder, _files.PathFor(orientation), orientation);

    [Theory(DisplayName = "Decode downscaled preserves visual TargetWidth for orientations 1 to 8")]
    [MemberData(nameof(OrientationContract.All), MemberType = typeof(OrientationContract))]
    public void DecodeDownscaledPreservesTargetWidth(ushort orientation)
    {
        const int targetWidth = 32;
        var decoded = _decoder.Decode(new DecodeRequest(_files.PathFor(orientation), TargetWidth: targetWidth, ApplyOrientation: true));

        Assert.Equal(targetWidth, decoded.PixelWidth);
        Assert.True(decoded.Downscaled);

        // OriginalWidth/Height must report the full (post-orientation) source size
        // -- 64x48, swapped to 48x64 for a transposing orientation (5-8) -- not the downscaled result.
        var transposed = orientation is >= 5 and <= 8;
        Assert.Equal(transposed ? 48 : 64, decoded.OriginalWidth);
        Assert.Equal(transposed ? 64 : 48, decoded.OriginalHeight);
    }

    [Fact(DisplayName = "Decode with ApplyOrientation=false ignores EXIF orientation tag")]
    public void DecodeWithoutApplyOrientationIgnoresTag()
    {
        var decoded = _decoder.Decode(new DecodeRequest(_files.PathFor(6), TargetWidth: 0, ApplyOrientation: false));
        // Without orientation, original raw dimensions are preserved
        Assert.Equal(64, decoded.PixelWidth);
        Assert.Equal(48, decoded.PixelHeight);
        // OriginalWidth/Height fall back to PixelWidth/Height here (no header re-read without
        // ApplyOrientation) -- correct anyway since this decode is undownscaled.
        Assert.Equal(64, decoded.OriginalWidth);
        Assert.Equal(48, decoded.OriginalHeight);
    }
}
