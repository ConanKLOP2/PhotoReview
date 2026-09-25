using System.Globalization;
using System.Text;

namespace PhotoReview.Imaging.Metadata;

/// <summary>An unsigned EXIF RATIONAL, kept exact so a shutter speed of 1/250 is shown as "1/250", not "0.004".</summary>
public readonly record struct ExifRational(uint Numerator, uint Denominator)
{
    /// <summary>Numerator / Denominator (0 when the denominator is 0).</summary>
    public double Value => Denominator == 0 ? 0 : (double)Numerator / Denominator;
}

/// <summary>
/// The handful of EXIF fields the photo information line shows. Produced by the decoder during the decode that
/// already runs (see <see cref="ExifParser"/> and <see cref="ExifQueryInterpreter"/>), carried on
/// <see cref="Decoding.IDecodedImage.Exif"/> and persisted in the preview disk-cache entry, so a cache hit never
/// re-reads the source. Every field is optional; values are already sanitised (trimmed, length-capped, zero or
/// invalid values dropped). Parsing is culture-invariant; display formatting is the caller's business.
/// </summary>
public sealed record ExifSummary
{
    /// <summary>Longest camera/lens text kept (characters); real values are far shorter.</summary>
    public const int MaxTextLength = 64;

    public DateTime? DateTaken { get; init; }
    public string? CameraMake { get; init; }
    public string? CameraModel { get; init; }
    public string? LensModel { get; init; }
    public int? Iso { get; init; }

    /// <summary>Focal length in millimetres.</summary>
    public ExifRational? FocalLength { get; init; }

    /// <summary>Aperture as an f-number (2.8 = f/2.8).</summary>
    public ExifRational? FNumber { get; init; }

    /// <summary>Exposure time in seconds.</summary>
    public ExifRational? ExposureTime { get; init; }

    public bool IsEmpty => DateTaken is null && CameraMake is null && CameraModel is null && LensModel is null
        && Iso is null && FocalLength is null && FNumber is null && ExposureTime is null;

    /// <summary>
    /// Builds a sanitised summary from raw tag values; returns null when nothing usable is left. Never throws.
    /// </summary>
    public static ExifSummary? Create(
        string? dateTaken, string? make, string? model, string? lens, long? iso,
        ExifRational? focalLength, ExifRational? fNumber, ExifRational? exposureTime)
    {
        var summary = new ExifSummary
        {
            DateTaken = ParseDate(dateTaken),
            CameraMake = CleanText(make),
            CameraModel = CleanText(model),
            LensModel = CleanText(lens),
            Iso = iso is > 0 and <= 10_000_000 ? (int)iso.Value : null,
            FocalLength = Positive(focalLength),
            FNumber = Positive(fNumber),
            ExposureTime = Positive(exposureTime),
        };
        return summary.IsEmpty ? null : summary;
    }

    private static ExifRational? Positive(ExifRational? value) =>
        value is { Numerator: > 0, Denominator: > 0 } ? value : null;

    private static readonly string[] DateFormats = ["yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy:MM:dd"];

    /// <summary>EXIF date "YYYY:MM:DD HH:MM:SS" (invariant); a "0000:00:00 ..." placeholder or garbage gives null.</summary>
    internal static DateTime? ParseDate(string? text)
    {
        var cleaned = CleanText(text);
        if (cleaned is null) return null;
        return DateTime.TryParseExact(cleaned, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>Stops at the first NUL, drops control characters, trims, caps at <see cref="MaxTextLength"/>; empty = null.</summary>
    internal static string? CleanText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var nul = text.IndexOf('\0', StringComparison.Ordinal);
        if (nul >= 0) text = text[..nul];
        var builder = new StringBuilder(Math.Min(text.Length, MaxTextLength));
        foreach (var ch in text)
        {
            if (builder.Length >= MaxTextLength) break;
            builder.Append(char.IsControl(ch) ? ' ' : ch);
        }
        var cleaned = builder.ToString().Trim();
        // A cut in the middle of a surrogate pair would leave half a character.
        if (cleaned.Length > 0 && char.IsHighSurrogate(cleaned[^1])) cleaned = cleaned[..^1].TrimEnd();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
