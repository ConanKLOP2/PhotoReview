using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.TestSupport;
using PhotoReview.TestSupport.Windows.Fixtures;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// WP-03 review checklist of the native-decoder review (lead, 2026-10-10): buffer lifetime on a failed copy (no finalizer
/// needed), what the decode-level ArgumentException relabel may and may not swallow, the int-based output size limit, and
/// damaged EXIF metadata in the embedded-thumbnail reader. Native pixel memory is counted process-wide, hence GlobalState.
/// </summary>
[Collection("GlobalState")]
public sealed class WicDirectDecoderWp03ReviewTests : IDisposable
{
    private readonly TempRoot _root = new("wp03-review");

    public void Dispose() => _root.Dispose();

    private static long SettledLiveCount()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return NativePixelMemory.LiveCount;
    }

    /// <summary>Takes ownership like a real codec and fails (or not) afterwards.</summary>
    private sealed class ThrowingCodec(Func<Exception> make) : IPlatformImageCodec
    {
        public string Name => "throwing";

        public object FromPixels(PixelBuffer pixels)
        {
            pixels.Dispose();
            throw make();
        }

        public PixelLease ToPixels(object platformImage) => throw new NotSupportedException();
    }

    [Fact(DisplayName = "A failed CopyPixels disposes the native buffer immediately, without waiting for a finalizer")]
    public void AllocateAndFill_CopyThrows_BufferFreedAtOnce()
    {
        var before = SettledLiveCount();

        var error = Record.Exception(() =>
            WicDirectDecoder.AllocateAndFill(16, 16, PixelLayout.Bgr32, _ => throw new InvalidOperationException("copy"), false));

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(before, NativePixelMemory.LiveCount); // no GC in between: a leaked buffer would still be counted
    }

    [Fact(DisplayName = "A COM failure of the colour transform during the copy is the ICC fallback error and frees the buffer")]
    public void AllocateAndFill_ComFailureWithTransform_IsNotSupportedAndFreed()
    {
        var before = SettledLiveCount();

        var error = Record.Exception(() =>
            WicDirectDecoder.AllocateAndFill(16, 16, PixelLayout.Pbgra32, _ => throw Marshal.GetExceptionForHR(unchecked((int)0x88982F50), new IntPtr(-1))!, true));

        Assert.IsType<NotSupportedException>(error);
        Assert.Equal(before, NativePixelMemory.LiveCount);
    }

    [Fact(DisplayName = "A successful fill hands the live buffer to the caller")]
    public void AllocateAndFill_Success_ReturnsTheLiveBuffer()
    {
        var before = SettledLiveCount();

        var buffer = WicDirectDecoder.AllocateAndFill(16, 8, PixelLayout.Bgr32, _ => { }, false);

        Assert.Equal((16, 8), (buffer.Width, buffer.Height));
        Assert.Equal(before + 1, NativePixelMemory.LiveCount);
        buffer.Dispose();
        Assert.Equal(before, NativePixelMemory.LiveCount);
    }

    [Fact(DisplayName = "An ArgumentNullException from our own code is not relabelled as damaged image data")]
    public void Decode_ArgumentNullException_PropagatesUnchanged()
    {
        var path = FixtureGenerator.GenerateGradientJpeg(_root.Combine("n.jpg"), 64, 48);
        var decoder = new WicDirectDecoder(new ThrowingCodec(() => new ArgumentNullException("pixels")));

        var error = Record.Exception(() => decoder.Decode(new DecodeRequest(path, 0)));

        Assert.IsType<ArgumentNullException>(error);
    }

    [Fact(DisplayName = "Any other ArgumentException (WIC E_INVALIDARG) stays a data fault, not a caller bug")]
    public void Decode_PlainArgumentException_IsInvalidData()
    {
        var path = FixtureGenerator.GenerateGradientJpeg(_root.Combine("a.jpg"), 64, 48);
        var decoder = new WicDirectDecoder(new ThrowingCodec(() => new ArgumentException("E_INVALIDARG")));

        var error = Record.Exception(() => decoder.Decode(new DecodeRequest(path, 0)));

        Assert.IsType<InvalidDataException>(error);
    }

    [Fact(DisplayName = "An out-of-memory from the allocator is neither relabelled as bad data nor retried")]
    public void Decode_OutOfMemory_Propagates()
    {
        var path = FixtureGenerator.GenerateGradientJpeg(_root.Combine("o.jpg"), 64, 48);
        var decoder = new WicDirectDecoder(new ThrowingCodec(() => Marshal.GetExceptionForHR(unchecked((int)0x8007000E), new IntPtr(-1))!));

        var error = Record.Exception(() => decoder.Decode(new DecodeRequest(path, 0)));

        Assert.IsType<OutOfMemoryException>(error);
    }

    [Theory(DisplayName = "An output beyond the int-based size limit is a clean admission error on any machine, never an OverflowException")]
    [InlineData(2147483648L)]
    [InlineData(4294967296L)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    public void EnsureOutputFits_BeyondIntRange_IsAdmissionError(long bufferLength)
    {
        var error = Record.Exception(() =>
            WicDirectDecoder.EnsureOutputFits(40000, 20000, bufferLength, () => (long.MaxValue / 2, 0)));

        Assert.IsType<DecoderMemoryAdmissionException>(error);
    }

    [Fact(DisplayName = "An output of exactly int.MaxValue bytes still fits a machine with room for it")]
    public void EnsureOutputFits_ExactlyIntMax_FitsWhenThereIsRoom()
    {
        var error = Record.Exception(() =>
            WicDirectDecoder.EnsureOutputFits(32768, 16384, int.MaxValue, () => (64L * 1024 * 1024 * 1024, 0)));

        Assert.Null(error);
    }

    [Theory(DisplayName = "TryRead never throws on a damaged APP1 segment (length field, truncation inside the Exif block)")]
    [InlineData("len-zero")]
    [InlineData("len-max")]
    [InlineData("len-short")]
    [InlineData("truncate-in-app1")]
    [InlineData("ifd1-offset")]
    public void TryRead_DamagedApp1Segment_DoesNotThrow(string kind)
    {
        var bytes = EmbeddedThumbnailJpegFixture.CreateWithThumbnail(mainSize: 96, thumbnailSize: 24);
        var marker = new byte[] { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };
        var at = bytes.AsSpan().IndexOf(marker);
        Assert.True(at > 2, "the fixture must carry an APP1 Exif block");
        switch (kind)
        {
            case "len-zero": bytes[at - 2] = 0; bytes[at - 1] = 0; break;
            case "len-max": bytes[at - 2] = 0xFF; bytes[at - 1] = 0xFF; break;
            case "len-short": bytes[at - 2] = 0; bytes[at - 1] = 8; break;
            case "truncate-in-app1": bytes = bytes[..(at + 40)]; break;
            default:
                // IFD0 entry count 0 then a next-IFD offset (the thumbnail IFD) pointing past the block.
                for (var i = 0; i < 4; i++) bytes[at + marker.Length + 10 + i] = 0xFF;
                break;
        }

        var path = _root.Combine("app1-" + kind + ".jpg");
        File.WriteAllBytes(path, bytes);

        var error = Record.Exception(() => EmbeddedThumbnailReader.TryRead(path));

        Assert.Null(error);
    }
    [Theory(DisplayName = "TryRead never throws on damaged EXIF metadata next to the thumbnail")]
    [InlineData(0, 0x5A)]   // byte-order mark II/MM -> garbage
    [InlineData(2, 0x00)]   // TIFF magic 42
    [InlineData(4, 0xFF)]   // IFD0 offset -> far outside the block
    [InlineData(5, 0xFF)]
    [InlineData(8, 0xFF)]   // IFD0 entry count
    [InlineData(9, 0xFF)]
    public void TryRead_DamagedExifHeader_DoesNotThrow(int offsetInTiff, byte value)
    {
        var bytes = EmbeddedThumbnailJpegFixture.CreateWithThumbnail(mainSize: 96, thumbnailSize: 24);
        var marker = new byte[] { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };
        var at = bytes.AsSpan().IndexOf(marker);
        Assert.True(at > 0, "the fixture must carry an APP1 Exif block");
        bytes[at + marker.Length + offsetInTiff] = value;
        var path = _root.Combine("damaged" + offsetInTiff + ".jpg");
        File.WriteAllBytes(path, bytes);

        var error = Record.Exception(() => EmbeddedThumbnailReader.TryRead(path));

        Assert.Null(error);
    }
}
