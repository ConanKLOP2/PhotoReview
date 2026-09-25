using System.Globalization;

namespace PhotoReview.Imaging.Metadata;

/// <summary>
/// Maps WIC metadata-query results to an <see cref="ExifSummary"/>. Shared by the WPF decoder
/// (<c>BitmapMetadata.GetQuery</c> on the header frame it already opens) and WicDirect (its frame's
/// <c>IWICMetadataQueryReader</c>), so both read the same tags the same way. WIC's value types: ASCII = string,
/// SHORT = ushort (ushort[] for several), LONG = uint, RATIONAL = ulong (low 32 bits numerator, high 32 bits
/// denominator), SRATIONAL = long.
/// </summary>
public static class ExifQueryInterpreter
{
    /// <summary>IFD root of the EXIF block: inside APP1 for JPEG, the file itself for TIFF.</summary>
    public const string JpegIfdRoot = "/app1/ifd";

    /// <inheritdoc cref="JpegIfdRoot"/>
    public const string TiffIfdRoot = "/ifd";

    /// <param name="query">Returns the value of a metadata query, or null when absent/unreadable. Must not throw.</param>
    /// <param name="ifdRoot"><see cref="JpegIfdRoot"/> or <see cref="TiffIfdRoot"/>.</param>
    public static ExifSummary? Read(Func<string, object?> query, string ifdRoot)
    {
        ArgumentNullException.ThrowIfNull(query);
        string Ifd0(ushort tag) => string.Create(CultureInfo.InvariantCulture, $"{ifdRoot}/{{ushort={tag}}}");
        string Exif(ushort tag) => string.Create(CultureInfo.InvariantCulture, $"{ifdRoot}/exif/{{ushort={tag}}}");

        var date = AsString(query(Exif(ExifParser.TagDateTimeOriginal)))
            ?? AsString(query(Exif(ExifParser.TagDateTimeDigitized)))
            ?? AsString(query(Ifd0(ExifParser.TagDateTime)));
        return ExifSummary.Create(
            date,
            AsString(query(Ifd0(ExifParser.TagMake))),
            AsString(query(Ifd0(ExifParser.TagModel))),
            AsString(query(Exif(ExifParser.TagLensModel))),
            AsInteger(query(Exif(ExifParser.TagIso))),
            AsRational(query(Exif(ExifParser.TagFocalLength))),
            AsRational(query(Exif(ExifParser.TagFNumber))),
            AsRational(query(Exif(ExifParser.TagExposureTime))));
    }

    internal static string? AsString(object? value) => value switch
    {
        string text => text,
        char[] chars => new string(chars),
        _ => null,
    };

    internal static long? AsInteger(object? value) => value switch
    {
        byte b => b,
        ushort us => us,
        short s => s,
        uint ui => ui,
        int i => i,
        ushort[] { Length: > 0 } array => array[0],
        uint[] { Length: > 0 } array => array[0],
        short[] { Length: > 0 } array => array[0],
        int[] { Length: > 0 } array => array[0],
        _ => null,
    };

    internal static ExifRational? AsRational(object? value) => value switch
    {
        ulong packed => FromPacked(packed),
        long signed => FromSigned(signed),
        ulong[] { Length: > 0 } array => FromPacked(array[0]),
        long[] { Length: > 0 } array => FromSigned(array[0]),
        _ => null,
    };

    private static ExifRational FromPacked(ulong packed) => new((uint)(packed & 0xFFFF_FFFF), (uint)(packed >> 32));

    private static ExifRational? FromSigned(long packed)
    {
        int numerator = unchecked((int)(packed & 0xFFFF_FFFF)), denominator = unchecked((int)(packed >> 32));
        return numerator > 0 && denominator > 0 ? new ExifRational((uint)numerator, (uint)denominator) : null;
    }
}
