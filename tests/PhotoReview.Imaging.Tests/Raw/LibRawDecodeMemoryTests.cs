using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>Peak-memory behaviour of a full RAW decode: no duplicate pixel copies and an upfront headroom check (pure helpers, no real 100 MP decode).</summary>
public sealed class LibRawDecodeMemoryTests
{
    [Fact]
    public void EstimatePeakBytes_HundredMegapixelFullSize_CountsWorkingImageRgbRawBufferAndBitmap()
    {
        const int Width = 11_600, Height = 8_700; // ~100 MP

        var estimate = DecodeMemoryGuard.EstimatePeakBytes(Width, Height, Width, Height);

        Assert.Equal((long)Width * Height * (8 + 3 + 2 + 4), estimate);
        Assert.InRange(estimate, 1_650_000_000L, 1_750_000_000L);
    }

    [Fact]
    public void EstimatePeakBytes_BayerHundredMegapixelBoundedTarget_IsThirteenBytesPerPixelPlusTheTarget()
    {
        const int Width = 11_600, Height = 8_700;

        var estimate = DecodeMemoryGuard.EstimatePeakBytes(Width, Height, 1920, 1280, Width, Height, DecodeMemoryGuard.RawBufferFamily.Bayer);

        Assert.Equal((long)Width * Height * 13 + 1920L * 1280 * 4, estimate);
    }

    [Fact]
    public void EstimatePeakBytes_LinearHundredMegapixelBoundedTarget_IsNineteenBytesPerPixelPlusTheTarget()
    {
        const int Width = 11_600, Height = 8_700;

        var estimate = DecodeMemoryGuard.EstimatePeakBytes(Width, Height, 1920, 1280, Width, Height, DecodeMemoryGuard.RawBufferFamily.Linear);

        Assert.Equal((long)Width * Height * 19 + 1920L * 1280 * 4, estimate);
    }

    [Fact]
    public void EstimatePeakBytes_FloatHundredMegapixelBoundedTarget_CountsTheFloatBufferAndTheSixteenBitCopyTogether()
    {
        const int Width = 11_600, Height = 8_700;

        var estimate = DecodeMemoryGuard.EstimatePeakBytes(Width, Height, 1920, 1280, Width, Height, DecodeMemoryGuard.RawBufferFamily.Float);

        Assert.Equal((long)Width * Height * (8 + 3 + 24) + 1920L * 1280 * 4, estimate); // 35 B/px: above the 19 of an integer linear file
    }

    [Fact]
    public void EstimatePeakBytes_RawBufferUsesTheSensorSizeNotTheOutputSize()
    {
        // A 10 MP output cropped from a 12 MP sensor: the unpacked buffer is sensor sized.
        var estimate = DecodeMemoryGuard.EstimatePeakBytes(4000, 2500, 100, 100, rawWidth: 4200, rawHeight: 2860);

        Assert.Equal(4000L * 2500 * 11 + 4200L * 2860 * 2 + 100L * 100 * 4, estimate);
    }

    [Fact]
    public void HasHeadroom_BayerHundredMegapixel_IsRefusedWithTwelveHundredMegabytesAndAllowedWithFifteenHundred()
    {
        const int Width = 11_600, Height = 8_700;
        var estimate = DecodeMemoryGuard.EstimatePeakBytes(Width, Height, 1920, 1280, Width, Height);

        Assert.False(MemoryHeadroom.HasHeadroom(estimate, totalAvailableBytes: 1_200_000_000L, memoryLoadBytes: 0));
        Assert.True(MemoryHeadroom.HasHeadroom(estimate, totalAvailableBytes: 1_500_000_000L, memoryLoadBytes: 0));
    }

    private static readonly DecodeMemoryGuard.RawStructure Mosaic = new(Colors: 3, Filters: 0x94949494);
    private static readonly DecodeMemoryGuard.RawStructure NoMosaic = new(Colors: 3, Filters: 0);

