using System.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Imaging.TurboJpeg.Native;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.Imaging.Tests.Robustness;
using PhotoReview.TestSupport;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// Mutation-testing gap closers for <see cref="TurboJpegDecoder"/> (Stryker survivors ~L67-L650): signature length boundary, the
/// native error detail carried by the messages, the memory/size guard thresholds, the DCT scale picked from the declared size, the
/// source-size limit, the header-area growth when the first read ends inside the SOS header, and the ICC/EXIF header scan.
/// </summary>
public sealed class TurboJpegGuardMutationTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private readonly TempRoot _root = new("turbo-guards");

    public void Dispose() => _root.Dispose();

    private static byte[] SmallJpeg(int width = 64, int height = 48)
    {
        using var stream = new MemoryStream();
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(FixtureGenerator.CreateGradientCheckerboard(width, height)));
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

    /// <summary>A JPEG whose SOF declares a data precision libjpeg-turbo rejects while parsing the header.</summary>
    private static byte[] WithBadPrecision(byte[] jpeg)
    {
        var patched = (byte[])jpeg.Clone();
        for (var i = 2; i + 9 < patched.Length; i++)
        {
            if (patched[i] != 0xFF || patched[i + 1] != 0xC0) continue;
            patched[i + 4] = 3;
            return patched;
        }

        throw new InvalidOperationException("no SOF0");
    }

    private static Exception Catch(Action act)
    {
        try { act(); }
        catch (Exception ex) { return ex; }
        throw new Xunit.Sdk.XunitException("expected an exception");
    }

    private static string InEnglish(Exception ex)
    {
        using var _ = TestLocalization.Use(TestLocalization.English);
        return UserFacingError.Describe(ex);
    }

    // ---- signature check ----

    [Fact]
    public void Decode_ExactlyTheThreeSignatureBytes_FailsAsABrokenHeaderNotAsANonJpeg()
    {
        var ex = Catch(() => new TurboJpegDecoder().Decode(new DecodeRequest("sig.jpg", DecodeBox.Unbounded, bytes: new byte[] { 0xFF, 0xD8, 0xFF })));

        Assert.IsType<InvalidDataException>(ex);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0x00 })]
    [InlineData(new byte[] { 0x00, 0xD8, 0xFF })]
    [InlineData(new byte[] { 0xFF, 0x00, 0xFF })]
    public void Decode_WithoutTheJpegSignature_IsNotSupported(byte[] bytes)
    {
        var ex = Catch(() => new TurboJpegDecoder().Decode(new DecodeRequest("sig.jpg", DecodeBox.Unbounded, bytes: bytes)));

        Assert.IsType<NotSupportedException>(ex);
    }

    [Fact]
    public void ReadInfo_FileOfExactlyTheThreeSignatureBytes_FailsAsABrokenHeaderNotAsANonJpeg()
    {
        var path = _root.File("sig3.jpg", 0xFF, 0xD8, 0xFF);

        Assert.IsType<InvalidDataException>(Catch(() => new TurboJpegDecoder().ReadInfo(path)));
    }

    [Fact]
    public void ReadInfo_FileOfTwoBytes_IsNotSupported()
    {
        var path = _root.File("sig2.jpg", 0xFF, 0xD8);

        Assert.IsType<NotSupportedException>(Catch(() => new TurboJpegDecoder().ReadInfo(path)));
    }

    [Fact]
    public void Decode_NonJpegBytesWithoutPath_NamesTheMemoryBufferInTheLogMessage()
    {
        var request = new DecodeRequest(null!, 0, Bytes: new byte[] { 1, 2, 3, 4 });

        var ex = Catch(() => new TurboJpegDecoder().Decode(request));

        Assert.Equal("File is not a valid JPEG: memory buffer", ex.Message);
    }

    // ---- native error detail in the messages ----

    [Fact]
    public void Decode_HeaderTheNativeParserRejects_CarriesTheNativeDetailInLogAndUserText()
    {
        var garbage = WithBadPrecision(SmallJpeg());

        var ex = Catch(() => new TurboJpegDecoder().Decode(new DecodeRequest("g.jpg", DecodeBox.Unbounded, bytes: garbage)));

        Assert.IsType<InvalidDataException>(ex);
        Assert.StartsWith("TurboJPEG failed to decompress header: ", ex.Message, StringComparison.Ordinal);
        var detail = ex.Message["TurboJPEG failed to decompress header: ".Length..];
        Assert.Equal("Unsupported JPEG data precision 3", detail);
        Assert.Contains(detail, InEnglish(ex), StringComparison.Ordinal);
        Assert.DoesNotContain("no detail", InEnglish(ex), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_ScanTheNativeDecoderRejects_CarriesTheNativeDetailInLogAndUserText()
    {
        var jpeg = SmallJpeg();
        var cut = jpeg.AsSpan(0, jpeg.Length * 6 / 10).ToArray();

        var ex = Catch(() => new TurboJpegDecoder().Decode(new DecodeRequest("cut.jpg", DecodeBox.Unbounded, bytes: cut)));

        Assert.IsType<InvalidDataException>(ex);
        Assert.StartsWith("TurboJPEG decompression failed: ", ex.Message, StringComparison.Ordinal);
        var detail = ex.Message["TurboJPEG decompression failed: ".Length..];
        Assert.NotEqual("Decompression error", detail);
        Assert.Contains(detail, InEnglish(ex), StringComparison.Ordinal);
        Assert.DoesNotContain("no detail", InEnglish(ex), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadInfo_HeaderTheNativeParserRejects_CarriesTheNativeDetailInLogAndUserText()
    {
        var path = _root.File("g.jpg", WithBadPrecision(SmallJpeg()));

        var ex = Catch(() => new TurboJpegDecoder().ReadInfo(path));

        Assert.IsType<InvalidDataException>(ex);
        Assert.StartsWith("TurboJPEG failed to read image info: ", ex.Message, StringComparison.Ordinal);
        var detail = ex.Message["TurboJPEG failed to read image info: ".Length..];
        Assert.Equal("Unsupported JPEG data precision 3", detail);
        Assert.Contains(detail, InEnglish(ex), StringComparison.Ordinal);
        Assert.DoesNotContain("no detail", InEnglish(ex), StringComparison.OrdinalIgnoreCase);
    }

    private static readonly byte[] NoSof = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x11, 0x22, 0xFF, 0xC4, 0x00, 0x02, 0xFF, 0xD9];

    [Fact]
    public void Decode_JpegWithoutAFrameHeader_ReportsInvalidDimensions()
    {
        var ex = Catch(() => new TurboJpegDecoder().Decode(new DecodeRequest("nosof.jpg", DecodeBox.Unbounded, bytes: NoSof)));

        Assert.IsType<InvalidDataException>(ex);
        Assert.StartsWith("Invalid image dimensions reported by TurboJPEG: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadInfo_JpegWithoutAFrameHeader_FailsWithTheGenericHeaderText()
    {
        var path = _root.File("nosof.jpg", NoSof);

        var ex = Catch(() => new TurboJpegDecoder().ReadInfo(path));

        Assert.IsType<InvalidDataException>(ex);
        Assert.Equal("TurboJPEG failed to read image info: Header parse error", ex.Message);
    }

    [Fact]
    public void Decode_SofDeclaringAZeroWidth_IsRejectedAsAnInvalidDataFault()
    {
        var zero = WithClaimedSize(SmallJpeg(), 0, 48);

        var ex = Catch(() => new TurboJpegDecoder().Decode(new DecodeRequest("zero.jpg", DecodeBox.Unbounded, bytes: zero)));

        Assert.IsType<InvalidDataException>(ex);
    }

    // ---- output-size guards ----

    [Fact]
    public void Decode_OutputBufferOfExactlyTheGuardThreshold_ConsultsTheMemoryBudget()
    {
        // 8192 x 4096 x 4 = 128 MiB = GuardThresholdBytes: the guard applies at the threshold itself.
        var bomb = WithClaimedSize(SmallJpeg(), 8192, 4096);
        var decoder = new TurboJpegDecoder { MemoryInfo = () => (64L * 1024 * 1024, 0) };

        var ex = Catch(() => decoder.Decode(new DecodeRequest("t.jpg", DecodeBox.Unbounded, bytes: bomb)));

        Assert.IsType<InvalidDataException>(ex);
        Assert.Contains("too large for the available memory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_OutputBufferJustBelowTheGuardThreshold_NeverConsultsTheMemoryBudget()
    {
        var bomb = WithClaimedSize(SmallJpeg(), 8192, 4095);
        var consulted = 0;
        var decoder = new TurboJpegDecoder { MemoryInfo = () => { consulted++; return (64L * 1024 * 1024, 0); } };

        var ex = Catch(() => decoder.Decode(new DecodeRequest("t.jpg", DecodeBox.Unbounded, bytes: bomb)));

        Assert.Equal(0, consulted);
        Assert.DoesNotContain("too large for the available memory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_DownscaleOfAHugeDeclaredSize_IsGuardedByTheScaledOutputNotTheOriginal()
    {
        // 20000 x 20000 declared, box 2000 x 2000: the 1/8 DCT scale gives 2500 x 2500 (25 MB), which fits a 1 GB machine
        // although the full-size buffer (1.6 GB) would not. The stub scan data then fails later, in the decompress.
        var bomb = WithClaimedSize(SmallJpeg(), 20000, 20000);
        var decoder = new TurboJpegDecoder { MemoryInfo = () => (1 * Gb, 0) };

        var ex = Catch(() => decoder.Decode(new DecodeRequest("t.jpg", new DecodeBox(2000, 2000), bytes: bomb)));

        Assert.IsType<InvalidDataException>(ex);
        Assert.DoesNotContain("too large", ex.Message, StringComparison.Ordinal);
        Assert.StartsWith("TurboJPEG decompression failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_ImpossibleOutputSize_IsRefusedWithoutAskingTheMemoryBudget()
    {
        // 32768 x 16384 x 4 = 2^31 bytes: beyond what one int-sized buffer can hold, whatever RAM there is.
        var bomb = WithClaimedSize(SmallJpeg(), 32768, 16384);
        var decoder = new TurboJpegDecoder { MemoryInfo = () => throw new InvalidOperationException("must not be consulted") };

        var ex = Catch(() => decoder.Decode(new DecodeRequest("t.jpg", DecodeBox.Unbounded, bytes: bomb)));

        Assert.IsType<InvalidDataException>(ex);
        Assert.Contains("output dimensions are too large:", ex.Message, StringComparison.Ordinal);
    }

    // ---- source-size guards ----

    [Fact]
    public void Decode_SourceFileOfExactlyTheGuardThreshold_ConsultsTheMemoryBudgetBeforeReading()
    {
        var path = Path.Combine(_root.Path, "exact.jpg");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            fs.SetLength(MemoryHeadroom.GuardThresholdBytes);
        var probed = 0;
        var decoder = new TurboJpegDecoder { MemoryInfo = () => { probed++; return (MemoryHeadroom.GuardThresholdBytes / 2, 0); } };

        var ex = Catch(() => decoder.Decode(new DecodeRequest(path, TargetWidth: 0)));

        Assert.Equal(1, probed);
        Assert.IsType<NotSupportedException>(ex);
        Assert.Contains("too large for the available memory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_SourceFileJustBelowTheGuardThreshold_NeverConsultsTheMemoryBudget()
    {
        var path = Path.Combine(_root.Path, "below.jpg");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            fs.SetLength(MemoryHeadroom.GuardThresholdBytes - 1);
        var probed = 0;
        var decoder = new TurboJpegDecoder { MemoryInfo = () => { probed++; return (MemoryHeadroom.GuardThresholdBytes / 2, 0); } };

        var ex = Catch(() => decoder.Decode(new DecodeRequest(path, TargetWidth: 0)));

        Assert.Equal(0, probed);
        Assert.IsType<NotSupportedException>(ex); // the zero-filled file is simply not a JPEG
        Assert.DoesNotContain("too large for the available memory", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Reports a length without holding the data (the reader must not trust it blindly).</summary>
    private sealed class LengthOnlyStream(long reportedLength) : Stream
    {
        public int ReadCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => reportedLength;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { ReadCalls++; return 0; }
        public override int Read(Span<byte> buffer) { ReadCalls++; return 0; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void ReadAllBytes_LengthOneAboveTheLimit_IsRefusedBeforeReading()
    {
        using var stream = new LengthOnlyStream(TurboJpegDecoder.MaxSourceBytes + 1);

        Assert.IsType<NotSupportedException>(Catch(() => TurboJpegDecoder.ReadAllBytes(stream)));
        Assert.Equal(0, stream.ReadCalls);
    }

    [Fact]
    public void ReadAllBytes_LengthExactlyTheLimit_PassesTheLimitCheck()
    {
        using var stream = new LengthOnlyStream(TurboJpegDecoder.MaxSourceBytes);

        // The limit itself is allowed: the buffer is allocated and then trimmed to the (empty) data. A machine that cannot
        // commit ~2 GB right now refuses with OutOfMemoryException, which also proves the size check did not fire.
        try
        {
            var bytes = TurboJpegDecoder.ReadAllBytes(stream);
            Assert.Empty(bytes);
            Assert.Equal(1, stream.ReadCalls);
        }
        catch (OutOfMemoryException)
        {
            Assert.Equal(0, stream.ReadCalls);
        }
    }

    // ---- header area growth (ReadInfo) ----

    /// <summary>A valid JPEG whose SOS marker starts <paramref name="bytesOfSosInFirstChunk"/> bytes before the end of the 64 KiB first read.</summary>
    private static byte[] JpegWithSosCutByTheFirstRead(int bytesOfSosInFirstChunk)
    {
        var jpeg = SmallJpeg();
        var sos = -1;
        for (var i = 2; i + 3 < jpeg.Length; )
        {
            if (jpeg[i] != 0xFF) throw new InvalidOperationException("marker walk");
            if (jpeg[i + 1] == 0xDA) { sos = i; break; }
            i += 2 + ((jpeg[i + 2] << 8) | jpeg[i + 3]);
        }

        Assert.True(sos > 0);
        // Pad with one COM segment (payload <= 65533) so that SOS starts at 65536 - bytesOfSosInFirstChunk.
        var comTotal = 65536 - bytesOfSosInFirstChunk - sos;
        Assert.InRange(comTotal, 5, 65537);
        var com = JpegBytes.Segment(0xFE, new byte[comTotal - 4]);
        return [.. jpeg.AsSpan(0, 2).ToArray(), .. com, .. jpeg.AsSpan(2).ToArray()];
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void ReadInfo_FirstReadEndsInsideTheSosHeader_ReadsOnAndReturnsTheSize(int bytesOfSosInFirstChunk)
    {
        var path = _root.File("sos-cut-" + bytesOfSosInFirstChunk + ".jpg", JpegWithSosCutByTheFirstRead(bytesOfSosInFirstChunk));

        var info = new TurboJpegDecoder().ReadInfo(path);

        Assert.Equal((64, 48), (info.Width, info.Height));
    }

    // ---- ICC / EXIF header scan ----

    private static byte[] IccApp2() => JpegBytes.Segment(0xE2, [.. JpegBytes.IccTag, 1, 1, 9, 9]);

    private static byte[] ExifApp1(int orientation) => JpegBytes.ExifApp1(JpegBytes.OrientationTiff(little: true, orientation));

    private static byte[] Header(params byte[][] segments) => [0xFF, 0xD8, .. segments.SelectMany(s => s), 0xFF, 0xDA, 0x00, 0x02];

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ScanHeader_ReportsOnlyWhatWasAskedFor(bool wantIcc, bool wantExif)
    {
        var jpeg = Header(IccApp2(), ExifApp1(6));

        TurboJpegDecoder.ScanHeader(jpeg, wantIcc, wantExif, out var hasIcc, out var exifTiff);

        Assert.Equal(wantIcc, hasIcc);
        Assert.Equal(wantExif, !exifTiff.IsEmpty);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ScanHeader_ExifBeforeIcc_ReportsOnlyWhatWasAskedFor(bool wantIcc, bool wantExif)
    {
        var jpeg = Header(ExifApp1(3), IccApp2());

        TurboJpegDecoder.ScanHeader(jpeg, wantIcc, wantExif, out var hasIcc, out var exifTiff);

        Assert.Equal(wantIcc, hasIcc);
        Assert.Equal(wantExif, !exifTiff.IsEmpty);
    }

    [Fact]
    public void ScanHeader_SecondExifSegment_IsIgnoredEvenWhenTheFirstOneHasNoTiffPart()
    {
        var emptyExif = JpegBytes.Segment(0xE1, [.. JpegBytes.ExifTag]);
        var jpeg = Header(emptyExif, ExifApp1(8));

        TurboJpegDecoder.ScanHeader(jpeg, wantIcc: false, wantExif: true, out _, out var exifTiff);

        Assert.True(exifTiff.IsEmpty);
        Assert.Equal(1, TurboJpegDecoder.ReadExifOrientation(jpeg));
    }

    [Fact]
    public void ScanHeader_FirstExifSegmentWins()
    {
        var jpeg = Header(ExifApp1(6), ExifApp1(8));

        Assert.Equal(6, TurboJpegDecoder.ReadExifOrientation(jpeg));
    }

    [Fact]
    public void ScanHeader_IccThenExif_FindsBothEvenWhenTheIccComesFirst()
    {
        var jpeg = Header(IccApp2(), ExifApp1(5));

        TurboJpegDecoder.ScanHeader(jpeg, wantIcc: true, wantExif: true, out var hasIcc, out var exifTiff);

        Assert.True(hasIcc);
        Assert.False(exifTiff.IsEmpty);
    }

    // ---- scaling factor value semantics ----

    [Theory]
    [InlineData(1, 2, 1, 2, true)]
    [InlineData(1, 2, 1, 4, false)]
    [InlineData(1, 2, 3, 2, false)]
    [InlineData(1, 2, 3, 4, false)]
    public void TjScalingFactor_Equality_NeedsBothNumeratorAndDenominator(int n1, int d1, int n2, int d2, bool equal)
    {
        var a = new TjScalingFactor(n1, d1);
        var b = new TjScalingFactor(n2, d2);

        Assert.Equal(equal, a.Equals(b));
        Assert.Equal(equal, a == b);
        Assert.Equal(!equal, a != b);
        Assert.Equal(equal, a.Equals((object)b));
        if (equal) Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // ---- unmanaged handle lifetime ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SafeTurboJpegHandle_InvalidHandleValue_IsInvalidAndDisposesWithoutCallingIntoTheNativeLibrary(long value)
    {
        var handle = new SafeTurboJpegHandle(new IntPtr(value), ownsHandle: true);

        Assert.True(handle.IsInvalid);
        Assert.Null(Record.Exception(handle.Dispose));
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void SafeTurboJpegHandle_RealHandle_IsValidAndClosesOnDispose()
    {
        var handle = TurboJpegNative.CreateDecompressor();

        Assert.False(handle.IsInvalid);
        handle.Dispose();
        handle.Dispose();
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void TurboJpegAvailability_WithTheNativeDllPresent_ProbesSuccessfully()
    {
        Assert.True(TurboJpegAvailability.Probe(out var reason), reason);
        Assert.Null(reason);
    }
}