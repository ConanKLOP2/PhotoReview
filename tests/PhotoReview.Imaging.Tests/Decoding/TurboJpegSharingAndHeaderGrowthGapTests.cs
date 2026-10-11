using System.IO;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.Imaging.TurboJpeg;
using PhotoReview.TestSupport;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>Mutation-gap tests for <see cref="TurboJpegDecoder"/>: reads must tolerate a concurrent writer, and a header area beyond the first read cap must be followed.</summary>
public sealed class TurboJpegSharingAndHeaderGrowthGapTests : IDisposable
{
    private const int PayloadLength = 65_000;
    private const int PaddingBytes = 8 * 1024 * 1024 + 1024 * 1024; // beyond HeaderReadCap (8 MB)

    private readonly TempRoot _root = new("turbojpeg-gap");
    private readonly TurboJpegDecoder _decoder = new(WpfBitmapSourceCodec.Instance);

    public void Dispose() => _root.Dispose();

    private string MakeJpeg(string name)
    {
        var path = _root.Combine(name);
        FixtureGenerator.GenerateGradientJpeg(path, 64, 48);
        return path;
    }

    private static byte[] ComSegment()
    {
        var segment = new byte[4 + PayloadLength];
        segment[0] = 0xFF;
        segment[1] = 0xFE;
        segment[2] = (byte)((PayloadLength + 2) >> 8);
        segment[3] = unchecked((byte)(PayloadLength + 2));
        return segment;
    }

    private string PaddedFile(string name, byte[]? tailAfterSoi)
    {
        var path = _root.Combine(name);
        var segment = ComSegment();
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.Write([0xFF, 0xD8]);
        for (long written = 0; written < PaddingBytes; written += segment.Length) stream.Write(segment);
        if (tailAfterSoi is not null) stream.Write(tailAfterSoi);
        return path;
    }

    [Fact]
    public void ReadInfo_FileThatAnotherProcessHasOpenForWriting_IsStillRead()
    {
        var path = MakeJpeg("shared-info.jpg");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        var info = _decoder.ReadInfo(path);

        Assert.Equal((64, 48), (info.Width, info.Height));
    }

    [Fact]
    public void Decode_FileThatAnotherProcessHasOpenForWriting_IsStillDecoded()
    {
        var path = MakeJpeg("shared-decode.jpg");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        var image = _decoder.Decode(new DecodeRequest(path, 0));

        Assert.Equal(64, image.OriginalWidth);
    }

    [Fact]
    public void ExtendHeaderArea_FileThatAnotherProcessHasOpenForWriting_IsStillRead()
    {
        var path = MakeJpeg("shared-extend.jpg");
        var all = File.ReadAllBytes(path);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        var extended = TurboJpegDecoder.ExtendHeaderArea(path, all[..16]);

        Assert.True(extended.Length > 16);
    }

    [Fact]
    public void ReadInfo_FrameHeaderBeyondTheFirstReadCap_IsFoundByTheExtendedRead()
    {
        var jpeg = File.ReadAllBytes(MakeJpeg("source.jpg"));
        var path = PaddedFile("late-sof.jpg", jpeg[2..]);

        var info = _decoder.ReadInfo(path);

        Assert.Equal((64, 48), (info.Width, info.Height));
    }

    [Fact]
    public void ReadInfo_EndlessHeaderSegmentsWithoutAFrame_FailsInsteadOfReportingASize()
    {
        var path = PaddedFile("no-sof.jpg", tailAfterSoi: null);

        Assert.ThrowsAny<InvalidDataException>(() => _decoder.ReadInfo(path));
    }
}