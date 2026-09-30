using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Helper to select the most appropriate embedded preview from a RAW container.
/// Follows RAW-21 (ADR 0009, docs/adr/0009-camera-raw-support.md):
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
        var chosen = SelectPreviewCore(source, previews, requestBox, orientation);
        return chosen is { Kind: EmbeddedPreviewKind.Jpeg } ? ResolveColorSpace(source, chosen) : chosen;
    }

    private static EmbeddedPreview? SelectPreviewCore(
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
    public static bool TryReadJpegFrame(IRawHeaderSource source, long offset, long length, out int width, out int height, out PreviewColorSpace colorSpace) =>
        WalkJpeg(source, offset, length, needFrame: true, out width, out height, out colorSpace);

    /// <summary>
    /// The container may declare the preview size (CR3 PRVW, CR2/DNG IFDs) yet say nothing about its colour space, so the
    /// EXIF interoperability marker is still looked up. Same bounded per-segment walk, stopping at the frame header
    /// (the EXIF APP1 precedes it), so no extra full-JPEG read happens.
    /// </summary>
    private static EmbeddedPreview ResolveColorSpace(IRawHeaderSource source, EmbeddedPreview preview)
    {
        if (preview.ColorSpace != PreviewColorSpace.Unknown
            || preview.Offset < 0 || preview.Offset >= source.Length || preview.Length < 4)
            return preview;

        WalkJpeg(source, preview.Offset, preview.Length, needFrame: false, out _, out _, out var colorSpace);
        return colorSpace != PreviewColorSpace.Unknown ? preview with { ColorSpace = colorSpace } : preview;
    }

    private static bool WalkJpeg(IRawHeaderSource source, long offset, long length, bool needFrame, out int width, out int height, out PreviewColorSpace colorSpace)
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
                if (!needFrame) return false; // colour space only: the EXIF APP1 always precedes the frame header
                if (marker > 0xC2 || payloadLen < 5) return false;
                var frame = source.Read(pos + 4, 5); // precision, height (2), width (2)
                height = (frame[1] << 8) | frame[2];
                width = (frame[3] << 8) | frame[4];
                return width > 0 && height > 0;
            }

            if (marker == 0xE1 && payloadLen >= 14 && colorSpace == PreviewColorSpace.Unknown) // APP1 EXIF
            {
                var payload = source.Read(pos + 4, payloadLen);
                if (IsAdobeRgbExif(payload))
                {
                    colorSpace = PreviewColorSpace.AdobeRgb;
                    if (!needFrame) return false; // colour space found and no frame requested
                }
            }

            pos += 2 + segLen;
        }

        return false;
    }

    /// <summary>
    /// True when the EXIF APP1 payload ("Exif\0\0" + TIFF) marks Adobe RGB the standard way: the Interoperability IFD
    /// (Exif IFD pointer 0x8769 -> Interop pointer 0xA005) has InteropIndex (0x0001) "R03" (DCF option file / Adobe RGB).
    /// Only that IFD entry counts: a byte search over the payload also hit MakerNote binary at random (~0.4 % per 64 KB).
    /// Every offset is bounds-checked against the payload; malformed data yields <c>false</c>.
    /// </summary>
    internal static bool IsAdobeRgbExif(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 14 || !payload[..6].SequenceEqual("Exif\0\0"u8)) return false;
        var tiff = payload[6..];
        bool little;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I') little = true;
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M') little = false;
        else return false;
        if (ReadU16(tiff, 2, little) != 42) return false;

        var ifd0 = ReadU32(tiff, 4, little);
        if (!TryFindLongEntry(tiff, ifd0, 0x8769, little, out var exifIfd)) return false;
        if (!TryFindLongEntry(tiff, exifIfd, 0xA005, little, out var interopIfd)) return false;
        return TryFindEntry(tiff, interopIfd, 0x0001, little, out var entryOffset)
            && ReadU16(tiff, entryOffset + 2, little) == 2 // ASCII
            && ReadU32(tiff, entryOffset + 4, little) == 4 // "R03\0" fits the 4-byte inline value
            && entryOffset + 12 <= tiff.Length
            && tiff.Slice((int)entryOffset + 8, 4).SequenceEqual("R03\0"u8);
    }

    private static bool TryFindLongEntry(ReadOnlySpan<byte> tiff, long ifdOffset, ushort tag, bool little, out long value)
    {
        value = 0;
        if (!TryFindEntry(tiff, ifdOffset, tag, little, out var entryOffset)) return false;
        var type = ReadU16(tiff, entryOffset + 2, little);
        if (type != 4 || ReadU32(tiff, entryOffset + 4, little) != 1) return false; // LONG x 1
        value = ReadU32(tiff, entryOffset + 8, little);
        return true;
    }

    private static bool TryFindEntry(ReadOnlySpan<byte> tiff, long ifdOffset, ushort tag, bool little, out long entryOffset)
    {
        entryOffset = 0;
        if (ifdOffset < 8 || ifdOffset + 2 > tiff.Length) return false;
        var count = ReadU16(tiff, ifdOffset, little);
        for (var i = 0; i < count; i++)
        {
            var entry = ifdOffset + 2 + 12L * i;
            if (entry + 12 > tiff.Length) return false;
            if (ReadU16(tiff, entry, little) != tag) continue;
            entryOffset = entry;
            return true;
        }
        return false;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, long offset, bool little) =>
        little ? (ushort)(data[(int)offset] | (data[(int)offset + 1] << 8)) : (ushort)((data[(int)offset] << 8) | data[(int)offset + 1]);

    private static uint ReadU32(ReadOnlySpan<byte> data, long offset, bool little)
    {
        var o = (int)offset;
        return little
            ? (uint)(data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24))
            : (uint)((data[o] << 24) | (data[o + 1] << 16) | (data[o + 2] << 8) | data[o + 3]);
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
                if (IsAdobeRgbExif(span.Slice(payloadOffset, payloadLen)))
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
