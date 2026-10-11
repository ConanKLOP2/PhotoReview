using PhotoReview.Imaging.LibRaw;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Reader size vs LibRaw size for every corpus file. The reader reports the camera-visible image (the size of the embedded full JPEG
/// and of the Exif dimensions: Canon/Nikon/Sony/Fuji/Panasonic/Pentax/DNG), LibRaw decodes the sensor's active area and so is usually
/// a few pixels larger (masked-border margins kept for demosaicing: e.g. 5D Mark IV 6720x4480 vs 6744x4502); it is one or two pixels
/// smaller only for the R6. The three ORF bodies are equal (see <see cref="OrfDisplayedSizeTests"/>). The zoom swap therefore
/// changes the content scale by at most 0.6 % (Canon 350D: 3456 vs 3474) on the other formats. Exact numbers pin the audit, so a
/// reader or LibRaw change shows up here. LibRaw size is <c>ReadInfo</c> (header parse only), which
/// ReadInfo_PortraitRewrittenCorpusFile_MatchesDecodedOrientedDimensions and the E-P3 decode test show equals a real decode.
/// </summary>
[Trait("Category", "Native")]
public sealed class RawSizeConsistencyAuditTests
{
    [Theory]
    [InlineData("Apple - iPhone 6s Plus - 16bit (4_3).DNG", 4032, 3024, 4032, 3024)]
    [InlineData("Canon - EOS 350D - RAW (3_2).CR2", 3456, 2304, 3474, 2314)]
    [InlineData("Canon - EOS 5D Mark IV - RAW (3_2).CR2", 6720, 4480, 6744, 4502)]
    [InlineData("Canon - EOS 7D - sRAW2 (sRAW) (3_2).CR2", 2592, 1728, 2592, 1728)]
    [InlineData("Canon - EOS M50 - CRAW (3_2).CR3", 6000, 4000, 6024, 4020)]
    [InlineData("Canon - EOS R6 - 3_2.CR3", 3408, 2272, 3407, 2271)]
    [InlineData("Fujifilm - X-E2S - 14bit 14bit uncompressed (3_2).RAF", 4896, 3264, 4934, 3296)]
    [InlineData("Fujifilm - X-T2 - 14bit 14bit uncompressed (3_2).RAF", 6000, 4000, 6032, 4032)]
    [InlineData("Fujifilm - X100V - 14bit 14bit compressed (3_2).RAF", 6240, 4160, 6246, 4170)]
    [InlineData("Leica - M8 - 8bit 8bit uncompressed (3_2).DNG", 3916, 2634, 3920, 2638)]
    [InlineData("Nikon - D40X - 12bit 12bit compressed (Lossy (type 1)) (3_2).NEF", 3872, 2592, 3900, 2613)]
    [InlineData("Nikon - D800 - 14bit 14bit compressed (Lossless) (3_2).NEF", 7360, 4912, 7378, 4924)]
    [InlineData("Nikon - Z 7 - 12bit 12bit compressed (3_2).NEF", 8256, 5504, 8288, 5520)]
    [InlineData("Olympus - E-M1 - 16bit (4_3).orf", 4640, 3472, 4640, 3472)]
    [InlineData("Olympus - E-P3 - 16bit (4_3).ORF", 4056, 3040, 4056, 3040)]
    [InlineData("OM System - OM-1 - 16bit (4_3).ORF", 5220, 3912, 5220, 3912)]
    [InlineData("Panasonic - DC-GH5 - 1_1.RW2", 5184, 3888, 5208, 3904)]
    [InlineData("Panasonic - DC-S1 - 3_2.RW2", 6000, 4000, 6024, 4016)]
    [InlineData("Panasonic - DMC-GF1 - 4_3.rw2", 4000, 3000, 4016, 3016)]
    [InlineData("Pentax - K-7 - 12bit (3_2).DNG", 4672, 3104, 4684, 3122)]
    [InlineData("Sony - ILCE-7M3 - 14bit 14bit compressed (3_2).ARW", 6000, 4000, 6024, 4024)]
    [InlineData("Sony - ILCE-7R - 14bit 14bit compressed (3_2).ARW", 7360, 4912, 7362, 4920)]
    [InlineData("Sony - NEX-6 - 12bit 12bit compressed (3_2).ARW", 4912, 3264, 4920, 3276)]
    public void CorpusFile_ReaderAndLibRawSizes_ArePinned(string fileName, int readerWidth, int readerHeight, int librawWidth, int librawHeight)
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason)) return;
        var path = RawCorpus.TryGetFile(fileName);
        if (path is null) return;

        var reader = new RawDecoder(new LibRawDecoder(WpfBitmapSourceCodec.Instance)).ReadInfo(path);
        var libraw = new LibRawDecoder(WpfBitmapSourceCodec.Instance).ReadInfo(path);

        Assert.Equal((readerWidth, readerHeight), (reader.Width, reader.Height));
        Assert.Equal((librawWidth, librawHeight), (libraw.Width, libraw.Height));
    }
}
