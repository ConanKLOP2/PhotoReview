using PhotoReview.Imaging.Raw.Tiff;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>
/// Legacy Olympus MakerNotes start with "OLYMP\0" + 2 version bytes and keep their IFD at +8 with file-absolute
/// offsets. They used to fall through to the plain-TIFF branch, which parsed the signature bytes as an IFD.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class OrfLegacyMakerNoteTests
{
    private const int NoteOffset = 200;
    private const int JpegOffset = 400;

    private static readonly byte[] Jpeg = SyntheticRawBuilder.CreateMinimalJpeg(160, 120);

    private static byte[] Build(bool littleEndian, ReadOnlySpan<byte> signature)
    {
        var signatureLength = signature.Length;
        var file = new TiffBytes(littleEndian, JpegOffset + Jpeg.Length).Header(8, littleEndian ? "IIRO"u8 : "MMOR"u8)
            .Ifd(8, 0, Long(0x8769, 100))
            .Ifd(100, 0, At(0x927C, 7, 100, NoteOffset))
            .Put(NoteOffset, signature)
            // ThumbnailImage (0x0100): UNDEFINED bytes at a file-absolute offset.
            .Ifd(NoteOffset + signatureLength, 0, At(0x0100, 7, (uint)Jpeg.Length, JpegOffset))
            .Put(JpegOffset, Jpeg);
        return file.ToArray();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OrfReader_LegacyOlympNote_ThumbnailOffsetIsFileAbsoluteAndIfdIsAtPlusEight(bool littleEndian)
    {
        var data = Build(littleEndian, "OLYMP\0\x01\0"u8);

        var info = new OrfContainerReader().Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

        var preview = Assert.Single(info.Previews);
        Assert.Equal(JpegOffset, preview.Offset);
        Assert.Equal(Jpeg.Length, preview.Length);
    }

    [Fact]
    public void OrfReader_LegacyOlympNote_DoesNotParseSignatureBytesAsIfd()
    {
        // Signature only, nothing at +8: the reader must not throw or invent a preview.
        var data = Build(true, "OLYMP\0\x01\0"u8);
        data.AsSpan(NoteOffset + 8, 40).Clear();

        var info = new OrfContainerReader().Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

        Assert.Empty(info.Previews);
    }
}
