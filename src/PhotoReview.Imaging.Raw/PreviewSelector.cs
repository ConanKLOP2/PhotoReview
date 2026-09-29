using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Helper to select the most appropriate embedded preview from a RAW container.
/// Follows RAW-21 / TASKS.md:
/// Among JPEG previews, select the smallest with both sides &gt;= requested box (after orientation transpose),
/// else the largest preview.
/// Unknown preview sizes (Width == 0 or Height == 0) are resolved by walking the preview's JPEG marker segments with bounded per-segment reads (SOF may sit past 64 KB, e.g. Fujifilm RAF),
/// largest-first, stopping as soon as the choice is certain.
/// </summary>
public static class PreviewSelector
{
    private const int MaxJpegSegments = 512;

    /// <summary>
    /// Selects the best embedded preview matching <paramref name="requestBox"/> and <paramref name="orientation"/>.
    /// </summary>
    public static EmbeddedPreview? SelectPreview(
        IRawHeaderSource source,
        IReadOnlyList<EmbeddedPreview> previews,
        DecodeBox requestBox,
        int orientation)
    {
        if (previews == null || previews.Count == 0)
            return null;

        // Filter to JPEG previews (Kind == Jpeg)
        var jpegPreviews = previews.Where(p => p.Kind == EmbeddedPreviewKind.Jpeg).ToList();
        if (jpegPreviews.Count == 0)
        {
            // Fallback to any preview
            return previews[0];
        }

        if (jpegPreviews.Count == 1)
        {
            return ResolveDimensions(source, jpegPreviews[0]);
        }

        // Resolve dimensions for candidates (largest byte length first)
        // If dimensions are missing (0,0), walk the JPEG segments to the SOF
        var resolved = new List<EmbeddedPreview>(jpegPreviews.Count);
        foreach (var p in jpegPreviews.OrderByDescending(p => p.Length))
        {
            resolved.Add(ResolveDimensions(source, p));
        }

        bool isTransposed = ExifOrientation.IsTransposed(orientation);

        // If requestBox is unbounded (or <= 0), pick largest
        if (requestBox.IsUnbounded || (requestBox.Width <= 0 && requestBox.Height <= 0))
        {
            return resolved.OrderByDescending(p => (long)p.Width * p.Height).ThenByDescending(p => p.Length).First();
        }

        int reqW = requestBox.Width;
        int reqH = requestBox.Height;

        // Smallest preview where both visual sides >= requested box
        var candidates = resolved
            .Where(p =>
            {
                int visW = isTransposed ? p.Height : p.Width;
                int visH = isTransposed ? p.Width : p.Height;

                if (reqW > 0 && reqH > 0)
                    return visW >= reqW && visH >= reqH;
                if (reqW > 0)
                    return visW >= reqW;
                if (reqH > 0)
                    return visH >= reqH;
                return true;
            })
            .OrderBy(p => (long)p.Width * p.Height)
            .ThenBy(p => p.Length)
            .ToList();

        if (candidates.Count > 0)
            return candidates[0];

        // Else largest available preview
        return resolved.OrderByDescending(p => (long)p.Width * p.Height).ThenByDescending(p => p.Length).First();
    }

    private static EmbeddedPreview ResolveDimensions(IRawHeaderSource source, EmbeddedPreview preview)
    {
        if (preview.Width > 0 && preview.Height > 0)
            return preview;

        if (preview.Offset < 0 || preview.Offset >= source.Length || preview.Length < 4)
            return preview;

        if (TryReadJpegFrame(source, preview.Offset, preview.Length, out int width, out int height, out var colorSpace))
        {
            return preview with
            {
                Width = width,
                Height = height,
                ColorSpace = colorSpace != PreviewColorSpace.Unknown ? colorSpace : preview.ColorSpace
            };
        }

        return preview;
    }