    [Theory]
    [InlineData("lossless_dng_load_raw()")]
    [InlineData("packed_dng_load_raw()")]
    [InlineData("deflate_dng_load_raw()")]
    [InlineData("lossy_dng_load_raw()")]
    public void Classify_DngDecoderOnAMosaicFile_IsBayer(string name)
    {
        // A Bayer deflate DNG is no longer charged 8 B/px (a 100 MP file used to be refused ~600 MB early).
        Assert.Equal(DecodeMemoryGuard.RawBufferFamily.Bayer, DecodeMemoryGuard.Classify(name, Mosaic));
    }

    [Theory]
    [InlineData("lossless_dng_load_raw()")]
    [InlineData("packed_dng_load_raw()")]
    [InlineData("deflate_dng_load_raw()")]
    [InlineData("lossy_dng_load_raw()")]
    public void Classify_DngDecoderOnANonMosaicFile_IsLinear(string name)
    {
        // Linear DNG / ProRAW-style (ADOBECOPYPIXEL): 4 x 16-bit raw_alloc, not 2 B/px.
        Assert.Equal(DecodeMemoryGuard.RawBufferFamily.Linear, DecodeMemoryGuard.Classify(name, NoMosaic));
        Assert.Equal(DecodeMemoryGuard.RawBufferFamily.Linear, DecodeMemoryGuard.Classify(name, new DecodeMemoryGuard.RawStructure(4, 0)));
    }

    [Theory]
    [InlineData("lossless_dng_load_raw()")]
    [InlineData("packed_dng_load_raw()")]
    [InlineData("deflate_dng_load_raw()")]
    [InlineData("lossy_dng_load_raw()")]
    public void Classify_DngDecoderWithUnreadableStructure_FallsBackToTheConservativeLinearEstimate(string name)
    {
        Assert.Equal(DecodeMemoryGuard.RawBufferFamily.Linear, DecodeMemoryGuard.Classify(name, null));
    }

    [Theory]
    [InlineData("uncompressed_fp_dng_load_raw()")]
    [InlineData("deflate_fp_dng_load_raw()")]
    public void Classify_FloatDngDecoder_IsFloatWhateverTheStructure(string name)
    {
        Assert.Equal(DecodeMemoryGuard.RawBufferFamily.Float, DecodeMemoryGuard.Classify(name, Mosaic));
        Assert.Equal(DecodeMemoryGuard.RawBufferFamily.Float, DecodeMemoryGuard.Classify(name, NoMosaic));
        Assert.Equal(DecodeMemoryGuard.RawBufferFamily.Float, DecodeMemoryGuard.Classify(name, null));
    }

    [Theory]
    [InlineData("canon_load_raw()")]
    [InlineData("nikon_load_raw()")]
    [InlineData(null)]
    public void Classify_NonDngOrUnknownDecoder_IsBayerEvenWithoutAMosaic(string? name)
    {
        Assert.Equal(DecodeMemoryGuard.RawBufferFamily.Bayer, DecodeMemoryGuard.Classify(name, NoMosaic));
        Assert.Equal(DecodeMemoryGuard.RawBufferFamily.Bayer, DecodeMemoryGuard.Classify(name, null));
    }

    [Fact]
    public void Estimate_LinearFortyEightMegapixelLosslessDng_IsNotChargedAsBayer()
    {
        const int Width = 8_000, Height = 6_000; // 48 MP
        var linear = DecodeMemoryGuard.EstimatePeakBytes(Width, Height, 1920, 1280, Width, Height,
            DecodeMemoryGuard.Classify("lossless_dng_load_raw()", NoMosaic));
        var bayer = DecodeMemoryGuard.EstimatePeakBytes(Width, Height, 1920, 1280, Width, Height,
            DecodeMemoryGuard.Classify("lossless_dng_load_raw()", Mosaic));

        Assert.Equal((long)Width * Height * 6, linear - bayer); // ~288 MB of under-estimate fixed
    }

