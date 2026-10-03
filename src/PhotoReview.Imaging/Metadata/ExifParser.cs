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
    internal const ushort TagOrientation = 0x0112;
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
    public static ExifSummary? TryParseJpeg(ReadOnlySpan<byte> jpeg) => TryParseGuarded(FindExifTiffBlock(jpeg), allowOlympusRawMagic: false);

    /// <summary>
    /// Reads the EXIF summary from an already-located Exif TIFF block (the bytes after "Exif\0\0" in the first
    /// Exif-headed APP1 segment, as <see cref="FindExifTiffBlock"/> or a caller's own marker walk -- e.g.
    /// TurboJpegDecoder's combined header scan (IMG-07) -- would return). Null for an empty span, no/corrupt
    /// EXIF, or no usable field; never throws. Lets a caller that already walked the JPEG markers itself (to
    /// avoid a second walk over the same header bytes) reuse this parser without going through
    /// <see cref="TryParseJpeg"/>'s own <see cref="FindExifTiffBlock"/> walk.
    /// A whole-file TIFF block of an Olympus ORF ("IIRO"/"IIRS"/"MMOR": magic 0x4F52 or 0x5352 instead of 42) is
    /// accepted here too, because the RAW pipeline hands ORF blocks to this method; <see cref="TryParseJpeg"/> stays strict.
    /// </summary>
    public static ExifSummary? TryParseTiffBlock(ReadOnlySpan<byte> tiffBlock) =>
        TryParseGuarded(tiffBlock, allowOlympusRawMagic: true, ifdIsExif: false);

    /// <summary>
    /// Like <see cref="TryParseTiffBlock(ReadOnlySpan{byte})"/> but, when <paramref name="ifdIsExif"/> is true, treats the
    /// block's IFD0 itself as the Exif IFD (exposure time, f-number, ISO, dates, focal length, lens) instead of following a
    /// 0x8769 pointer. Canon CR3 stores its exposure fields this way in the moov "CMT2" box. Make/Model/DateTime are not
    /// read in that mode (they belong to IFD0 proper, i.e. CMT1).
    /// </summary>
    public static ExifSummary? TryParseTiffBlock(ReadOnlySpan<byte> tiffBlock, bool ifdIsExif) =>
        TryParseGuarded(tiffBlock, allowOlympusRawMagic: true, ifdIsExif);

    private static ExifSummary? TryParseGuarded(ReadOnlySpan<byte> tiffBlock, bool allowOlympusRawMagic, bool ifdIsExif = false)
    {
        if (tiffBlock.IsEmpty) return null;
        try
        {
            return TryParseTiff(tiffBlock, allowOlympusRawMagic, ifdIsExif);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or DecoderFallbackException)
        {
            // Defensive only: the bounds checks below should make this unreachable. A metadata bug must never fail a decode.
            return null;
        }
    }

    /// <summary>
    /// Reads the EXIF Orientation (tag 0x0112, IFD0) of a JPEG's first Exif APP1 segment. Null for a non-JPEG, no/corrupt
    /// EXIF, or an orientation outside 1 to 8; never throws.
    /// </summary>
    public static int? TryReadOrientationFromJpeg(ReadOnlySpan<byte> jpeg) => TryReadOrientation(FindExifTiffBlock(jpeg));

    /// <summary>
    /// Reads the Orientation (tag 0x0112) from IFD0 of an Exif TIFF block ("II*\0" / "MM\0*" header). Null when the block is
    /// empty/corrupt, has no Orientation, or the value is outside 1 to 8; never throws.
    /// </summary>
    public static int? TryReadOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return null;
        bool little;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I') little = true;
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M') little = false;
        else return null;
        if (TiffStructure.ReadU16(tiff, 2, little) != 42) return null;

        var ifd0 = TiffStructure.ReadU32(tiff, 4, little);
        if (ifd0 < 8 || ifd0 > (uint)tiff.Length - 2) return null;
        var start = (int)ifd0;
        int count = Math.Min(Math.Min((int)TiffStructure.ReadU16(tiff, start, little), MaxIfdEntries), (tiff.Length - start - 2) / 12);
        for (var i = 0; i < count; i++)
        {
            var entry = start + 2 + (i * 12);
            if (TiffStructure.ReadU16(tiff, entry, little) != TagOrientation) continue;
            var type = TiffStructure.ReadU16(tiff, entry + 2, little);
            var n = TiffStructure.ReadU32(tiff, entry + 4, little);
            if (!TiffStructure.TryGetValueSpan(tiff, entry, type, n, little, out var value)) return null;
            return TiffStructure.ReadUnsigned(value, type, little) is long orientation and >= 1 and <= 8 ? (int)orientation : null;
        }
        return null;
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
            if (marker == 0x00) return default;         // 0xFF00 is a stuffed byte, not a marker: the header area is corrupt
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { offset += 2; continue; }
            if (marker is 0xDA or 0xD9) return default; // image data / end: EXIF must come before

            int length = (jpeg[offset + 2] << 8) | jpeg[offset + 3];
            if (length < 2 || offset + 2 + length > jpeg.Length) return default;

            var payload = jpeg.Slice(offset + 4, length - 2);
            // The first Exif-headed APP1 wins even when its TIFF part is empty/garbage (same rule as TurboJpegDecoder).
            if (marker == 0xE1 && payload.Length >= 6 && payload[..6].SequenceEqual("Exif\0\0"u8))
                return payload[6..];
            offset += 2 + length;
        }
        return default;
    }

    /// <summary>Parses a TIFF-structured EXIF block ("II*\0" / "MM\0*" header).</summary>
    internal static ExifSummary? TryParseTiff(ReadOnlySpan<byte> tiff, bool allowOlympusRawMagic = false, bool ifdIsExif = false)
    {
        if (tiff.Length < 8) return null;
        bool little;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I') little = true;
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M') little = false;
        else return null;
        ushort magic = TiffStructure.ReadU16(tiff, 2, little);
        // Olympus ORF replaces 42 with 'RO' (0x4F52) or 'SR' (0x5352) but is otherwise plain TIFF.
        if (magic != 42 && !(allowOlympusRawMagic && magic is 0x4F52 or 0x5352)) return null;

        var values = new RawValues();
        var ifd0 = TiffStructure.ReadU32(tiff, 4, little);
        if (ifdIsExif)
        {
            ReadIfd(tiff, ifd0, little, ref values, isExifIfd: true);
        }
        else
        {
            var exifIfd = ReadIfd(tiff, ifd0, little, ref values, isExifIfd: false);
            // Only one level is followed (no recursion), so a hostile pointer back to IFD0 cannot loop.
            if (exifIfd is { } exifOffset)
                ReadIfd(tiff, exifOffset, little, ref values, isExifIfd: true);
        }

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
        int count = TiffStructure.ReadU16(tiff, start, little);
        var available = (tiff.Length - start - 2) / 12;
        count = Math.Min(Math.Min(count, MaxIfdEntries), available);

        uint? exifPointer = null;
        for (var i = 0; i < count; i++)
        {
            var entry = start + 2 + (i * 12);
            var tag = TiffStructure.ReadU16(tiff, entry, little);
            var type = TiffStructure.ReadU16(tiff, entry + 2, little);
            var n = TiffStructure.ReadU32(tiff, entry + 4, little);
            if (!TiffStructure.TryGetValueSpan(tiff, entry, type, n, little, out var value)) continue;

            if (!isExifIfd)
            {
                switch (tag)
                {
                    case TagMake: values.Make ??= TiffStructure.ReadAscii(value, type); break;
                    case TagModel: values.Model ??= TiffStructure.ReadAscii(value, type); break;
                    case TagDateTime: values.DateTime ??= TiffStructure.ReadAscii(value, type); break;
                    case TagExifIfd:
                        if (exifPointer is null && TiffStructure.ReadUnsigned(value, type, little) is long pointer && pointer is >= 8 and <= uint.MaxValue)
                            exifPointer = (uint)pointer;
                        break;
                }
            }
            else
            {
                switch (tag)
                {
                    case TagExposureTime: values.ExposureTime ??= TiffStructure.ReadRational(value, type, little); break;
                    case TagFNumber: values.FNumber ??= TiffStructure.ReadRational(value, type, little); break;
                    case TagIso: values.Iso ??= TiffStructure.ReadUnsigned(value, type, little); break;
                    case TagDateTimeOriginal: values.DateOriginal ??= TiffStructure.ReadAscii(value, type); break;
                    case TagDateTimeDigitized: values.DateDigitized ??= TiffStructure.ReadAscii(value, type); break;
                    case TagFocalLength: values.FocalLength ??= TiffStructure.ReadRational(value, type, little); break;
                    case TagLensModel: values.Lens ??= TiffStructure.ReadAscii(value, type); break;
                }
            }
        }
        return exifPointer;
    }
}
