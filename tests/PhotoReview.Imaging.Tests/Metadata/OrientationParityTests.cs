using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Tests.Metadata;

/// <summary>
/// RV-I04: TurboJpegDecoder used to carry its own EXIF orientation parser that disagreed with ExifParser on malformed IFD0s,
/// so ReadInfo/Decode (TurboJpeg) and the other decoders could rotate the same file differently.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class OrientationParityTests
{
    private static byte[] Header(bool little, uint ifd0Offset)
    {
        var header = new List<byte>();
        header.AddRange(little ? "II"u8.ToArray() : "MM"u8.ToArray());
        header.AddRange(ExifTestData.U16(42, little));
        header.AddRange(ExifTestData.U32(ifd0Offset, little));
        return [.. header];
    }

    private static byte[] Entry(bool little, ushort tag, ushort type, uint count, byte[] value4)
    {
        var entry = new List<byte>();
        entry.AddRange(ExifTestData.U16(tag, little));
        entry.AddRange(ExifTestData.U16(type, little));
        entry.AddRange(ExifTestData.U32(count, little));
        entry.AddRange(value4);
        return [.. entry];
    }

    private static byte[] ShortValue(bool little, ushort v) => [.. ExifTestData.U16(v, little), 0, 0];

    private static byte[] Ifd0At8(bool little, params byte[][] entries)
    {
        var tiff = new List<byte>(Header(little, 8));
        tiff.AddRange(ExifTestData.U16((ushort)entries.Length, little));
        foreach (var entry in entries) tiff.AddRange(entry);
        tiff.AddRange(new byte[4]);
        return [.. tiff];
    }

    public static TheoryData<string, byte[]> Cases()
    {
        var data = new TheoryData<string, byte[]>
        {
            { "duplicate tag 274: 0 then 6", Ifd0At8(true, Entry(true, 274, 3, 1, ShortValue(true, 0)), Entry(true, 274, 3, 1, ShortValue(true, 6))) },
            { "tag 274 stored as BYTE (big-endian)", Ifd0At8(false, Entry(false, 274, 1, 1, [6, 0, 0, 0])) },
            { "normal orientation 6 (little-endian)", Ifd0At8(true, Entry(true, 274, 3, 1, ShortValue(true, 6))) },
            { "normal orientation 8 (big-endian)", Ifd0At8(false, Entry(false, 274, 3, 1, ShortValue(false, 8))) },
        };

        // IFD0 offset inside the header itself (4): the "count" is the offset field, so the entries are misaligned garbage.
        var overlapping = new byte[40];
        Header(true, 4).CopyTo(overlapping, 0);
        Entry(true, 274, 3, 1, ShortValue(true, 6)).CopyTo(overlapping, 18);
        data.Add("ifd0Offset = 4 (inside the header)", overlapping);
        return data;
    }

    [Theory(DisplayName = "TurboJpeg and ExifParser read the same orientation from well-formed and malformed IFD0s")]
    [MemberData(nameof(Cases))]
    public void TurboJpegAndExifParser_Agree(string name, byte[] tiff)
    {
        var jpeg = ExifTestData.JpegWithApp1(tiff);

        var expected = ExifParser.TryReadOrientation(tiff) ?? 1;
        var turbo = TurboJpegDecoder.ReadExifOrientation(jpeg);

        Assert.True(expected == turbo, $"{name}: ExifParser {expected}, TurboJpeg {turbo}");
    }

    [Fact(DisplayName = "TurboJpeg keeps reading a plain orientation-6 EXIF block as 6")]
    public void NormalOrientation_StillRead() =>
        Assert.Equal(6, TurboJpegDecoder.ReadExifOrientation(ExifTestData.JpegWithApp1(Ifd0At8(true, Entry(true, 274, 3, 1, ShortValue(true, 6))))));
}
