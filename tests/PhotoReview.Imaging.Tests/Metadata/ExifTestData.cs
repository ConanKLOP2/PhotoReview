using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Metadata;

/// <summary>Hand-built EXIF/TIFF blocks (either byte order) and encoder-built JPEGs carrying EXIF, for the EXIF tests.</summary>
internal static class ExifTestData
{
    internal readonly record struct Entry(ushort Tag, ushort Type, uint Count, byte[] Value);

    public static Entry Ascii(ushort tag, string text) =>
        new(tag, 2, (uint)(Encoding.ASCII.GetByteCount(text) + 1), [.. Encoding.ASCII.GetBytes(text), 0]);

    public static Entry Short(ushort tag, ushort value, bool little) =>
        new(tag, 3, 1, U16(value, little));

    public static Entry Rational(ushort tag, uint numerator, uint denominator, bool little) =>
        new(tag, 5, 1, [.. U32(numerator, little), .. U32(denominator, little)]);

    public static byte[] U16(ushort value, bool little)
    {
        var bytes = new byte[2];
        if (little) BinaryPrimitives.WriteUInt16LittleEndian(bytes, value); else BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    public static byte[] U32(uint value, bool little)
    {
        var bytes = new byte[4];
        if (little) BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); else BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>
    /// TIFF block: header, IFD0 (plus an Exif-IFD pointer when <paramref name="exif"/> is non-empty), the Exif IFD,
    /// then the out-of-line values. Offsets are relative to the block start, as in a real APP1 segment.
    /// </summary>
    public static byte[] Tiff(bool little, IReadOnlyList<Entry> ifd0, IReadOnlyList<Entry> exif)
    {
        var ifd0Entries = new List<Entry>(ifd0);
        var hasExif = exif.Count > 0;
        if (hasExif) ifd0Entries.Add(new Entry(ExifParser.TagExifIfd, 4, 1, new byte[4])); // patched below

        var ifd0Offset = 8;
        var ifd0Size = 2 + (ifd0Entries.Count * 12) + 4;
        var exifOffset = ifd0Offset + ifd0Size;
        var exifSize = hasExif ? 2 + (exif.Count * 12) + 4 : 0;
        var dataOffset = exifOffset + exifSize;

        var data = new List<byte>();
        var output = new List<byte>();
        output.AddRange(little ? "II"u8.ToArray() : "MM"u8.ToArray());
        output.AddRange(U16(42, little));
        output.AddRange(U32((uint)ifd0Offset, little));

        void WriteIfd(IReadOnlyList<Entry> entries)
        {
            output.AddRange(U16((ushort)entries.Count, little));
            foreach (var entry in entries)
            {
                var value = entry.Tag == ExifParser.TagExifIfd ? U32((uint)exifOffset, little) : entry.Value;
                output.AddRange(U16(entry.Tag, little));
                output.AddRange(U16(entry.Type, little));
                output.AddRange(U32(entry.Count, little));
                if (value.Length <= 4)
                {
                    output.AddRange(value);
                    output.AddRange(new byte[4 - value.Length]);
                }
                else
                {
                    output.AddRange(U32((uint)(dataOffset + data.Count), little));
                    data.AddRange(value);
                    if (data.Count % 2 == 1) data.Add(0);
                }
            }
            output.AddRange(U32(0, little)); // no next IFD
        }

        WriteIfd(ifd0Entries);
        if (hasExif) WriteIfd(exif);
        output.AddRange(data);
        return [.. output];
    }

    /// <summary>A structurally minimal JPEG byte stream: SOI, APP1 "Exif" with <paramref name="tiff"/>, SOS, EOI.</summary>
    public static byte[] JpegWithApp1(byte[] tiff, byte[]? app0 = null)
    {
        var output = new List<byte> { 0xFF, 0xD8 };
        if (app0 is not null) AddSegment(output, 0xE0, app0);
        AddSegment(output, 0xE1, [.. "Exif\0\0"u8.ToArray(), .. tiff]);
        output.AddRange(new byte[] { 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9 });
        return [.. output];
    }

    public static void AddSegment(List<byte> output, byte marker, byte[] payload)
    {
        output.Add(0xFF);
        output.Add(marker);
        output.AddRange(U16((ushort)(payload.Length + 2), little: false));
        output.AddRange(payload);
    }

    /// <summary>The camera the "full" fixtures describe.</summary>
    public static (List<Entry> Ifd0, List<Entry> Exif) FullCamera(bool little) =>
    (
        [
            Ascii(ExifParser.TagMake, "Canon"),
            Ascii(ExifParser.TagModel, "Canon EOS R5"),
            Ascii(ExifParser.TagDateTime, "2024:06:30 09:00:00"),
        ],
        [
            Rational(ExifParser.TagExposureTime, 1, 250, little),
            Rational(ExifParser.TagFNumber, 28, 10, little),
            Short(ExifParser.TagIso, 400, little),
            Ascii(ExifParser.TagDateTimeOriginal, "2024:05:01 14:03:22"),
            Rational(ExifParser.TagFocalLength, 50, 1, little),
            Ascii(ExifParser.TagLensModel, "RF24-70mm F2.8 L IS USM"),
        ]
    );

    public static void AssertFullCamera(ExifSummary? summary)
    {
        Assert.NotNull(summary);
        Assert.Equal("Canon", summary.CameraMake);
        Assert.Equal("Canon EOS R5", summary.CameraModel);
        Assert.Equal(new DateTime(2024, 5, 1, 14, 3, 22), summary.DateTaken); // DateTimeOriginal wins over DateTime
        Assert.Equal("RF24-70mm F2.8 L IS USM", summary.LensModel);
        Assert.Equal(400, summary.Iso);
        Assert.Equal(new ExifRational(50, 1), summary.FocalLength);
        Assert.Equal(new ExifRational(28, 10), summary.FNumber);
        Assert.Equal(new ExifRational(1, 250), summary.ExposureTime);
    }

    /// <summary>
    /// A real encoder-written JPEG (WPF JpegBitmapEncoder) whose EXIF is set through <see cref="BitmapMetadata.SetQuery"/>,
    /// i.e. laid out by WIC exactly as the decoders will meet it.
    /// </summary>
    public static byte[] EncodeJpegWithExif(int width = 64, int height = 48, bool withExif = true, ushort orientation = 1)
    {
        var bitmap = FixtureGenerator.CreateGradientCheckerboard(width, height);
        BitmapMetadata? metadata = null;
        if (withExif)
        {
            metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=271}", "Canon");
            metadata.SetQuery("/app1/ifd/{ushort=272}", "Canon EOS R5");
            metadata.SetQuery("/app1/ifd/{ushort=306}", "2024:06:30 09:00:00");
            if (orientation != 1) metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
            metadata.SetQuery("/app1/ifd/exif/{ushort=33434}", Pack(1, 250));
            metadata.SetQuery("/app1/ifd/exif/{ushort=33437}", Pack(28, 10));
            metadata.SetQuery("/app1/ifd/exif/{ushort=34855}", (ushort)400);
            metadata.SetQuery("/app1/ifd/exif/{ushort=36867}", "2024:05:01 14:03:22");
            metadata.SetQuery("/app1/ifd/exif/{ushort=37386}", Pack(50, 1));
            metadata.SetQuery("/app1/ifd/exif/{ushort=42036}", "RF24-70mm F2.8 L IS USM");
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static ulong Pack(uint numerator, uint denominator) => ((ulong)denominator << 32) | numerator;
}
