using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Mutation-testing gap tests (Stryker round 2) for the managed pixel path of the LibRaw decoder: the resampler against an independent
/// per-pixel reference (the output pixels are what the user sees for a full RAW decode), the validation boundaries and the cancellation
/// check, plus the plausibility rules of the raw-structure reader (the memory guard's classification input). Pure managed, no native
/// library.
/// </summary>
public sealed class RgbBgraResamplerMutationGapTests
{
    private static byte[] Noise(int width, int height, int channels, int seed)
    {
        var data = new byte[width * height * channels];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Run(byte[] source, int sw, int sh, int tw, int th, int channels)
    {
        var bgra = new byte[tw * th * 4];
        RgbBgraResampler.Resize(source, sw, sh, bgra, tw, th, CancellationToken.None, channels);
        return bgra;
    }

    /// <summary>Reference: every target pixel is the rounded-half-up mean of the source pixels its footprint covers (at least one per axis).</summary>
    private static byte[] ReferenceBox(byte[] source, int sw, int sh, int tw, int th, int channels)
    {
        var result = new byte[tw * th * 4];
        for (var y = 0; y < th; y++)
        {
            for (var x = 0; x < tw; x++)
            {
                var yStart = (int)((long)y * sh / th);
                var yEnd = Math.Max(yStart + 1, (int)((long)(y + 1) * sh / th));
                var xStart = (int)((long)x * sw / tw);
                var xEnd = Math.Max(xStart + 1, (int)((long)(x + 1) * sw / tw));
                var sums = new long[3];
                long count = 0;
                for (var sy = yStart; sy < yEnd; sy++)
                {
                    for (var sx = xStart; sx < xEnd; sx++)
                    {
                        for (var c = 0; c < 3; c++) sums[c] += source[((sy * sw) + sx) * channels + (channels == 3 ? c : 0)];
                        count++;
                    }
                }

                var at = ((y * tw) + x) * 4;
                result[at] = (byte)((sums[2] + (count / 2)) / count);     // B
                result[at + 1] = (byte)((sums[1] + (count / 2)) / count); // G
                result[at + 2] = (byte)((sums[0] + (count / 2)) / count); // R
                result[at + 3] = 255;
            }
        }

        return result;
    }

    /// <summary>Reference: standard bilinear sampling at pixel centres, edges clamped, rounded to the nearest even.</summary>
    private static byte[] ReferenceBilinear(byte[] source, int sw, int sh, int tw, int th, int channels)
    {
        var result = new byte[tw * th * 4];
        for (var y = 0; y < th; y++)
        {
            for (var x = 0; x < tw; x++)
            {
                var fy = Math.Clamp((y + 0.5) * sh / th - 0.5, 0, sh - 1.0);
                var fx = Math.Clamp((x + 0.5) * sw / tw - 0.5, 0, sw - 1.0);
                int y0 = (int)Math.Floor(fy), x0 = (int)Math.Floor(fx);
                int y1 = Math.Min(y0 + 1, sh - 1), x1 = Math.Min(x0 + 1, sw - 1);
                double wy = fy - y0, wx = fx - x0;
                var at = ((y * tw) + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    int ch = channels == 3 ? c : 0;
                    double Pixel(int px, int py) => source[((py * sw) + px) * channels + ch];
                    var top = Pixel(x0, y0) + ((Pixel(x1, y0) - Pixel(x0, y0)) * wx);
                    var bottom = Pixel(x0, y1) + ((Pixel(x1, y1) - Pixel(x0, y1)) * wx);
                    var value = (byte)Math.Clamp((int)Math.Round(top + ((bottom - top) * wy)), 0, 255);
                    result[at + (2 - c)] = value; // output is B, G, R
                }

                result[at + 3] = 255;
            }
        }

        return result;
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(1, 3)]
    [InlineData(3, 1)]
    [InlineData(1, 1)]
    public void Resize_UpscaleOrMildDownscale_MatchesTheBilinearReference(int channels, int variant)
    {
        var (sw, sh, tw, th) = variant == 3 ? (3, 2, 5, 4) : (7, 5, 4, 3); // 3x2 -> 5x4 (up), 7x5 -> 4x3 (ratios 1.75 / 1.67)
        var source = Noise(sw, sh, channels, seed: 11 + channels);

        Assert.Equal(ReferenceBilinear(source, sw, sh, tw, th, channels), Run(source, sw, sh, tw, th, channels));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public void Resize_WidthShrinksMoreThanTwiceWhileHeightGrows_UsesAreaAveragingForTheWholeImage(int channels)
    {
        // 40x6 -> 10x12: the width ratio (4) selects the area average even though the height is upscaled.
        var source = Noise(40, 6, channels, seed: 21);

        Assert.Equal(ReferenceBox(source, 40, 6, 10, 12, channels), Run(source, 40, 6, 10, 12, channels));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public void Resize_HeightShrinksMoreThanTwiceWhileWidthGrows_UsesAreaAveragingForTheWholeImage(int channels)
    {
        var source = Noise(6, 40, channels, seed: 22);

        Assert.Equal(ReferenceBox(source, 6, 40, 12, 10, channels), Run(source, 6, 40, 12, 10, channels));
    }

    [Fact]
    public void Resize_ReductionOfExactlyTwoOnOneAxis_StaysBilinear()
    {
        // 8x4 -> 4x4: the width ratio is exactly the threshold (2.0), which is NOT above it. Values chosen so a half-way mean
        // rounds differently in bilinear (to even) and in the area average (half up): (2 + 3) / 2 = 2.5.
        byte[] source = [2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3];
        var gray = Run(source, 8, 4, 4, 4, channels: 1);

        Assert.Equal(ReferenceBilinear(source, 8, 4, 4, 4, 1), gray);
        Assert.Equal(2, gray[0]);
    }

    [Fact]
    public void Resize_ReductionOfExactlyTwoOnTheHeightAxis_StaysBilinear()
    {
        byte[] source = [2, 2, 2, 2, 3, 3, 3, 3, 2, 2, 2, 2, 3, 3, 3, 3];
        var gray = Run(source, 4, 4, 4, 2, channels: 1);

        Assert.Equal(ReferenceBilinear(source, 4, 4, 4, 2, 1), gray);
        Assert.Equal(2, gray[0]);
    }

    // ---- cancellation ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Resize_SameSizeWithACancelledToken_ThrowsEvenForASinglePixel()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            RgbBgraResampler.Resize(new byte[3], 1, 1, new byte[4], 1, 1, cts.Token));
    }

    [Fact]
    public void Resize_SameSizeWithACancelledToken_ThrowsForALargerImage()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            RgbBgraResampler.Resize(new byte[3 * 16], 4, 4, new byte[4 * 16], 4, 4, cts.Token));
    }