    /// <summary>
    /// Walks the JPEG marker segments of the byte range <paramref name="offset"/>..+<paramref name="length"/> with
    /// bounded per-segment reads, so a frame header located anywhere in the range (Fuji RAF: after a ~65 KB EXIF APP1)
    /// is found without buffering the range. Accepts SOF0/1/2 only; lossless (SOF3), arithmetic-coded or hierarchical
    /// frames, malformed markers, truncation and SOS/EOI before a frame header yield <c>false</c> (dimensions unknown).
    /// At most <see cref="MaxJpegSegments"/> segments are visited. Never reads past the source or the range.
    /// </summary>
    public static bool TryReadJpegFrame(IRawHeaderSource source, long offset, long length, out int width, out int height, out PreviewColorSpace colorSpace)
    {
        width = 0;
        height = 0;
        colorSpace = PreviewColorSpace.Unknown;

        if (length < 4 || offset < 0 || offset >= source.Length) return false;

        long end = Math.Min(offset + length, source.Length);
        if (end - offset < 4 || source.Read(offset, 2) is not [0xFF, 0xD8]) return false;

        long pos = offset + 2;
        for (int segment = 0; segment < MaxJpegSegments && pos + 4 <= end; segment++)
        {
            var head = source.Read(pos, 4);
            if (head[0] != 0xFF) break;

            byte marker = head[1];
            if (marker == 0xFF) { pos++; continue; } // fill byte
            if (marker == 0x00 || marker is 0xDA or 0xD9) break; // stuffed / SOS / EOI
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD8) { pos += 2; continue; } // standalone markers

            int segLen = (head[2] << 8) | head[3];
            if (segLen < 2 || pos + 2 + segLen > end) break;

            int payloadLen = segLen - 2;
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (marker > 0xC2 || payloadLen < 5) return false;
                var frame = source.Read(pos + 4, 5); // precision, height (2), width (2)
                height = (frame[1] << 8) | frame[2];
                width = (frame[3] << 8) | frame[4];
                return width > 0 && height > 0;
            }

            if (marker == 0xE1 && payloadLen >= 14 && colorSpace == PreviewColorSpace.Unknown) // APP1 EXIF
            {
                var payload = source.Read(pos + 4, payloadLen);
                if (payload.IndexOf("Adobe RGB"u8) >= 0 || payload.IndexOf("R03"u8) >= 0)
                    colorSpace = PreviewColorSpace.AdobeRgb;
            }

            pos += 2 + segLen;
        }

        return false;
    }

    /// <summary>
    /// Reads JPEG SOF markers to extract pixel dimensions and checks APP1/APP2 for color space hints.
    /// </summary>
    public static bool TryExtractJpegDimensions(ReadOnlySpan<byte> span, out int width, out int height, out PreviewColorSpace colorSpace)
    {
        width = 0;
        height = 0;
        colorSpace = PreviewColorSpace.Unknown;

        if (span.Length < 4 || span[0] != 0xFF || span[1] != 0xD8)
            return false;

        int offset = 2;
        while (offset + 4 <= span.Length)
        {
            if (span[offset] != 0xFF) return false;
            byte marker = span[offset + 1];
            if (marker == 0xFF) { offset++; continue; }
            if (marker == 0x00) return false;
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { offset += 2; continue; }
            if (marker is 0xDA or 0xD9) break; // SOS or EOI

            int segLen = (span[offset + 2] << 8) | span[offset + 3];
            if (segLen < 2 || offset + 2 + segLen > span.Length) break;

            int payloadOffset = offset + 4;
            int payloadLen = segLen - 2;

            // SOF0 (0xC0), SOF1 (0xC1), SOF2 (0xC2)
            if (marker is 0xC0 or 0xC1 or 0xC2 && payloadLen >= 5)
            {
                // [precision 1 byte][height 2 bytes][width 2 bytes]
                height = (span[payloadOffset + 1] << 8) | span[payloadOffset + 2];
                width = (span[payloadOffset + 3] << 8) | span[payloadOffset + 4];
            }
            else if (marker == 0xE1 && payloadLen >= 14) // APP1 EXIF
            {
                var payload = span.Slice(payloadOffset, payloadLen);
                if (payload.IndexOf("Adobe RGB"u8) >= 0 || payload.IndexOf("R03"u8) >= 0)
                {
                    colorSpace = PreviewColorSpace.AdobeRgb;
                }
            }

            offset += 2 + segLen;
            if (width > 0 && height > 0 && colorSpace != PreviewColorSpace.Unknown)
                break;
        }

        return width > 0 && height > 0;
    }
}