    [Fact]
    public void Estimate_BayerHundredMegapixelDeflateDng_IsTheThirteenBytesPerPixelBayerResult()
    {
        const int Width = 11_600, Height = 8_700;
        var family = DecodeMemoryGuard.Classify("deflate_dng_load_raw()", Mosaic);

        var estimate = DecodeMemoryGuard.EstimatePeakBytes(Width, Height, 1920, 1280, Width, Height, family);

        Assert.Equal((long)Width * Height * 13 + 1920L * 1280 * 4, estimate);
    }

    /// <summary>Builds a fake libraw_iparams_t block holding the given colors/filters/cdesc at the pinned offsets.</summary>
    private static IntPtr FakeIParams(int colors, uint filters, byte[] cdesc)
    {
        var block = Marshal.AllocHGlobal(440);
        for (var i = 0; i < 440; i++) Marshal.WriteByte(block, i, 0);
        Marshal.WriteInt32(block, LibRawNativeMethods.IParamsColorsOffset, colors);
        Marshal.WriteInt32(block, LibRawNativeMethods.IParamsFiltersOffset, unchecked((int)filters));
        for (var i = 0; i < cdesc.Length; i++) Marshal.WriteByte(block, LibRawNativeMethods.IParamsCdescOffset + i, cdesc[i]);
        return block;
    }

    private static readonly byte[] Rgbg = "RGBG"u8.ToArray();

    [Fact]
    public void TryReadRawStructure_PlausibleBlock_ReturnsColorsAndFilters()
    {
        var block = FakeIParams(3, 0x94949494, Rgbg);
        try
        {
            var structure = LibRawNativeMethods.TryReadRawStructure(block);

            Assert.Equal(new DecodeMemoryGuard.RawStructure(3, 0x94949494), structure);
            Assert.True(structure!.Value.IsBayer);
        }
        finally { Marshal.FreeHGlobal(block); }
    }

    [Fact]
    public void TryReadRawStructure_ColorsOutOfRange_FailsClosed()
    {
        foreach (var colors in new[] { 0, 7, -1 })
        {
            var block = FakeIParams(colors, 0x94949494, Rgbg);
            try { Assert.Null(LibRawNativeMethods.TryReadRawStructure(block)); }
            finally { Marshal.FreeHGlobal(block); }
        }
    }

    [Fact]
    public void TryReadRawStructure_CdescNotLetters_FailsClosedBecauseTheLayoutDoesNotMatch()
    {
        var block = FakeIParams(3, 0x94949494, [0, (byte)'G', (byte)'B', (byte)'G']);
        try { Assert.Null(LibRawNativeMethods.TryReadRawStructure(block)); }
        finally { Marshal.FreeHGlobal(block); }
    }

    [Fact]
    public void TryReadRawStructure_NullPointer_IsNull() => Assert.Null(LibRawNativeMethods.TryReadRawStructure(IntPtr.Zero));

