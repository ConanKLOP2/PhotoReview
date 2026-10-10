using PhotoReview.Imaging.Pixels;
using PhotoReview.Imaging.TurboJpeg;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.Imaging.Tests.Pixels;

/// <summary>WP-05: the integer area filter behind TurboJpeg's fine scale, against a brute-force reference.</summary>
[Trait("Category", "HotPath")]
public sealed class PixelAreaResamplerTests
{
    private static PixelBuffer Fill(int width, int height, Func<int, int, int, byte> value)
    {
        var buffer = PixelBuffer.Allocate(width, height, PixelLayout.Bgr32);
        for (var y = 0; y < height; y++)
        {
            var row = buffer.GetRow(y);
            for (var x = 0; x < width; x++)
            {
                row[x * 4] = value(x, y, 0);
                row[x * 4 + 1] = value(x, y, 1);
                row[x * 4 + 2] = value(x, y, 2);
                row[x * 4 + 3] = 0xFF;
            }
        }

        return buffer;
    }

    /// <summary>Every output channel = round(sum over source pixels of overlap area * value / (sw * sh)), computed per pixel pair.</summary>
    private static byte Reference(PixelBuffer src, int tw, int th, int x, int y, int channel)
    {
        long sw = src.Width, sh = src.Height;
        long sum = 0;
        for (var sy = 0; sy < sh; sy++)
        {
            long oy = Math.Min((y + 1) * sh, (sy + 1) * (long)th) - Math.Max(y * sh, sy * (long)th);
            if (oy <= 0) continue;
            for (var sx = 0; sx < sw; sx++)
            {
                long ox = Math.Min((x + 1) * sw, (sx + 1) * (long)tw) - Math.Max(x * sw, sx * (long)tw);
                if (ox <= 0) continue;
                sum += src.GetRow(sy)[sx * 4 + channel] * ox * oy;
            }
        }

        var total = sw * sh;
        return (byte)((sum + total / 2) / total);
    }

    [Theory]
    [InlineData(3, 1, 2, 1)]
    [InlineData(2, 2, 1, 1)]
    [InlineData(7, 5, 3, 2)]
    [InlineData(10, 10, 10, 4)]
    [InlineData(10, 10, 4, 10)]
    [InlineData(31, 17, 30, 16)]
    [InlineData(64, 48, 13, 11)]
    [InlineData(1, 9, 1, 3)]
    [InlineData(9, 1, 4, 1)]
    public void Resize_MatchesBruteForceAreaAverage(int sw, int sh, int tw, int th)
    {
        using var src = Fill(sw, sh, (x, y, c) => (byte)((x * 37 + y * 91 + c * 53 + x * y * 7) % 256));

        using var dst = PixelAreaResampler.Resize(src, tw, th);

        Assert.Equal((tw, th), (dst.Width, dst.Height));
        Assert.Equal(PixelLayout.Bgr32, dst.Layout);
        for (var y = 0; y < th; y++)
        {
            var row = dst.GetRow(y);
            for (var x = 0; x < tw; x++)
            {
                for (var c = 0; c < 3; c++)
                    Assert.Equal(Reference(src, tw, th, x, y, c), row[x * 4 + c]);
                Assert.Equal(0xFF, row[x * 4 + 3]);
            }
        }
    }

    [Fact]
    public void Resize_HandComputedThreeToTwo()
    {
        // Row of three pixels 0, 90, 255 -> two: (2*0 + 1*90) / 3 = 30 and (1*90 + 2*255) / 3 = 200.
        using var src = Fill(3, 1, (x, _, _) => (byte)(x switch { 0 => 0, 1 => 90, _ => 255 }));

        using var dst = PixelAreaResampler.Resize(src, 2, 1);

        var row = dst.GetRow(0);
        Assert.Equal([30, 30, 30, 0xFF, 200, 200, 200, 0xFF], row.ToArray());
    }

    [Fact]
    public void Resize_SameSize_IsAnIndependentCopy()
    {
        using var src = Fill(5, 4, (x, y, c) => (byte)(x + y * 10 + c));

        using var dst = PixelAreaResampler.Resize(src, 5, 4);

        Assert.NotSame(src, dst);
        PixelAssert.Equal(src, dst);
        src.GetRow(0)[0] ^= 0xFF;
        Assert.NotEqual(src.GetRow(0)[0], dst.GetRow(0)[0]);
    }

    [Fact]
    public void Resize_NeverUpscales_AnAxisKeepsTheSourceSize()
    {
        using var src = Fill(8, 8, (x, y, _) => (byte)(x * y));

        using var dst = PixelAreaResampler.Resize(src, 20, 4);

        Assert.Equal((8, 4), (dst.Width, dst.Height));
    }

    [Fact]
    public void Resize_ConstantImage_StaysConstant_AndLeavesTheSourceUntouched()
    {
        using var src = Fill(101, 67, (_, _, c) => (byte)(200 + c));
        using var snapshot = PixelAssert.Clone(src);

        using var dst = PixelAreaResampler.Resize(src, 33, 29);

        for (var y = 0; y < dst.Height; y++)
        {
            var row = dst.GetRow(y);
            for (var x = 0; x < dst.Width; x++)
                Assert.Equal([(byte)200, (byte)201, (byte)202, (byte)0xFF], row.Slice(x * 4, 4).ToArray());
        }

        PixelAssert.Equal(snapshot, src);
    }

    [Fact]
    public void Resize_RejectsBadArguments_AndDoesNotLeak()
    {
        using var pbgra = PixelBuffer.Allocate(2, 2, PixelLayout.Pbgra32);
        using var bgr = PixelBuffer.Allocate(2, 2, PixelLayout.Bgr32);

        Assert.Throws<ArgumentNullException>(() => PixelAreaResampler.Resize(null!, 1, 1));
        Assert.Throws<ArgumentException>(() => PixelAreaResampler.Resize(pbgra, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelAreaResampler.Resize(bgr, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelAreaResampler.Resize(bgr, 1, 0));
    }
}
