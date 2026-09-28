using System.Diagnostics;
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
    private const int MaxBlockReadBytes = 128 * 1024;

    /// <summary>
    /// Attempts to extract an <see cref="ExifSummary"/> from the container's EXIF blocks.
    /// Iterates through <see cref="RawContainerInfo.ExifBlocks"/>:
    /// - For TIFF blocks (<see cref="ExifBlock.IsTiffHeader"/> = true), reads the block span and parses via <see cref="ExifParser.TryParseTiffBlock"/>.
    /// - For JPEG preview blocks (e.g. RAF/RW2 where <see cref="ExifBlock.IsTiffHeader"/> = false), reads the leading bytes and parses via <see cref="ExifParser.TryParseJpeg"/>.
    /// Returns null if no valid EXIF metadata is found or on corrupt input; never throws.
    /// </summary>
    public static ExifSummary? TryReadExif(IRawHeaderSource source, RawContainerInfo containerInfo)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(containerInfo);

        if (containerInfo.ExifBlocks.Count == 0)
            return null;

        foreach (var block in containerInfo.ExifBlocks)
        {
            if (block.Offset < 0 || block.Offset >= source.Length || block.Length <= 0)
                continue;

            int toRead = (int)Math.Min(block.Length, MaxBlockReadBytes);
            var span = source.Read(block.Offset, toRead);
            if (span.IsEmpty)
                continue;

            ExifSummary? summary = null;
            if (block.IsTiffHeader)
            {
                summary = ExifParser.TryParseTiffBlock(span);
            }
            else
            {
                summary = ExifParser.TryParseJpeg(span);
            }

            if (summary is not null && !summary.IsEmpty)
                return summary;
        }

        return null;
    }
}