    // ---- validation boundaries ---------------------------------------------------------------------------------------------

    [Fact]
    public void ValidateSourceLength_ZeroHeight_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => RgbBgraResampler.ValidateSourceLength(100, 0, 0));
    }

    [Fact]
    public void ValidateSourceLength_ByteCountOfExactlyIntMaxValue_IsAccepted()
    {
        Assert.Equal(int.MaxValue, RgbBgraResampler.ValidateSourceLength(int.MaxValue, 1, int.MaxValue, channels: 1));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    [InlineData(-1, 5)]
    [InlineData(5, -1)]
    public void ValidateTargetLength_NonPositiveSide_IsRejected(int width, int height)
    {
        Assert.Throws<InvalidDataException>(() => RgbBgraResampler.ValidateTargetLength(width, height));
    }

    [Theory]
    [InlineData(0, 4, 2, 2)]
    [InlineData(4, 0, 2, 2)]
    [InlineData(4, 4, 0, 2)]
    [InlineData(4, 4, 2, 0)]
    public void ResizeToBuffer_AZeroSizedSide_IsRejectedAsInvalidDataBeforeAnyWork(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        Assert.Throws<InvalidDataException>(() =>
            RgbBgraResampler.ResizeToBuffer(new byte[48], sourceWidth, sourceHeight, targetWidth, targetHeight, 3, CancellationToken.None));
    }

    // ---- raw structure reader ----------------------------------------------------------------------------------------------

    private static IntPtr Iparams(int colors, string cdesc, int filters)
    {
        var block = Marshal.AllocHGlobal(512);
        for (var i = 0; i < 512; i++) Marshal.WriteByte(block, i, 0);
        Marshal.WriteInt32(block, LibRawNativeMethods.IParamsColorsOffset, colors);
        Marshal.WriteInt32(block, LibRawNativeMethods.IParamsFiltersOffset, filters);
        for (var i = 0; i < cdesc.Length; i++) Marshal.WriteByte(block, LibRawNativeMethods.IParamsCdescOffset + i, (byte)cdesc[i]);
        return block;
    }

    [Theory]
    [InlineData(1, "G")]
    [InlineData(2, "AZ")]  // 'A' and 'Z' are the first and last accepted letters
    [InlineData(3, "RGB")]
    [InlineData(4, "RGBG")]
    public void TryReadRawStructure_PlausibleColourDescription_ReturnsTheStructure(int colors, string cdesc)
    {
        var block = Iparams(colors, cdesc, unchecked((int)0x94949494));
        try
        {
            var structure = LibRawNativeMethods.TryReadRawStructure(block);

            Assert.Equal(new DecodeMemoryGuard.RawStructure(colors, 0x94949494u), structure);
        }
        finally { Marshal.FreeHGlobal(block); }
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(5, "RGBGE")]
    [InlineData(-1, "R")]
    public void TryReadRawStructure_ColourCountOutsideOneToFour_IsImplausible(int colors, string cdesc)
    {
        var block = Iparams(colors, cdesc, 1);
        try
        {
            Assert.Null(LibRawNativeMethods.TryReadRawStructure(block));
        }
        finally { Marshal.FreeHGlobal(block); }
    }

    [Theory]
    [InlineData("RG\0")]  // third description byte missing (not a capital letter)
    [InlineData("RGb")]
    [InlineData("R@B")]   // '@' is just below 'A'
    [InlineData("R[B")]   // '[' is just above 'Z'
    public void TryReadRawStructure_ColourDescriptionThatIsNotCapitalLetters_IsImplausible(string cdesc)
    {
        var block = Iparams(3, cdesc, 1);
        try
        {
            Assert.Null(LibRawNativeMethods.TryReadRawStructure(block));
        }
        finally { Marshal.FreeHGlobal(block); }
    }

    [Fact]
    public void TryReadRawStructure_OnlyAsManyDescriptionBytesAsColoursAreChecked()
    {
        // Colours = 2: the byte after "RG" is not part of the description and may be anything (here the terminator).
        var block = Iparams(2, "RG", 1);
        try
        {
            Assert.NotNull(LibRawNativeMethods.TryReadRawStructure(block));
        }
        finally { Marshal.FreeHGlobal(block); }
    }

    [Fact]
    public void TryReadRawStructure_NullPointer_IsImplausible()
    {
        Assert.Null(LibRawNativeMethods.TryReadRawStructure(IntPtr.Zero));
    }
}
