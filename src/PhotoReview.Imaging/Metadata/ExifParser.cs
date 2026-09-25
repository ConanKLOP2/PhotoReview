using System.Buffers.Binary;
using System.Text;

namespace PhotoReview.Imaging.Metadata;

/// <summary>
/// Small bounded EXIF reader over JPEG bytes a decoder already holds (TurboJpeg). Walks the marker segments up to
/// SOS, takes the first APP1 "Exif\0\0" block (at most 64 KB by the JPEG format) and reads IFD0 plus the Exif sub-IFD
/// only. Fuzz-safe by construction: every offset/length is bounds-checked against the block, entry counts are capped,
/// each IFD is visited at most once and nothing here throws -- bad EXIF yields null (or fewer fields), never an
/// exception out of the decoder.
/// </summary>
public static class ExifParser
{
    internal const ushort TagMake = 0x010F;
    internal const ushort TagModel = 0x0110;
    internal const ushort TagDateTime = 0x0132;
    internal const ushort TagExifIfd = 0x8769;
    internal const ushort TagExposureTime = 0x829A;
    internal const ushort TagFNumber = 0x829D;
    internal const ushort TagIso = 0x8827;
    internal const ushort TagDateTimeOriginal = 0x9003;
    internal const ushort TagDateTimeDigitized = 0x9004;
    internal const ushort TagFocalLength = 0x920A;
    internal const ushort TagLensModel = 0xA434;

    /// <summary>More entries than any real IFD has; a larger count means a corrupt block (read only this many).</summary>
    internal const int MaxIfdEntries = 256;

    /// <summary>Longest ASCII value read (bytes); <see cref="ExifSummary.CleanText"/> caps further.</summary>
    private const int MaxAsciiBytes = 256;

    /// <summary>Reads the EXIF summary of a JPEG; null for a non-JPEG, no/corrupt EXIF, or no usable field.</summary>
    public static ExifSummary? TryParseJpeg(ReadOnlySpan<byte> jpeg)
    {
        try
        {
            var tiff = FindExifTiffBlock(jpeg);
            return tiff.IsEmpty ? null : TryParseTiff(tiff);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or DecoderFallbackException)
        {
            // Defensive only: the bounds checks below should make this unreachable. A metadata bug must never fail a decode.
            return null;
        }
    }

    /// <summary>The TIFF structure inside the first APP1 Exif segment, or empty.</summary>
    internal static ReadOnlySpan<byte> FindExifTiffBlock(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return default;

        var offset = 2;
        while (offset + 4 <= jpeg.Length)
        {
            if (jpeg[offset] != 0xFF) return default;
            var marker = jpeg[offset + 1];
            if (marker == 0xFF) { offset++; continue; } // fill byte
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { offset += 2; continue; }
            if (marker is 0xDA or 0xD9) return default; // image data / end: EXIF must come before

            int length = (jpeg[offset + 2] << 8) | jpeg[offset + 3];
            if (length < 2 || offset + 2 + length > jpeg.Length) return default;

            var payload = jpeg.Slice(offset + 4, length - 2);
            if (marker == 0xE1 && payload.Length > 6 && payload[..6].SequenceEqual("Exif\0\0"u8))
                return payload[6..];
            offset += 2 + length;
        }
        return default;
    }

    /// <summary>Parses a TIFF-structured EXIF block ("II*\0" / "MM\0*" header).</summary>
    internal static ExifSummary? TryParseTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return null;
        bool little;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I') little = true;
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M') little = false;
        else return null;
        if (ReadU16(tiff, 2, little) != 42) return null;

        var values = new RawValues();
        var ifd0 = ReadU32(tiff, 4, little);
        var exifIfd = ReadIfd(tiff, ifd0, little, ref values, isExifIfd: false);
        if (exifIfd is { } exifOffset && exifOffset != ifd0)
            ReadIfd(tiff, exifOffset, little, ref values, isExifIfd: true);

