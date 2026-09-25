using System.Buffers.Binary;
using System.Text;

namespace PhotoReview.Imaging.Metadata;

/// <summary>
/// Compact binary form of an <see cref="ExifSummary"/> for the preview disk-cache entry (PreviewCacheFile v7).
/// Layout: version byte, presence mask byte, then only the present fields in mask-bit order -- date as int64 ticks,
/// texts as a length byte + UTF-8, ISO as int32, rationals as two uint32 (little-endian). Decoding validates
/// everything and returns null for anything malformed (the entry is then shown without EXIF, never rejected).
/// </summary>
internal static class ExifSummaryCodec
{
    private const byte FormatVersion = 1;

    /// <summary>Upper bound of an encoded summary (texts are capped at 64 chars = at most 192 UTF-8 bytes).</summary>
    internal const int MaxEncodedLength = 1024;

    [Flags]
    private enum Field : byte
    {
        Date = 1, Make = 2, Model = 4, Lens = 8, Iso = 16, Focal = 32, FNumber = 64, Exposure = 128,
    }

    public static byte[] Encode(ExifSummary? summary)
    {
        if (summary is null || summary.IsEmpty) return [];
        var buffer = new List<byte>(96) { FormatVersion, 0 };
        Field mask = 0;
        Span<byte> scratch = stackalloc byte[8];

        if (summary.DateTaken is { } date)
        {
            mask |= Field.Date;
            BinaryPrimitives.WriteInt64LittleEndian(scratch, date.Ticks);
            buffer.AddRange(scratch.ToArray());
        }
        if (AddText(buffer, summary.CameraMake)) mask |= Field.Make;
        if (AddText(buffer, summary.CameraModel)) mask |= Field.Model;
        if (AddText(buffer, summary.LensModel)) mask |= Field.Lens;
        if (summary.Iso is { } iso)
        {
            mask |= Field.Iso;
            BinaryPrimitives.WriteInt32LittleEndian(scratch, iso);
            buffer.AddRange(scratch[..4].ToArray());
        }
        if (AddRational(buffer, summary.FocalLength)) mask |= Field.Focal;
        if (AddRational(buffer, summary.FNumber)) mask |= Field.FNumber;
        if (AddRational(buffer, summary.ExposureTime)) mask |= Field.Exposure;

        buffer[1] = (byte)mask;
        return buffer.ToArray();
    }

    private static bool AddText(List<byte> buffer, string? text)
    {
        if (text is null) return false;
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > byte.MaxValue) bytes = bytes[..byte.MaxValue]; // unreachable for a sanitised summary
        buffer.Add((byte)bytes.Length);
        buffer.AddRange(bytes);
        return true;
    }

    private static bool AddRational(List<byte> buffer, ExifRational? value)
    {
        if (value is not { } rational) return false;
        Span<byte> scratch = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(scratch, rational.Numerator);
        BinaryPrimitives.WriteUInt32LittleEndian(scratch[4..], rational.Denominator);
        buffer.AddRange(scratch.ToArray());
        return true;
    }

    /// <summary>Decodes <see cref="Encode"/>'s output; null for an empty, unknown-version or malformed block.</summary>
    public static ExifSummary? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2 || data[0] != FormatVersion) return null;
        var mask = (Field)data[1];
        var pos = 2;

        DateTime? date = null;
        if (mask.HasFlag(Field.Date))
        {
            if (!Take(data, ref pos, 8, out var bytes)) return null;
            var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes);
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return null;
            date = new DateTime(ticks, DateTimeKind.Unspecified);
        }
        string? make = null, model = null, lens = null;
        if (mask.HasFlag(Field.Make) && !TakeText(data, ref pos, out make)) return null;
        if (mask.HasFlag(Field.Model) && !TakeText(data, ref pos, out model)) return null;
        if (mask.HasFlag(Field.Lens) && !TakeText(data, ref pos, out lens)) return null;
        int? iso = null;
        if (mask.HasFlag(Field.Iso))
        {
            if (!Take(data, ref pos, 4, out var bytes)) return null;
            iso = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }
        ExifRational? focal = null, fNumber = null, exposure = null;
        if (mask.HasFlag(Field.Focal) && !TakeRational(data, ref pos, out focal)) return null;
        if (mask.HasFlag(Field.FNumber) && !TakeRational(data, ref pos, out fNumber)) return null;
        if (mask.HasFlag(Field.Exposure) && !TakeRational(data, ref pos, out exposure)) return null;
        if (pos != data.Length) return null;

        // Re-sanitise: a cache file is just bytes on disk, treat it like any other untrusted input.
        var summary = ExifSummary.Create(null, make, model, lens, iso, focal, fNumber, exposure);
        if (date is null) return summary;
        return summary is null ? new ExifSummary { DateTaken = date } : summary with { DateTaken = date };
    }

    private static bool Take(ReadOnlySpan<byte> data, ref int pos, int length, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if (length < 0 || pos > data.Length - length) return false;
        bytes = data.Slice(pos, length);
        pos += length;
        return true;
    }

    private static bool TakeText(ReadOnlySpan<byte> data, ref int pos, out string? text)
    {
        text = null;
        if (!Take(data, ref pos, 1, out var lengthByte)) return false;
        if (!Take(data, ref pos, lengthByte[0], out var bytes)) return false;
        text = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static bool TakeRational(ReadOnlySpan<byte> data, ref int pos, out ExifRational? value)
    {
        value = null;
        if (!Take(data, ref pos, 8, out var bytes)) return false;
        value = new ExifRational(BinaryPrimitives.ReadUInt32LittleEndian(bytes), BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]));
        return true;
    }
}
