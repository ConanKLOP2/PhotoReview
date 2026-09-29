using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

[Trait("Category", "Native")]
public sealed class LibRawDecoderBufferPinTests
{
    private static readonly string SamplePath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus/Canon - EOS 350D - RAW (3_2).CR2"));

    [Fact]
    public void Decode_WithRequestBytes_KeepsBufferPinnedThroughUnpackAndProcess()
    {
        if (!File.Exists(SamplePath)) return;
        using var manager = new PinTrackingMemoryManager(File.ReadAllBytes(SamplePath));
        var pinCounts = new List<(string Stage, int Pins)>();
        var decoder = new LibRawDecoder(stage => pinCounts.Add((stage, manager.ActivePins)));

        var image = decoder.Decode(new DecodeRequest(SamplePath, DecodeBox.Unbounded, bytes: manager.Memory));

        Assert.True(image.PixelWidth > 0);
        Assert.Equal(["opened", "unpacked", "processed"], pinCounts.Select(entry => entry.Stage));
        // libraw_open_buffer keeps a pointer into the buffer; it must stay pinned until the handle is closed.
        Assert.All(pinCounts, entry => Assert.Equal(1, entry.Pins));
        Assert.Equal(0, manager.ActivePins);
        Assert.Equal(1, manager.TotalPins);
    }

    private sealed unsafe class PinTrackingMemoryManager : MemoryManager<byte>
    {
        private readonly byte* _buffer;
        private readonly int _length;
        private int _activePins;
        private int _totalPins;

        internal PinTrackingMemoryManager(byte[] content)
        {
            _length = content.Length;
            _buffer = (byte*)NativeMemory.Alloc((nuint)_length);
            content.CopyTo(new Span<byte>(_buffer, _length));
        }

        internal int ActivePins => Volatile.Read(ref _activePins);
        internal int TotalPins => Volatile.Read(ref _totalPins);

        public override Span<byte> GetSpan() => new(_buffer, _length);

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            Interlocked.Increment(ref _activePins);
            Interlocked.Increment(ref _totalPins);
            return new MemoryHandle(_buffer + elementIndex, default, this);
        }

        public override void Unpin() => Interlocked.Decrement(ref _activePins);

        protected override void Dispose(bool disposing) => NativeMemory.Free(_buffer);
    }
}

public sealed class LibRawThumbnailTrimTests
{
    private static byte[] Jpeg() => SyntheticRawBuilder.CreateMinimalJpeg(64, 48);

    [Fact]
    public void TrimToEndOfImage_ZeroPaddedJpeg_TrimsToEoiAndStaysDecodable()
    {
        var jpeg = Jpeg();
        var padded = jpeg.Concat(new byte[37]).ToArray();

        var trimmed = LibRawDecoder.TrimToEndOfImage(padded);

        Assert.Equal(jpeg, trimmed);
        var decoded = new WpfBitmapImageDecoder().Decode(new DecodeRequest("thumb.jpg", DecodeBox.Unbounded, bytes: trimmed));
        Assert.Equal(64, decoded.OriginalWidth);
    }

    [Fact]
    public void TrimToEndOfImage_TrailingFfFillAfterEoi_TrimsToEoi()
    {
        var jpeg = Jpeg();
        var padded = jpeg.Concat(new byte[] { 0x00, 0xFF, 0xFF, 0x00 }).ToArray();

        Assert.Equal(jpeg, LibRawDecoder.TrimToEndOfImage(padded));
    }

    [Fact]
    public void TrimToEndOfImage_UnpaddedJpeg_ReturnsSameArray()
    {
        var jpeg = Jpeg();

        Assert.Same(jpeg, LibRawDecoder.TrimToEndOfImage(jpeg));
    }

    [Fact]
    public void TrimToEndOfImage_TruncatedJpegWithoutEoi_Throws()
    {
        var jpeg = Jpeg();
        var truncated = jpeg[..(jpeg.Length - 2)].Concat(new byte[16]).ToArray();

        Assert.Throws<InvalidDataException>(() => LibRawDecoder.TrimToEndOfImage(truncated));
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0x00, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xFF, 0xFF, 0xFF })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0xFF, 0xD9 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF })]
    public void TrimToEndOfImage_HostileOrIncompleteData_Throws(byte[] data)
    {
        Assert.Throws<InvalidDataException>(() => LibRawDecoder.TrimToEndOfImage(data));
    }
}