    [Fact]
    public void EstimatePeakBytes_BoundedTarget_CountsOnlyTheTargetBitmap()
    {
        Assert.Equal((6000L * 4000 * 13) + (1920L * 1280 * 4), DecodeMemoryGuard.EstimatePeakBytes(6000, 4000, 1920, 1280));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    public void EstimatePeakBytes_InvalidSource_IsZero(int width, int height)
    {
        Assert.Equal(0, DecodeMemoryGuard.EstimatePeakBytes(width, height, 10, 10));
    }

    [Fact]
    public void EstimatePeakBytes_AbsurdDimensions_SaturatesInsteadOfOverflowing()
    {
        Assert.True(DecodeMemoryGuard.EstimatePeakBytes(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue) > 0);
    }

    [Theory]
    [InlineData(2_000L, 10_000L, 8_000L, true)]   // exactly the headroom is allowed
    [InlineData(1_000L, 10_000L, 8_000L, true)]
    [InlineData(2_001L, 10_000L, 8_000L, false)]
    [InlineData(1L, 10_000L, 10_000L, false)]     // no headroom at all
    [InlineData(1L, 10_000L, 20_000L, false)]     // load above the total (stale reading)
    [InlineData(long.MaxValue, 0L, 0L, true)]     // unknown total never refuses
    public void HasHeadroom_ComparesTheEstimateWithTotalMinusLoad(long estimate, long total, long load, bool expected)
    {
        Assert.Equal(expected, MemoryHeadroom.HasHeadroom(estimate, total, load));
    }

    private static BitmapSource Bitmap(byte[] source, int sw, int sh, int tw, int th, int channels)
    {
        using var buffer = RgbBgraResampler.ResizeToBuffer(source, sw, sh, tw, th, channels, CancellationToken.None);
        return RgbBgraResampler.ToBitmap(buffer);
    }

    [Fact]
    public void ResizeToBuffer_FourMegapixelFullSize_DoesNotAllocateAManagedBgraCopy()
    {
        const int Width = 2_300, Height = 1_800; // ~4 MP
        var gray = new byte[Width * Height];
        for (var i = 0; i < gray.Length; i += 4099) gray[i] = 200;
        // Warm the path (JIT, WPF statics) on a small image: only the full-size run is measured.
        _ = Bitmap(new byte[16 * 16], 16, 16, 16, 16, 1);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var bitmap = Bitmap(gray, Width, Height, Width, Height, 1);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal((Width, Height), (bitmap.PixelWidth, bitmap.PixelHeight));
        // The ~16.6 MB of BGRA pixels live in a temporary unmanaged buffer and the bitmap's own copy; a managed byte[] of them would show up here.
        // The budget is 2 MB (1 MB was too tight): tiered JIT and WPF's first-use statics (even after the warm-up above) can allocate a few
        // hundred KB on this thread, while a 16.6 MB managed copy would still exceed the bound 8x over.
        Assert.True(allocated < 2L * 1024 * 1024, $"allocated {allocated} managed bytes for a {Width * Height * 4L}-byte bitmap");
    }

    [Fact]
    public void ResizeToBuffer_BufferDisposedBeforeTheBitmapIsRead_BitmapKeepsItsOwnPixels()
    {
        byte[] rgb = [255, 0, 0, 0, 255, 0, 0, 0, 255, 10, 20, 30]; // 2x2: red, green / blue, (10,20,30)
        BitmapSource bitmap;
        using (var buffer = RgbBgraResampler.ResizeToBuffer(rgb, 2, 2, 2, 2, 3, CancellationToken.None))
        {
            bitmap = RgbBgraResampler.ToBitmap(buffer);
        }

        Assert.True(bitmap.IsFrozen);
        Assert.Equal(PixelFormats.Bgr32, bitmap.Format);
        var pixels = new byte[16];
        bitmap.CopyPixels(new Int32Rect(0, 0, 2, 2), pixels, 8, 0);
        Assert.Equal([(byte)0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 30, 20, 10, 255], pixels);
    }

    [Fact]
    public void ResizeToBuffer_DownscaledMonochrome_MatchesResizeIntoAnArray()
    {
        const int Source = 90, Target = 30;
        var gray = new byte[Source * Source];
        for (var i = 0; i < gray.Length; i++) gray[i] = (byte)(i * 31 % 251);
        var expected = new byte[Target * Target * 4];
        RgbBgraResampler.Resize(gray, Source, Source, expected, Target, Target, CancellationToken.None, channels: 1);

        var bitmap = Bitmap(gray, Source, Source, Target, Target, 1);

        var actual = new byte[expected.Length];
        bitmap.CopyPixels(actual, Target * 4, 0);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResizeToBuffer_CancelledToken_ThrowsOperationCanceledAndFreesTheBuffer()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            RgbBgraResampler.ResizeToBuffer(new byte[60 * 60 * 3], 60, 60, 20, 20, 3, cts.Token));
    }

    [Fact]
    public void BgraBuffer_DisposedTwice_FreesOnce()
    {
        var buffer = new RgbBgraResampler.BgraBuffer(4, 4);
        buffer.Dispose();
        buffer.Dispose();

        Assert.Equal(IntPtr.Zero, buffer.Pointer);
    }
}
