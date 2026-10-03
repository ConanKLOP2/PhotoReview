using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>Pure managed LibRaw interop logic: needs neither libraw.dll nor the corpus, so it runs in the default CI category.</summary>
public sealed class LibRawManagedLogicTests
{
    [Theory]
    [InlineData("0.22.2", true)]
    [InlineData("0.22.2-Release", true)]
    [InlineData("0.22.1", false)]
    [InlineData("0.22.3", false)]
    [InlineData("0.22.9-Release", false)]
    [InlineData("0.22.0", false)]
    [InlineData("0.22", false)]
    [InlineData("0.23.2", false)]
    [InlineData("1.22.2", false)]
    [InlineData("garbage", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CheckExactVersion_OnlyTheTestedPatchReleasePasses(string? version, bool accepted)
    {
        Assert.Equal(accepted, LibRawAvailability.CheckExactVersion(version));
    }

    [Theory]
    [InlineData(-100007)]
    [InlineData(12)]
    public void CreateThumbnailFailure_InsufficientMemory_IsResourceErrorNotCorruptFile(int code)
    {
        Assert.IsType<InvalidOperationException>(LibRawDecoder.CreateThumbnailFailure(code));
    }

    [Fact]
    public void CreateThumbnailFailure_DataError_StaysInvalidData()
    {
        Assert.IsType<System.IO.InvalidDataException>(LibRawDecoder.CreateThumbnailFailure(-100008));
    }

    [Fact]
    public void Resize_MonochromeSameSize_ExpandsGrayToOpaqueBgra()
    {
        byte[] gray = [0, 90, 180, 255];
        var bgra = new byte[gray.Length * 4];

        RgbBgraResampler.Resize(gray, 2, 2, bgra, 2, 2, CancellationToken.None, channels: 1);

        Assert.Equal([(byte)0, 0, 0, 255, 90, 90, 90, 255, 180, 180, 180, 255, 255, 255, 255, 255], bgra);
    }

    [Fact]
    public void Resize_MonochromeBoxAverage_AveragesEachTargetPixel()
    {
        // 6x2 gray reduced to 2x1 (ratio 3 > 2): left 3x2 block averages (0+30)*3/6 = 15, right block (100+200)*3/6 = 150.
        byte[] gray = [0, 0, 0, 100, 100, 100, 30, 30, 30, 200, 200, 200];
        var bgra = new byte[2 * 4];

        RgbBgraResampler.Resize(gray, 6, 2, bgra, 2, 1, CancellationToken.None, channels: 1);

        Assert.Equal([(byte)15, 15, 15, 255, 150, 150, 150, 255], bgra);
    }

    [Fact]
    public void Resize_MonochromeBilinear_InterpolatesLikeTheRgbPath()
    {
        // Same ramp as the 3-channel Resize_ModerateReduction_UsesBilinear test, one byte per pixel.
        byte[] gray = [0, 90, 180, 255];
        var bgra = new byte[3 * 4];

        RgbBgraResampler.Resize(gray, 4, 1, bgra, 3, 1, CancellationToken.None, channels: 1);

        Assert.Equal([(byte)15, 15, 15, 255, 135, 135, 135, 255, 242, 242, 242, 255], bgra);
    }

    [Fact]
    public void Resize_MonochromeAndRgbWithEqualChannels_ProduceIdenticalOutput()
    {
        const int Source = 30, Target = 7;
        var gray = new byte[Source * Source];
        for (var i = 0; i < gray.Length; i++) gray[i] = (byte)(i * 37 % 251);
        var rgb = new byte[gray.Length * 3];
        for (var i = 0; i < gray.Length; i++) rgb.AsSpan(i * 3, 3).Fill(gray[i]);
        var fromGray = new byte[Target * Target * 4];
        var fromRgb = new byte[Target * Target * 4];

        RgbBgraResampler.Resize(gray, Source, Source, fromGray, Target, Target, CancellationToken.None, channels: 1);
        RgbBgraResampler.Resize(rgb, Source, Source, fromRgb, Target, Target, CancellationToken.None);

        Assert.Equal(fromRgb, fromGray);
    }

    [Fact]
    public void ValidateSourceLength_Monochrome_UsesOneBytePerPixel()
    {
        Assert.Equal(6000 * 4000, RgbBgraResampler.ValidateSourceLength(6000, 4000, 6000L * 4000, channels: 1));
        Assert.Throws<System.IO.InvalidDataException>(() => RgbBgraResampler.ValidateSourceLength(6000, 4000, 6000L * 4000 - 1, channels: 1));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    public void IsSupportedChannelCount_OnlyGrayAndRgb(int channels, bool supported)
    {
        Assert.Equal(supported, RgbBgraResampler.IsSupportedChannelCount(channels));
    }

    [Fact]
    public void ValidateSourceLength_UnsupportedChannelCount_ThrowsInvalidData()
    {
        Assert.Throws<System.IO.InvalidDataException>(() => RgbBgraResampler.ValidateSourceLength(10, 10, 1000, channels: 4));
    }

    [Theory]
    [InlineData(-100007)] // LIBRAW_UNSUFFICIENT_MEMORY
    [InlineData(12)]      // ENOMEM returned as a positive errno
    public void CreateFailure_InsufficientMemory_IsInvalidOperationLikeManagedOutOfMemory(int code)
    {
        var ex = LibRawDecoder.CreateFailure(code, "unpack RAW data");

        Assert.IsType<InvalidOperationException>(ex);
        Assert.IsNotType<InvalidDataException>(ex);
    }

    [Theory]
    [InlineData(-100008)] // LIBRAW_DATA_ERROR
    [InlineData(-100009)] // LIBRAW_IO_ERROR
    [InlineData(-2)]      // LIBRAW_FILE_UNSUPPORTED
    public void CreateFailure_OtherCodes_StayInvalidData(int code)
    {
        Assert.IsType<InvalidDataException>(LibRawDecoder.CreateFailure(code, "unpack RAW data"));
    }

    [Theory]
    [InlineData("0.22.2", true)]
    [InlineData("0.22.0", true)]
    [InlineData("0.22.9-Release", true)]
    [InlineData("0.23.0", false)]
    [InlineData("0.21.4", false)]
    [InlineData("1.22.0", false)]
    [InlineData("garbage", false)]
    [InlineData("", false)]
    public void CheckVersion_OnlyThePinnedMajorMinorPasses(string version, bool accepted)
    {
        var problem = LibRawAvailability.CheckVersion(version, "libraw.dll");

        Assert.Equal(accepted, problem is null);
        if (!accepted) Assert.Contains("libraw.dll", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadJpegThumbnail_AndDecode_WithCancelledToken_ThrowOperationCanceledBeforeOpeningTheFile()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => LibRawDecoder.ReadJpegThumbnail(@"Z:\missing.orf", cts.Token));
        Assert.Throws<OperationCanceledException>(() =>
            new LibRawDecoder().Decode(new DecodeRequest(@"Z:\missing.cr2", DecodeBox.Unbounded), cts.Token));
    }

    // ---- white-balance layout proof over a fake memory block (the setters write where a LibRaw with the given layout would) ----

    private const int BlockSize = 6000;

    private static readonly LibRawNativeMethods.WhiteBalanceLayout Layout = LibRawNativeMethods.PinnedWhiteBalanceLayout;

    private sealed unsafe class FakeLibRaw : IDisposable
    {
        private readonly LibRawNativeMethods.WhiteBalanceLayout _real;

        internal FakeLibRaw(LibRawNativeMethods.WhiteBalanceLayout real)
        {
            _real = real;
            Pointer = (IntPtr)NativeMemory.AllocZeroed(BlockSize);
        }

        internal IntPtr Pointer { get; }
        internal List<string> Calls { get; } = [];
        internal int Flag => Marshal.ReadInt32(Pointer, Layout.UseCameraWb);

        internal (int Color, int Bps, int Bright, int Flag) Snapshot() =>
            (Marshal.ReadInt32(Pointer, Layout.OutputColor), Marshal.ReadInt32(Pointer, Layout.OutputBps),
                Marshal.ReadInt32(Pointer, Layout.NoAutoBright), Flag);

        internal bool TrySet(Func<bool> versionPinned, LibRawNativeMethods.WhiteBalanceLayout assumed) =>
            LibRawNativeMethods.TrySetUseCameraWb(Pointer,
                value => { Calls.Add("color"); Marshal.WriteInt32(Pointer, _real.OutputColor, value); },
                value => { Calls.Add("bps"); Marshal.WriteInt32(Pointer, _real.OutputBps, value); },
                value => { Calls.Add("bright"); Marshal.WriteInt32(Pointer, _real.NoAutoBright, value); },
                1, 8, 1, versionPinned, assumed);

        public void Dispose() => NativeMemory.Free((void*)Pointer);
    }

    [Fact]
    public void TrySetUseCameraWb_LayoutMatches_SetsFlagAndLeavesTheRealOutputSettings()
    {
        using var fake = new FakeLibRaw(Layout);

        Assert.True(fake.TrySet(() => true, Layout));

        Assert.Equal((1, 8, 1, 1), fake.Snapshot()); // the sentinels are gone, the real settings and the flag are in
    }

    [Theory]
    [InlineData(4, 0, 0)]    // output_color read from the wrong place
    [InlineData(0, -4, 0)]   // output_bps read from the wrong place
    [InlineData(0, 0, 8)]    // no_auto_bright read from the wrong place
    [InlineData(-8, -8, -8)] // a whole shifted block
    public void TrySetUseCameraWb_WrongOffset_FailsClosedWithoutWritingTheFlag(int colorShift, int bpsShift, int brightShift)
    {
        using var fake = new FakeLibRaw(Layout);
        var wrong = new LibRawNativeMethods.WhiteBalanceLayout(Layout.OutputColor + colorShift, Layout.OutputBps + bpsShift,
            Layout.NoAutoBright + brightShift, Layout.UseCameraWb);

        var applied = fake.TrySet(() => true, wrong);

        Assert.False(applied);
        Assert.Equal(0, fake.Flag);
        // The output settings still reach LibRaw through the supported setters (daylight WB, sRGB, 8 bit).
        Assert.Equal((1, 8, 1, 0), fake.Snapshot());
    }

    [Fact]
    public void TrySetUseCameraWb_VersionNotPinned_TouchesNoMemoryAndCallsNoSetter()
    {
        using var fake = new FakeLibRaw(Layout);
        var gateCalls = 0;

        var applied = fake.TrySet(() => { gateCalls++; return false; }, Layout);

        Assert.False(applied);
        Assert.Equal(1, gateCalls);
        Assert.Empty(fake.Calls); // not even the sentinel/setter traffic: the gate runs before any write
        Assert.Equal((0, 0, 0, 0), fake.Snapshot());
    }

    private static string Long(string root) => root + new string('a', LibRawDecoder.LongPathThreshold) + @"\photo.cr2";

    [Fact]
    public void ToExtendedLengthPath_LongDrivePath_GetsTheExtendedPrefix()
    {
        var path = Long(@"C:\photos\");

        Assert.Equal(@"\\?\" + path, LibRawDecoder.ToExtendedLengthPath(path));
    }

    [Fact]
    public void ToExtendedLengthPath_LongUncPath_BecomesTheUncExtendedForm()
    {
        var path = Long(@"\\server\share\dir\");

        Assert.Equal(@"\\?\UNC\server\share\dir\" + new string('a', LibRawDecoder.LongPathThreshold) + @"\photo.cr2", LibRawDecoder.ToExtendedLengthPath(path));
    }

    [Fact]
    public void ToExtendedLengthPath_DotSegmentsAndForwardSlashes_AreNormalisedBecauseExtendedPathsDoNotDoIt()
    {
        var path = @"C:\photos\x\..\" + new string('b', LibRawDecoder.LongPathThreshold) + "/photo.cr2";

        Assert.Equal(@"\\?\C:\photos\" + new string('b', LibRawDecoder.LongPathThreshold) + @"\photo.cr2", LibRawDecoder.ToExtendedLengthPath(path));
    }

    [Theory]
    [InlineData(@"C:\photos\short.cr2")]
    [InlineData(@"relative\dir\photo.cr2")]
    public void ToExtendedLengthPath_ShortOrRelativePath_IsLeftAlone(string path)
    {
        Assert.Null(LibRawDecoder.ToExtendedLengthPath(path));
    }

    [Fact]
    public void ToExtendedLengthPath_ShortPathJustBelowTheThreshold_IsLeftAlone()
    {
        var path = @"C:\" + new string('a', LibRawDecoder.LongPathThreshold - 8) + ".cr2";
        Assert.Equal(LibRawDecoder.LongPathThreshold - 1, path.Length);

        Assert.Null(LibRawDecoder.ToExtendedLengthPath(path));
    }

    [Fact]
    public void ToExtendedLengthPath_AlreadyExtendedOrDevicePath_IsLeftAlone()
    {
        Assert.Null(LibRawDecoder.ToExtendedLengthPath(@"\\?\" + Long(@"C:\photos\")));
        Assert.Null(LibRawDecoder.ToExtendedLengthPath(@"\\.\" + Long(@"C:\photos\")));
    }

    // ---- TrimToEndOfImage: arbitrary trailing bytes after the real EOI, hostile embedded FFD9 ----

    private static byte[] Jpeg() => SyntheticRawBuilder.CreateMinimalJpeg(64, 48);

    /// <summary>The same JPEG with an APP1 segment (an EXIF-style embedded thumbnail, itself a complete JPEG) right after SOI.</summary>
    private static byte[] JpegWithEmbeddedThumbnail()
    {
        var thumbnail = SyntheticRawBuilder.CreateMinimalJpeg(8, 6);
        var main = Jpeg();
        var app1 = new List<byte> { 0xFF, 0xE1 };
        var length = 2 + 6 + thumbnail.Length;
        app1.Add((byte)(length >> 8));
        app1.Add((byte)length);
        app1.AddRange("Exif\0\0"u8.ToArray());
        app1.AddRange(thumbnail);
        return [.. main[..2], .. app1, .. main[2..]];
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(4096)]
    public void TrimToEndOfImage_ArbitraryNonFillTailAfterTheEoi_IsTrimmed(int tailLength)
    {
        var jpeg = Jpeg();
        var tail = new byte[tailLength];
        for (var i = 0; i < tail.Length; i++) tail[i] = (byte)(0x11 + i % 0x60); // never 00/FF

        Assert.Equal(jpeg, LibRawDecoder.TrimToEndOfImage([.. jpeg, .. tail]));
    }

    [Fact]
    public void TrimToEndOfImage_TailWithFfBytesButNoEoi_IsTrimmed()
    {
        var jpeg = Jpeg();
        byte[] tail = [0x12, 0xFF, 0x00, 0x34, 0xFF, 0xFE, 0x56, 0xFF, 0x78];

        Assert.Equal(jpeg, LibRawDecoder.TrimToEndOfImage([.. jpeg, .. tail]));
    }

    [Fact]
    public void TrimToEndOfImage_EmbeddedThumbnailWithCompleteMainImage_KeepsTheWholeMainImage()
    {
        var jpeg = JpegWithEmbeddedThumbnail();

        Assert.Equal(jpeg, LibRawDecoder.TrimToEndOfImage([.. jpeg, 0x12, 0x34, 0x56]));
    }

    [Fact]
    public void TrimToEndOfImage_MainImageTruncatedAfterAnEmbeddedThumbnailEoi_IsNotCutAtTheThumbnail()
    {
        var jpeg = JpegWithEmbeddedThumbnail();
        // The main image lost its EOI (truncated stream) and carries a garbage tail: the only FFD9 left is the thumbnail's,
        // followed by the main image's DQT/SOF/SOS, so it must not be taken as the end of the image.
        var truncated = jpeg[..^2];

        Assert.Throws<InvalidDataException>(() => LibRawDecoder.TrimToEndOfImage([.. truncated, 0x12, 0x34, 0x56, 0x78]));
    }

    [Fact]
    public void TrimToEndOfImage_TruncatedStreamWithGarbageTail_Throws()
    {
        var jpeg = Jpeg();

        Assert.Throws<InvalidDataException>(() => LibRawDecoder.TrimToEndOfImage([.. jpeg[..^2], 0x12, 0x34, 0x56, 0x78]));
    }

    [Fact]
    public void TrimToEndOfImage_ProgressiveStyleSecondScanBeforeTheEoi_IsKept()
    {
        // Two scans: the first scan's entropy data ends at a SOS marker, not at the EOI.
        var jpeg = Jpeg();
        var sos = jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xDA });
        var twoScans = jpeg[..^2].Concat(jpeg[sos..^2]).Concat(new byte[] { 0xFF, 0xD9 }).ToArray();

        Assert.Equal(twoScans, LibRawDecoder.TrimToEndOfImage([.. twoScans, 0x12, 0x34]));
    }

    [Fact]
    public void TrimToEndOfImage_UnparsableSegmentLengthButLastEoiWithCleanTail_FallsBackToTheLastEoi()
    {
        var jpeg = Jpeg();
        var corrupted = (byte[])jpeg.Clone();
        corrupted[4] = 0x00; // APP0 length 1: invalid, the structure walk gives up
        corrupted[5] = 0x01;

        Assert.Equal(corrupted, LibRawDecoder.TrimToEndOfImage([.. corrupted, 0x12, 0x34, 0x56]));
    }
}
