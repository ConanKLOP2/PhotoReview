using System.IO;
using PhotoReview.TestSupport;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>I4: the header-only read of a hostile JPEG with endless APPn segments must stop at a fixed bound.</summary>
public sealed class TurboJpegHeaderAreaCapTests : IDisposable
{
    private readonly TempRoot _root = new("jpeg-header-cap");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "I4: ExtendHeaderArea never reads past ExtendedHeaderCap although the file is all header segments")]
    public void ExtendHeaderArea_EndlessAppSegments_StopsAtTheCap()
    {
        const int payloadLength = 65_000;
        var segment = new byte[4 + payloadLength];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        segment[2] = (byte)((payloadLength + 2) >> 8);
        segment[3] = unchecked((byte)(payloadLength + 2));
        var path = _root.Combine("endless-app.jpg");
        long written = 2;
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.Write([0xFF, 0xD8]);
            while (written < TurboJpegDecoder.ExtendedHeaderCap + 2L * 1024 * 1024)
            {
                stream.Write(segment);
                written += segment.Length;
            }
        }

        var initial = new byte[64 * 1024];
        using (var stream = File.OpenRead(path)) stream.ReadExactly(initial);

        var extended = TurboJpegDecoder.ExtendHeaderArea(path, initial);

        Assert.True(extended.Length > initial.Length);
        Assert.True(extended.Length <= TurboJpegDecoder.ExtendedHeaderCap,
            $"read {extended.Length} bytes of a {written}-byte file");
    }
}
