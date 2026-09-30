using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>Peak-memory behaviour of a full RAW decode: no duplicate pixel copies and an upfront headroom check (pure helpers, no real 100 MP decode).</summary>
public sealed class LibRawDecodeMemoryTests
{
    [Fact]
    public void EstimatePeakBytes_HundredMegapixelFullSize_CountsWorkingImageRgbAndBitmap()
    {
        const int Width = 11_600, Height = 8_700; // ~100 MP

        var estimate = DecodeMemoryGuard.EstimatePeakBytes(Width, Height, Width, Height);

        Assert.Equal((long)Width * Height * (8 + 3 + 4), estimate);
        Assert.InRange(estimate, 1_400_000_000L, 1_600_000_000L);
    }

    [Fact]
    public void EstimatePeakBytes_BoundedTarget_CountsOnlyTheTargetBitmap()
    {
        Assert.Equal((6000L * 4000 * 11) + (1920L * 1280 * 4), DecodeMemoryGuard.EstimatePeakBytes(6000, 4000, 1920, 1280));
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
        Assert.Equal(expected, DecodeMemoryGuard.HasHeadroom(estimate, total, load));
    }

    private static BitmapSource Bitmap(byte[] source, int sw, int sh, int tw, int th, int channels)
    {
        using var buffer = RgbBgraResampler.ResizeToBuffer(source, sw, sh, tw, th, channels, CancellationToken.None);
        return RgbBgraResampler.ToBitmap(buffer);
    }

    [Fact]
    public void ResizeToBuffer_ThirtyMegapixelFullSize_DoesNotAllocateAManagedBgraCopy()
    {
        const int Width = 6_500, Height = 4_600; // ~30 MP
        var gray = new byte[Width * Height];
        for (var i = 0; i < gray.Length; i += 4099) gray[i] = 200;
        // Warm the path (JIT, WPF statics) on a small image: only the full-size run is measured.
        _ = Bitmap(new byte[16 * 16], 16, 16, 16, 16, 1);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var bitmap = Bitmap(gray, Width, Height, Width, Height, 1);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal((Width, Height), (bitmap.PixelWidth, bitmap.PixelHeight));
        // The 120 MB of BGRA pixels live in a temporary unmanaged buffer and the bitmap's own copy; a managed byte[] of them would show up here.
        Assert.True(allocated < 8L * 1024 * 1024, $"allocated {allocated} managed bytes for a {Width * Height * 4L}-byte bitmap");
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
