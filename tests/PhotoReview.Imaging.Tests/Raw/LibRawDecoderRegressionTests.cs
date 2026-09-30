using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

// Shares a collection with LibRawDecoderTests: the full-decode gate is process-wide, so its slot count is only stable when no other LibRaw test decodes concurrently.
[Collection(LibRawNativeDecodeGate.Name)]
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
        Assert.Equal(["configured", "opened", "unpacked", "processed"], pinCounts.Select(entry => entry.Stage));
        // libraw_open_buffer keeps a pointer into the buffer; it must stay pinned until the handle is closed.
        Assert.All(pinCounts, entry => Assert.Equal(1, entry.Pins));
        Assert.Equal(0, manager.ActivePins);
        Assert.Equal(1, manager.TotalPins);
    }

    [Fact]
    public void Decode_FullResolution_HoldsTheSingleFullDecodeSlotAndReleasesIt()
    {
        if (!File.Exists(SamplePath)) return;
        var slotsWhileDecoding = -1;
        var decoder = new LibRawDecoder(stage => { if (stage == "processed") slotsWhileDecoding = LibRawDecoder.FullDecodeSlotsAvailable; });

        _ = decoder.Decode(new DecodeRequest(SamplePath, DecodeBox.Unbounded));

        Assert.Equal(0, slotsWhileDecoding);
        Assert.Equal(1, LibRawDecoder.FullDecodeSlotsAvailable);
    }

    [Fact]
    public void Decode_OutOfMemoryDuringDecode_MapsToInvalidOperationAndReleasesSlot()
    {
        if (!File.Exists(SamplePath)) return;
#pragma warning disable CA2201 // Simulating the runtime's allocation failure is the point of this test.
        var decoder = new LibRawDecoder(stage => { if (stage == "processed") throw new OutOfMemoryException(); });
#pragma warning restore CA2201

        var error = Assert.Throws<InvalidOperationException>(() => decoder.Decode(new DecodeRequest(SamplePath, DecodeBox.Unbounded)));

        Assert.IsType<OutOfMemoryException>(error.InnerException);
        Assert.Equal(1, LibRawDecoder.FullDecodeSlotsAvailable);
    }

    [Fact]
    public void Decode_BoundedDownscale_UsesBoxAverageAndKeepsOriginalSize()
    {
        if (!File.Exists(SamplePath)) return;

        var image = new LibRawDecoder().Decode(new DecodeRequest(SamplePath, new DecodeBox(300, 300)));

        Assert.True(image.Downscaled);
        Assert.InRange(image.PixelWidth, 1, 300);
        Assert.True(image.OriginalWidth > 2 * image.PixelWidth);
    }

    [Fact]
    public void ReadJpegThumbnail_AndDecode_WithCancelledToken_ThrowOperationCanceledBeforeOpeningTheFile()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => LibRawDecoder.ReadJpegThumbnail(@"Z:\missing.orf", cts.Token));
        Assert.Throws<OperationCanceledException>(() =>
            new LibRawDecoder().Decode(new DecodeRequest(@"Z:\missing.cr2", DecodeBox.Unbounded), cts.Token));
        Assert.Equal(1, LibRawDecoder.FullDecodeSlotsAvailable);
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

public sealed class RgbBgraResamplerTests
{
    [Fact]
    public void Resize_OnePixelCheckerboardReducedThreefold_AveragesInsteadOfAliasing()
    {
        const int Source = 60, Target = 20;
        var rgb = new byte[Source * Source * 3];
        for (var y = 0; y < Source; y++)
            for (var x = 0; x < Source; x++)
                rgb.AsSpan((y * Source + x) * 3, 3).Fill((x + y) % 2 == 0 ? (byte)255 : (byte)0);
        var bgra = new byte[Target * Target * 4];

        RgbBgraResampler.Resize(rgb, Source, Source, bgra, Target, Target, CancellationToken.None);

        // Bilinear sampling lands exactly on one source pixel per target pixel here and yields pure 0/255 (aliasing);
        // a 3x3 box of a checkerboard is 4 or 5 white pixels of 9 -> 113 or 142.
        for (var i = 0; i < Target * Target; i++)
        {
            Assert.InRange(bgra[i * 4], 100, 155);
            Assert.InRange(bgra[i * 4 + 1], 100, 155);
            Assert.InRange(bgra[i * 4 + 2], 100, 155);
            Assert.Equal(255, bgra[i * 4 + 3]);
        }
    }

    [Fact]
    public void Resize_SolidColorBoxAverage_KeepsChannelOrderBgr()
    {
        const int Source = 40, Target = 8;
        var rgb = new byte[Source * Source * 3];
        for (var i = 0; i < Source * Source; i++) { rgb[i * 3] = 200; rgb[i * 3 + 1] = 100; rgb[i * 3 + 2] = 10; }
        var bgra = new byte[Target * Target * 4];

        RgbBgraResampler.Resize(rgb, Source, Source, bgra, Target, Target, CancellationToken.None);

        Assert.Equal([(byte)10, (byte)100, (byte)200, (byte)255], bgra[..4]);
        Assert.Equal([(byte)10, (byte)100, (byte)200, (byte)255], bgra[^4..]);
    }

    [Fact]
    public void Resize_ModerateReduction_UsesBilinear()
    {
        // 4 -> 3 (ratio 1.33): a horizontal ramp must be interpolated, not box-averaged over whole source pixels.
        byte[] rgb = [0, 0, 0, 90, 90, 90, 180, 180, 180, 255, 255, 255];
        var bgra = new byte[3 * 4];

        RgbBgraResampler.Resize(rgb, 4, 1, bgra, 3, 1, CancellationToken.None);

        Assert.Equal(15, bgra[0]); // sample at 1/6 of the way between source pixels 0 and 1 (a box average would give 0)
        Assert.Equal(242, bgra[8]);
        Assert.Equal(135, bgra[4]);
    }

    [Fact]
    public void Resize_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            RgbBgraResampler.Resize(new byte[60 * 60 * 3], 60, 60, new byte[20 * 20 * 4], 20, 20, cts.Token));
    }

    [Theory]
    [InlineData(65535, 65535, uint.MaxValue)]
    [InlineData(46341, 46341, uint.MaxValue)]
    [InlineData(1000, 1000, 100u)]
    [InlineData(0, 10, 1000u)]
    public void ValidateSourceLength_AbsurdOrInconsistentHeader_ThrowsInvalidDataNotOverflow(int width, int height, uint dataSize)
    {
        Assert.Throws<InvalidDataException>(() => RgbBgraResampler.ValidateSourceLength(width, height, dataSize));
    }

    [Fact]
    public void ValidateSourceLength_ConsistentHeader_ReturnsRgbLength()
    {
        Assert.Equal(6000 * 4000 * 3, RgbBgraResampler.ValidateSourceLength(6000, 4000, 6000L * 4000 * 3));
    }

    [Theory]
    [InlineData(65535, 65535)]
    [InlineData(30000, 20000)]
    public void ValidateTargetLength_ArrayTooLarge_ThrowsInvalidDataNotOverflow(int width, int height)
    {
        Assert.Throws<InvalidDataException>(() => RgbBgraResampler.ValidateTargetLength(width, height));
    }

    [Fact]
    public void ValidateTargetLength_NormalSize_ReturnsBgraLength()
    {
        Assert.Equal(300 * 200 * 4, RgbBgraResampler.ValidateTargetLength(300, 200));
    }
}

[CollectionDefinition(Name)]
public sealed class LibRawNativeDecodeGate
{
    public const string Name = "LibRaw native decode gate";
}