        return ExifSummary.Create(
            values.DateOriginal ?? values.DateDigitized ?? values.DateTime,
            values.Make, values.Model, values.Lens, values.Iso,
            values.FocalLength, values.FNumber, values.ExposureTime);
    }

    private struct RawValues
    {
        public string? Make, Model, DateTime, DateOriginal, DateDigitized, Lens;
        public long? Iso;
        public ExifRational? FocalLength, FNumber, ExposureTime;
    }

    /// <summary>Reads one IFD; returns the Exif sub-IFD offset when this is IFD0 and it has one.</summary>
    private static uint? ReadIfd(ReadOnlySpan<byte> tiff, uint ifdOffset, bool little, ref RawValues values, bool isExifIfd)
    {
        if (ifdOffset < 8 || ifdOffset > (uint)tiff.Length - 2) return null;
        var start = (int)ifdOffset;
        int count = ReadU16(tiff, start, little);
        var available = (tiff.Length - start - 2) / 12;
        count = Math.Min(Math.Min(count, MaxIfdEntries), available);

        uint? exifPointer = null;
        for (var i = 0; i < count; i++)
        {
            var entry = start + 2 + (i * 12);
            var tag = ReadU16(tiff, entry, little);
            var type = ReadU16(tiff, entry + 2, little);
            var n = ReadU32(tiff, entry + 4, little);
            if (!TryGetValueSpan(tiff, entry, type, n, little, out var value)) continue;

            if (!isExifIfd)
            {
                switch (tag)
                {
                    case TagMake: values.Make ??= ReadAscii(value, type); break;
                    case TagModel: values.Model ??= ReadAscii(value, type); break;
                    case TagDateTime: values.DateTime ??= ReadAscii(value, type); break;
                    case TagExifIfd:
                        if (exifPointer is null && ReadUnsigned(value, type, little) is long pointer && pointer is >= 8 and <= uint.MaxValue)
                            exifPointer = (uint)pointer;
                        break;
                }
            }
            else
            {
                switch (tag)
                {
                    case TagExposureTime: values.ExposureTime ??= ReadRational(value, type, little); break;
                    case TagFNumber: values.FNumber ??= ReadRational(value, type, little); break;
                    case TagIso: values.Iso ??= ReadUnsigned(value, type, little); break;
                    case TagDateTimeOriginal: values.DateOriginal ??= ReadAscii(value, type); break;
                    case TagDateTimeDigitized: values.DateDigitized ??= ReadAscii(value, type); break;
                    case TagFocalLength: values.FocalLength ??= ReadRational(value, type, little); break;
                    case TagLensModel: values.Lens ??= ReadAscii(value, type); break;
                }
            }
        }
        return exifPointer;
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1, // BYTE, ASCII, SBYTE, UNDEFINED
        3 or 8 => 2,           // SHORT, SSHORT
        4 or 9 or 11 => 4,     // LONG, SLONG, FLOAT
        5 or 10 or 12 => 8,    // RATIONAL, SRATIONAL, DOUBLE
        _ => 0,
    };

    /// <summary>The bytes of an entry's value (inline when it fits in 4 bytes), bounds-checked; false when out of range.</summary>
    private static bool TryGetValueSpan(ReadOnlySpan<byte> tiff, int entry, ushort type, uint count, bool little, out ReadOnlySpan<byte> value)
    {
        value = default;
        var size = TypeSize(type);
        if (size == 0 || count == 0) return false;
        var total = (long)size * count;
        if (total <= 4)
        {
            value = tiff.Slice(entry + 8, (int)total);
            return true;
        }
        if (type == 2) total = Math.Min(total, MaxAsciiBytes); // only the start of an absurdly long string is needed
        else total = size;                                       // numeric: the first element is all we use
        var offset = ReadU32(tiff, entry + 8, little);
        if (offset > (uint)tiff.Length || total > tiff.Length - offset) return false;
        value = tiff.Slice((int)offset, (int)total);
        return true;
    }

    private static string? ReadAscii(ReadOnlySpan<byte> value, ushort type)
    {
        if (type is not (2 or 7)) return null;
        var end = value.IndexOf((byte)0);
        if (end >= 0) value = value[..end];
        return value.IsEmpty ? null : Encoding.UTF8.GetString(value);
    }

    private static long? ReadUnsigned(ReadOnlySpan<byte> value, ushort type, bool little) => type switch
    {
        1 when value.Length >= 1 => value[0],
        3 when value.Length >= 2 => ReadU16(value, 0, little),
        4 when value.Length >= 4 => ReadU32(value, 0, little),
        8 when value.Length >= 2 => (short)ReadU16(value, 0, little),
        9 when value.Length >= 4 => (int)ReadU32(value, 0, little),
        _ => null,
    };

    private static ExifRational? ReadRational(ReadOnlySpan<byte> value, ushort type, bool little)
    {
        if (value.Length < 8) return null;
        var numerator = ReadU32(value, 0, little);
        var denominator = ReadU32(value, 4, little);
        if (type == 10)
        {
            // SRATIONAL: only a positive value is meaningful for these tags.
            int n = (int)numerator, d = (int)denominator;
            if (n <= 0 || d <= 0) return null;
            return new ExifRational((uint)n, (uint)d);
        }
        return type == 5 ? new ExifRational(numerator, denominator) : null;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset, bool little)
    {
        if (offset < 0 || offset + 2 > data.Length) return 0;
        var slice = data.Slice(offset, 2);
        return little ? BinaryPrimitives.ReadUInt16LittleEndian(slice) : BinaryPrimitives.ReadUInt16BigEndian(slice);
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool little)
    {
        if (offset < 0 || offset + 4 > data.Length) return 0;
        var slice = data.Slice(offset, 4);
        return little ? BinaryPrimitives.ReadUInt32LittleEndian(slice) : BinaryPrimitives.ReadUInt32BigEndian(slice);
    }
}
