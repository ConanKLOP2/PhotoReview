using PhotoReview.Imaging.Pixels;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.Imaging.Tests.Pixels;

/// <summary>WP-02: helper <see cref="PixelAssert"/> không được "xanh rỗng" - nó phải bắt một byte lệch, sai kích thước, sai layout.</summary>
public sealed class PixelAssertTests
{
    [Theory]
    [InlineData(PixelLayout.Bgr32, 0)]
    [InlineData(PixelLayout.Pbgra32, 3)]
    [InlineData(PixelLayout.Pbgra32, 2)]
    public void Equal_DetectsOneDifferentByte(PixelLayout layout, int channel)
    {
        using var expected = PixelAssert.CreatePattern(9, 4, layout, seed: 11);
        using var actual = PixelAssert.Clone(expected);
        PixelAssert.Equal(expected, actual);

        actual.GetRow(3)[(8 * 4) + channel] ^= 1;

        var error = Assert.Throws<PixelMismatchException>(() => PixelAssert.Equal(expected, actual));
        Assert.Contains("(8, 3)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Equal_UnusedByte_IsComparedUnlessDisabled()
    {
        using var expected = PixelAssert.CreatePattern(3, 2, PixelLayout.Bgr32, seed: 2);
        using var actual = PixelAssert.Clone(expected);
        actual.GetRow(0)[3] ^= 0xFF;

        Assert.Throws<PixelMismatchException>(() => PixelAssert.Equal(expected, actual));
        PixelAssert.Equal(expected, actual, compareUnusedByte: false);
    }

    [Fact]
    public void Equal_DetectsSizeAndLayoutMismatch()
    {
        using var a = PixelBuffer.Allocate(3, 2, PixelLayout.Bgr32);
        using var transposed = PixelBuffer.Allocate(2, 3, PixelLayout.Bgr32);
        using var otherLayout = PixelBuffer.Allocate(3, 2, PixelLayout.Pbgra32);

        Assert.Throws<PixelMismatchException>(() => PixelAssert.Equal(a, transposed));
        Assert.Throws<PixelMismatchException>(() => PixelAssert.Equal(a, otherLayout));
    }

    [Fact]
    public void BitmapSourceBridge_RoundTripsBytes()
    {
        using var original = PixelAssert.CreatePattern(7, 5, PixelLayout.Pbgra32, seed: 4);

        using var back = PixelAssert.FromBitmapSource(PixelAssert.ToBitmapSource(original), PixelLayout.Pbgra32);

        Assert.NotSame(original, back);
        PixelAssert.Equal(original, back);
    }

    [Fact]
    public void Metrics_ZeroForEqual_PositiveForDifferent()
    {
        using var a = PixelAssert.CreatePattern(8, 8, PixelLayout.Bgr32, seed: 9);
        using var b = PixelAssert.Clone(a);
        Assert.Equal(0, PixelAssert.MeanAbsoluteError(a, b));
        Assert.Equal(double.PositiveInfinity, PixelAssert.Psnr(a, b));

        b.GetRow(0)[0] = (byte)(a.GetRow(0)[0] ^ 0x10);

        Assert.Equal(16.0 / (8 * 8 * 3), PixelAssert.MeanAbsoluteError(a, b), 9);
        Assert.InRange(PixelAssert.Psnr(a, b), 40, 70);
    }
}
