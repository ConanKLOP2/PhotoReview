using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>RV-T53 (orientation part): <see cref="ExifOrientation.Apply"/> / <see cref="ExifOrientation.CreateTransform"/> pixel mapping on a non-square image.</summary>
[Trait("Category", "HotPath")]
public sealed class ExifOrientationTests
{
    private const int W = 3;
    private const int H = 2;

    /// <summary>3x2 Bgra32 image where pixel (x, y) has B = x + 1, G = y + 1 so every pixel is unique.</summary>
    private static WriteableBitmap Source()
    {
        var pixels = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = ((y * W) + x) * 4;
                pixels[i] = (byte)(x + 1);
                pixels[i + 1] = (byte)(y + 1);
                pixels[i + 3] = 255;
            }
        var bitmap = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, W, H), pixels, W * 4, 0);
        return bitmap;
    }

    private static (int X, int Y) SourceOf(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4];
        image.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[0] - 1, pixel[1] - 1);
    }

    /// <summary>EXIF definition: where source pixel (x, y) of a W x H image lands.</summary>
    private static (int X, int Y) Expected(int orientation, int x, int y) => orientation switch
    {
        1 => (x, y),
        2 => (W - 1 - x, y),
        3 => (W - 1 - x, H - 1 - y),
        4 => (x, H - 1 - y),
        5 => (y, x),
        6 => (H - 1 - y, x),
        7 => (H - 1 - y, W - 1 - x),
        8 => (y, W - 1 - x),
        _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
    };

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Apply_NonSquareImage_PutsEverySourcePixelWhereTheExifOrientationDefinesIt(int orientation)
    {
        var result = WpfExifOrientation.Apply(Source(), orientation);

        var transposed = orientation >= 5;
        Assert.Equal(transposed ? H : W, result.PixelWidth);
        Assert.Equal(transposed ? W : H, result.PixelHeight);
        Assert.True(result.IsFrozen);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var (ex, ey) = Expected(orientation, x, y);
                Assert.Equal((x, y), SourceOf(result, ex, ey));
            }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(65535)]
    [InlineData(-3)]
    public void Apply_OrientationOutsideTwoToEight_ReturnsTheSameFrozenInstanceUntouched(int orientation)
    {
        var source = Source();
        Assert.False(source.IsFrozen);

        var result = WpfExifOrientation.Apply(source, orientation);

        Assert.Same(source, result);
        Assert.True(result.IsFrozen);
        Assert.Equal((W, H), (result.PixelWidth, result.PixelHeight));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(65535)]
    public void CreateTransform_OrientationOutsideOneToEight_IsTheIdentity(int orientation) =>
        Assert.Equal(Transform.Identity.Value, WpfExifOrientation.CreateTransform(orientation).Value);
}