using System.IO;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Extracts an <see cref="ExifSummary"/> from a parsed RAW container's <see cref="ExifBlock"/>s
/// using header bytes already read or requested through <see cref="IRawHeaderSource"/>.
/// Fulfills RAW-15 without performing a redundant whole-file read.
/// </summary>
public static class RawExif
{
    private const int MaxBlockReadBytes = RawContainerLimits.MaxExifBlockBytes;

    /// <summary>
    /// Attempts to extract an <see cref="ExifSummary"/> from the container's EXIF blocks.
    /// Iterates through <see cref="RawContainerInfo.ExifBlocks"/>:
    /// - For TIFF blocks (<see cref="ExifBlock.IsTiffHeader"/> = true), reads the block span and parses via <see cref="ExifParser.TryParseTiffBlock"/>.
    /// - For JPEG preview blocks (e.g. RAF/RW2 where <see cref="ExifBlock.IsTiffHeader"/> = false), reads the leading bytes and parses via <see cref="ExifParser.TryParseJpeg"/>.
    /// Summaries of several blocks are merged (first non-null value per field wins, in block order): a Canon CR3 has
    /// camera/date in CMT1 and the exposure fields in CMT2 (<see cref="ExifBlock.IfdIsExif"/>). Later blocks are only
    /// read while the merged summary still lacks a field. The one exception is the date: CMT1 carries only DateTime (0x0132,
    /// the file change time) while CMT2 carries DateTimeOriginal (0x9003, the capture time), so a date taken from an
    /// <see cref="ExifBlock.IfdIsExif"/> block replaces one that came from a block that is not.
    /// Returns null if no valid EXIF metadata is found or on corrupt input; never throws.
    /// </summary>
    public static ExifSummary? TryReadExif(IRawHeaderSource source, RawContainerInfo containerInfo) =>
        TryReadExif(source, containerInfo, out _);

    /// <summary>
    /// As <see cref="TryReadExif(IRawHeaderSource, RawContainerInfo)"/>; <paramref name="complete"/> is false when a block could
    /// not be read (hostile data, exhausted header budget, I/O error), so a null result must not be remembered as "this file
    /// has no EXIF".
    /// </summary>
    public static ExifSummary? TryReadExif(IRawHeaderSource source, RawContainerInfo containerInfo, out bool complete)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(containerInfo);

        complete = true;
        if (containerInfo.ExifBlocks.Count == 0)
            return null;

        ExifSummary? merged = null;
        bool dateFromExifIfd = false;
        foreach (var block in containerInfo.ExifBlocks)
        {
            if (block.Offset < 0 || block.Offset >= source.Length || block.Length <= 0)
                continue;

            // Clamp to the bytes that actually exist: a block may claim to extend past EOF.
            int toRead = (int)Math.Min(Math.Min(block.Length, MaxBlockReadBytes), source.Length - block.Offset);

            ExifSummary? summary;
            try
            {
                var span = source.Read(block.Offset, toRead);
                if (span.IsEmpty)
                    continue;

                summary = block.IsTiffHeader
                    ? ExifParser.TryParseTiffBlock(span, block.IfdIsExif)
                    : ExifParser.TryParseJpeg(span);
            }
            catch (InvalidDataException)
            {
                // Hostile or truncated header data (or an exhausted read budget): documented as never throwing.
                complete = false;
                continue;
            }

            if (summary is not null && !summary.IsEmpty)
            {
                if (merged is null)
                {
                    merged = summary;
                    dateFromExifIfd = block.IfdIsExif && summary.DateTaken is not null;
                }
                else
                {
                    bool preferLaterDate = block.IfdIsExif && !dateFromExifIfd && summary.DateTaken is not null;
                    merged = Merge(merged, summary);
                    if (preferLaterDate)
                    {
                        merged = merged with { DateTaken = summary.DateTaken };
                        dateFromExifIfd = true;
                    }
                }

                if (IsComplete(merged))
                    break;
            }
        }

        return merged;
    }

    private static bool IsComplete(ExifSummary s) =>
        s.DateTaken is not null && s.CameraMake is not null && s.CameraModel is not null && s.LensModel is not null
        && s.Iso is not null && s.FocalLength is not null && s.FNumber is not null && s.ExposureTime is not null;

    private static ExifSummary Merge(ExifSummary first, ExifSummary later) => first with
    {
        DateTaken = first.DateTaken ?? later.DateTaken,
        CameraMake = first.CameraMake ?? later.CameraMake,
        CameraModel = first.CameraModel ?? later.CameraModel,
        LensModel = first.LensModel ?? later.LensModel,
        Iso = first.Iso ?? later.Iso,
        FocalLength = first.FocalLength ?? later.FocalLength,
        FNumber = first.FNumber ?? later.FNumber,
        ExposureTime = first.ExposureTime ?? later.ExposureTime,
    };
}
