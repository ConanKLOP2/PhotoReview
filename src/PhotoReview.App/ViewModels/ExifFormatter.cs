using System.Globalization;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.App.ViewModels;

/// <summary>
/// Builds the photo information line shown under the status line, e.g.
/// <c>IMG_1234.jpg · 01/05/2024 14:03 · 6000×4000 · Canon EOS R5 · RF24-70mm F2.8 · ISO 400 · 50 mm · f/2.8 · 1/250 s</c>.
/// Missing values are skipped (never "?" placeholders); units come from the language catalog (ADR 0006); numbers and
/// the date use the display culture (AGENTS.md rule 4 allows CurrentCulture for UI text). The EXIF values themselves
/// were parsed culture-invariantly by the decoder.
/// </summary>
public static class ExifFormatter
{
    /// <summary>Same separator the status line uses between its parts.</summary>
    public const string Separator = " · ";

    /// <param name="fields">Parts to include (settings).</param>
    /// <param name="fileName">Shown for <see cref="ExifInfoFields.FileName"/>.</param>
    /// <param name="width">Original (post-orientation) width; 0 = unknown.</param>
    /// <param name="height">Original (post-orientation) height; 0 = unknown.</param>
    /// <param name="exif">EXIF read during the decode; null = none (e.g. an older cache entry).</param>
    /// <param name="provider">Number/date format; defaults to <see cref="CultureInfo.CurrentCulture"/>.</param>
    public static string Format(ExifInfoFields fields, string? fileName, int width, int height, ExifSummary? exif,
        IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        var parts = new List<string>(9);

        if (fields.HasFlag(ExifInfoFields.FileName) && !string.IsNullOrWhiteSpace(fileName))
            parts.Add(fileName);
        if (fields.HasFlag(ExifInfoFields.DateTaken) && exif?.DateTaken is { } date)
            parts.Add(date.ToString("g", provider));
        if (fields.HasFlag(ExifInfoFields.Dimensions) && width > 0 && height > 0)
            parts.Add(Tr.ExifDimensions(width.ToString(provider), height.ToString(provider)));
        if (fields.HasFlag(ExifInfoFields.Camera) && CameraText(exif?.CameraMake, exif?.CameraModel) is { } camera)
            parts.Add(camera);
        if (fields.HasFlag(ExifInfoFields.Lens) && exif?.LensModel is { } lens)
            parts.Add(lens);
        if (fields.HasFlag(ExifInfoFields.Iso) && exif?.Iso is { } iso)
            parts.Add(Tr.ExifIso(iso.ToString(provider)));
        if (fields.HasFlag(ExifInfoFields.FocalLength) && exif?.FocalLength is { } focal)
            parts.Add(Tr.ExifFocalLength(focal.Value.ToString("0.#", provider)));
        if (fields.HasFlag(ExifInfoFields.Aperture) && exif?.FNumber is { } aperture)
            parts.Add(Tr.ExifAperture(aperture.Value.ToString("0.#", provider)));
        if (fields.HasFlag(ExifInfoFields.ShutterSpeed) && ShutterText(exif?.ExposureTime, provider) is { } shutter)
            parts.Add(shutter);

        return string.Join(Separator, parts);
    }

    /// <summary>
    /// "Canon" + "Canon EOS R5" → "Canon EOS R5"; "NIKON CORPORATION" + "NIKON Z 6" → "NIKON Z 6";
    /// "SONY" + "ILCE-7M3" → "SONY ILCE-7M3". Either part alone is shown as is.
    /// </summary>
    internal static string? CameraText(string? make, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return string.IsNullOrWhiteSpace(make) ? null : make;
        if (string.IsNullOrWhiteSpace(make)) return model;
        if (model.StartsWith(make, StringComparison.OrdinalIgnoreCase)) return model;
        var space = make.IndexOf(' ', StringComparison.Ordinal);
        var makeWord = space > 0 ? make[..space] : make;
        return model.StartsWith(makeWord, StringComparison.OrdinalIgnoreCase) ? model : make + " " + model;
    }

    /// <summary>1/250 s for short exposures (exact when the numerator is 1), decimal seconds from 0.5 s up.</summary>
    internal static string? ShutterText(ExifRational? exposure, IFormatProvider provider)
    {
        if (exposure is not { Numerator: > 0, Denominator: > 0 } time) return null;
        if (time.Numerator == 1 && time.Denominator > 1)
            return Tr.ExifShutterFraction(time.Denominator.ToString(provider));
        var seconds = time.Value;
        if (seconds < 0.5)
        {
            var denominator = Math.Round(1 / seconds);
            return Tr.ExifShutterFraction(denominator.ToString("0", provider));
        }
        return Tr.ExifShutterSeconds(seconds.ToString("0.#", provider));
    }
}
