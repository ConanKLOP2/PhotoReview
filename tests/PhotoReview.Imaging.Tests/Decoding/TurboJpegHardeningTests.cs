using System.IO;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.Imaging.Tests.Robustness;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>RV-I05 (source read) and RV-I06 (output-size and scan-limit guards) of the TurboJpeg decoder.</summary>
[Trait("Category", "HotPath")]
public sealed class TurboJpegHardeningTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private readonly TempRoot _root = new("TurboHardening");

    public void Dispose() => _root.Dispose();

    private static byte[] SmallJpeg()
    {
        using var stream = new MemoryStream();
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(FixtureGenerator.CreateGradientCheckerboard(64, 48)));
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] WithClaimedSize(byte[] jpeg, int width, int height)
    {
        var patched = (byte[])jpeg.Clone();
        for (var i = 2; i + 9 < patched.Length; i++)
        {
            if (patched[i] != 0xFF || (patched[i + 1] != 0xC0 && patched[i + 1] != 0xC2)) continue;
            patched[i + 5] = (byte)(height >> 8); patched[i + 6] = (byte)height;
            patched[i + 7] = (byte)(width >> 8); patched[i + 8] = (byte)width;
            return patched;
        }

        throw new InvalidOperationException("no SOF");
    }

    // ---- RV-I05 ----------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "A zero-byte .jpg fails with NotSupportedException and hands its (empty) source bytes to the fallback")]
    public void ZeroByteFile_NotSupported_CarriesSourceBytes()
    {
        var path = _root.File("empty.jpg");

        var ex = Assert.Throws<NotSupportedException>(() => new TurboJpegDecoder().Decode(new DecodeRequest(path, DecodeBox.Unbounded)));

        Assert.True(DecodeFailureSourceBytes.TryGet(ex, out var bytes));
        Assert.Equal(0, bytes.Length);
    }

    [Fact(DisplayName = "A JPEG cut mid-scan fails with InvalidDataException carrying all the bytes that were read")]
    public void TruncatedMidScan_InvalidData_CarriesSourceBytes()
    {
        var jpeg = SmallJpeg();
        var cut = jpeg.AsSpan(0, jpeg.Length * 6 / 10).ToArray();
        var path = _root.File("cut.jpg", cut);

        var ex = Assert.Throws<InvalidDataException>(() => new TurboJpegDecoder().Decode(new DecodeRequest(path, DecodeBox.Unbounded)));

        Assert.True(DecodeFailureSourceBytes.TryGet(ex, out var bytes));
        Assert.Equal(cut.Length, bytes.Length);
    }

    /// <summary>A stream whose reported Length does not match what it yields.</summary>
    private sealed class MisreportingStream(byte[] content, long reportedLength) : Stream
    {
        private readonly MemoryStream _inner = new(content);
        public int ReadCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => reportedLength;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { ReadCalls++; return _inner.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { ReadCalls++; return _inner.Read(buffer); }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    [Fact(DisplayName = "ReadAllBytes returns the short buffer (no EndOfStreamException) when the stream yields fewer bytes than its Length")]
    public void ReadAllBytes_StreamShorterThanLength_ReturnsWhatExists()
    {
        var content = new byte[60];
        content.AsSpan().Fill(7);
        using var stream = new MisreportingStream(content, reportedLength: 100);

        var bytes = TurboJpegDecoder.ReadAllBytes(stream);

        Assert.Equal(content, bytes);
    }

    [Fact(DisplayName = "ReadAllBytes reads a normal stream in one read")]
    public void ReadAllBytes_ExactLength_OneRead()
    {
        var content = new byte[5000];
        new Random(3).NextBytes(content);
        using var stream = new MisreportingStream(content, reportedLength: content.Length);

        var bytes = TurboJpegDecoder.ReadAllBytes(stream);

        Assert.Equal(content, bytes);
        Assert.Equal(1, stream.ReadCalls);
    }

    [Fact(DisplayName = "ReadAllBytes refuses a source over int.MaxValue - 64 with NotSupportedException (the WIC fallback streams it), before reading")]
    public void ReadAllBytes_TooLarge_NotSupported()
    {
        using var stream = new MisreportingStream([1, 2, 3], reportedLength: int.MaxValue);

        Assert.Throws<NotSupportedException>(() => TurboJpegDecoder.ReadAllBytes(stream));
        Assert.Equal(0, stream.ReadCalls);
    }

    // ---- RV-I06 ----------------------------------------------------------------------------------------------------

    private static string Message(Action act)
    {
        try { act(); return string.Empty; }
        catch (InvalidDataException ex) { return ex.Message; }
    }

    [Fact(DisplayName = "A tiny JPEG declaring 20000x20000 is refused up front on a machine without the RAM for it (controlled error, nothing allocated)")]
    public void HugeDeclaredSize_OnSmallMachine_IsRefusedBeforeAllocation()
    {
        var bomb = WithClaimedSize(SmallJpeg(), 20000, 20000);
        var decoder = new TurboJpegDecoder { MemoryInfo = () => (1 * Gb, 0) };

        var message = Message(() => decoder.Decode(new DecodeRequest("bomb.jpg", DecodeBox.Unbounded, bytes: bomb)));

        Assert.Contains("too large for the available memory", message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A 12000x9000 original (108 MP) is NOT refused by the output guard on a 16 GB machine")]
    public void LegitimateLargeOriginal_OnNormalMachine_PassesTheGuard()
    {
        var big = WithClaimedSize(SmallJpeg(), 12000, 9000);
        var decoder = new TurboJpegDecoder { MemoryInfo = () => (16 * Gb, 4 * Gb) };

        // The pixel data is a 64x48 stub, so the decode itself fails later - but not at the memory guard.
        var message = Message(() => decoder.Decode(new DecodeRequest("big.jpg", DecodeBox.Unbounded, bytes: big)));

        Assert.DoesNotContain("too large for the available memory", message, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "OutputHasHeadroom scales with the machine: 12000x9000 BGRX fits 8 GB free, 20000x20000 does not fit 2 GB free")]
    [InlineData(12000L * 9000 * 4, 16L * Gb, 8L * Gb, true)]
    [InlineData(16000L * 12000 * 4, 16L * Gb, 4L * Gb, true)]
    [InlineData(20000L * 20000 * 4, 16L * Gb, 14L * Gb, false)]
    [InlineData(12000L * 9000 * 4, 0L, 0L, true)] // unknown total never refuses
    public void OutputHasHeadroom_FollowsTheRamBudget(long bufferLength, long total, long load, bool expected) =>
        Assert.Equal(expected, TurboJpegDecoder.OutputHasHeadroom(bufferLength, total, load));

    /// <summary>
    /// 8x8 single-component progressive JPEG: one DC-first scan, one AC-first scan per coefficient 1..63, and
    /// <paramref name="refineLevels"/> successive-approximation refinement scans for each of them (all blocks are zero, so
    /// every scan is one Huffman bit): 64 scans at 0 levels, 512 at 7.
    /// </summary>
    private static byte[] ProgressiveGray(int refineLevels)
    {
        var output = new List<byte> { 0xFF, 0xD8 };
        output.AddRange(JpegBytes.Segment(0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]));
        output.AddRange(JpegBytes.Segment(0xC2, [8, 0, 8, 0, 8, 1, 1, 0x11, 0]));
        byte[] counts = [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        output.AddRange(JpegBytes.Segment(0xC4, [0x00, .. counts, 0x00]));
        output.AddRange(JpegBytes.Segment(0xC4, [0x10, .. counts, 0x00]));

        void Scan(int ss, int se, int ah, int al)
        {
            output.AddRange(JpegBytes.Segment(0xDA, [1, 1, 0x00, (byte)ss, (byte)se, (byte)((ah << 4) | al)]));
            output.Add(0x7F); // code "0" (DC diff 0 / EOB / refinement bit 0), padded with 1 bits
        }

        void Coefficient(int k)
        {
            Scan(k, k, 0, refineLevels);
            for (var ah = refineLevels; ah >= 1; ah--) Scan(k, k, ah, ah - 1);
        }

        Coefficient(0);
        for (var k = 1; k <= 63; k++) Coefficient(k);
        output.AddRange([0xFF, 0xD9]);
        return [.. output];
    }

    [Fact(DisplayName = "Control: a valid 64-scan progressive JPEG decodes")]
    public void ProgressiveWithFewScans_Decodes()
    {
        var image = new TurboJpegDecoder().Decode(new DecodeRequest("p.jpg", DecodeBox.Unbounded, bytes: ProgressiveGray(0)));

        Assert.Equal((8, 8), (image.PixelWidth, image.PixelHeight));
    }

    [Fact(DisplayName = "A progressive JPEG with 512 scans (> 500) is a controlled InvalidDataException, not a long decode")]
    public void ProgressiveWithTooManyScans_IsRefused() =>
        Assert.Throws<InvalidDataException>(() =>
            new TurboJpegDecoder().Decode(new DecodeRequest("p512.jpg", DecodeBox.Unbounded, bytes: ProgressiveGray(7))));
}