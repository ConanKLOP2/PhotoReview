using System.Globalization;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.App.ViewModels;

/// <summary>
/// Builds the content shown in the main window's title bar (<see cref="AppSettings.TitleBarFields"/>), e.g.
/// <c>Vacation 2024 · 12/340 · IMG_1234.jpg</c>. The result is substituted for the folder placeholder of
/// <c>main.title.withFolder</c> / <c>main.title.withFolderLogging</c> -- it never builds the "Photo Review" /
/// " · LOG" wrapper itself. Reuses <see cref="StatusFormatter"/> and <see cref="ExifFormatter"/> logic (file size,
/// EXIF text, dates) so the same value renders identically wherever it appears.
/// </summary>
public static class TitleBarFormatter
{
    /// <summary>Same separator the EXIF line uses between its parts.</summary>
    public const string Separator = ExifFormatter.Separator;

    /// <param name="fields">Parts to include (settings), in the fixed order documented on <see cref="TitleBarFields"/>.</param>
    /// <param name="folderPath">Full path of the open folder; the caller has already checked it is non-blank.</param>
    /// <param name="index">0-based index of the presented photo in the catalog; null = none presented (e.g. an empty folder).</param>
    /// <param name="count">Number of photos in the catalog; null/0 = none.</param>
    /// <param name="fileName">Presented photo's file name (<see cref="ImagePresenter.CurrentPhotoInfo"/>); null = none presented.</param>
    /// <param name="length">Presented photo's size in bytes, from the catalog entry (no extra disk read); null = unknown.</param>
    /// <param name="width">Original (post-orientation) width; 0 = unknown.</param>
    /// <param name="height">Original (post-orientation) height; 0 = unknown.</param>
    /// <param name="modifiedUtc">Presented photo's last-write time, from the catalog entry (no extra disk read); null = unknown.</param>
    /// <param name="exif">EXIF read during the decode; null = none (e.g. an older cache entry).</param>
    /// <param name="provider">Number/date format; defaults to <see cref="CultureInfo.CurrentCulture"/>.</param>
    public static string Format(TitleBarFields fields, string folderPath, int? index, int? count, string? fileName,
        long? length, int width, int height, DateTime? modifiedUtc, ExifSummary? exif, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        var parts = new List<string>(14);

        if (fields.HasFlag(TitleBarFields.FolderName) && !string.IsNullOrWhiteSpace(folderPath))
            parts.Add(System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folderPath)));
        if (fields.HasFlag(TitleBarFields.FolderPath) && !string.IsNullOrWhiteSpace(folderPath))
            parts.Add(folderPath);
        if (fields.HasFlag(TitleBarFields.IndexCount) && index is { } idx && count is > 0)
            parts.Add(Tr.StatusPosition(idx + 1, count.Value));
        if (fields.HasFlag(TitleBarFields.FileName) && !string.IsNullOrWhiteSpace(fileName))
            parts.Add(fileName);
        if (fields.HasFlag(TitleBarFields.FileSize) && length is { } len)
            parts.Add(StatusFormatter.FormatFileSize(len));
        if (fields.HasFlag(TitleBarFields.Dimensions) && width > 0 && height > 0)
            parts.Add(Tr.ExifDimensions(width.ToString(provider), height.ToString(provider)));
        if (fields.HasFlag(TitleBarFields.ModifiedDate) && modifiedUtc is { } modified)
            parts.Add(Tr.ExifModified(ExifFormatter.FormatDateTime(modified.ToLocalTime(), provider)));
        if (fields.HasFlag(TitleBarFields.DateTaken) && exif?.DateTaken is { } date)
            parts.Add(Tr.ExifDateTaken(ExifFormatter.FormatDateTime(date, provider)));
        if (fields.HasFlag(TitleBarFields.Camera) && ExifFormatter.CameraText(exif?.CameraMake, exif?.CameraModel) is { } camera)
            parts.Add(camera);
        if (fields.HasFlag(TitleBarFields.Lens) && exif?.LensModel is { } lens)
            parts.Add(lens);
        if (fields.HasFlag(TitleBarFields.Iso) && exif?.Iso is > 0 and var iso)
            parts.Add(Tr.ExifIso(iso.ToString(provider)));
        if (fields.HasFlag(TitleBarFields.FocalLength) && exif?.FocalLength is { Value: > 0 } focal)
            parts.Add(Tr.ExifFocalLength(focal.Value.ToString("0.#", provider)));
        if (fields.HasFlag(TitleBarFields.Aperture) && exif?.FNumber is { Value: > 0 } aperture)
            parts.Add(Tr.ExifAperture(aperture.Value.ToString("0.#", provider)));
        if (fields.HasFlag(TitleBarFields.ShutterSpeed) && ExifFormatter.ShutterText(exif?.ExposureTime, provider) is { } shutter)
            parts.Add(shutter);

        return string.Join(Separator, parts);
    }
}
