using System.IO;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Helper to select the most appropriate embedded preview from a RAW container.
/// Follows RAW-21 (ADR 0009, docs/adr/0009-camera-raw-support.md):
/// Among JPEG previews, select the smallest with both sides &gt;= requested box (after orientation transpose),
/// else the largest preview.
/// Unknown preview sizes (Width == 0 or Height == 0) are resolved by walking the preview's JPEG marker segments with bounded per-segment reads (SOF may sit past 64 KB, e.g. Fujifilm RAF),
/// in largest-byte-length order. Every unknown-size candidate is resolved, because neither the byte length nor a resolved sibling bounds the pixel size of another candidate (a heavily compressed JPEG can be the largest by pixels), so no early stop is certain; the cost is paid once per file because <see cref="EmbeddedPreview.HeaderResolved"/> previews are cached with the container info.
/// </summary>
public static class PreviewSelector
{
    private const int MaxJpegSegments = 512;

    /// <summary>
    /// A preview whose pixel size could not be determined (lossless/arithmetic-coded frame, truncated header) would score an area
    /// of 0 and always lose to any tiny known thumbnail, even when it is by far the largest JPEG in the file. Its size is
    /// therefore estimated from its byte length at this many pixels per byte (about 0.25 byte per pixel, the compression of a
    /// typical camera JPEG), which keeps it comparable with known thumbnail areas: a 4 MB unknown-size JPEG outranks a 160x120 thumbnail
    /// but never a known preview of at least <see cref="ViewablePreviewLongSide"/> pixels (see <see cref="Score"/>). Used only as a ranking score, never reported as dimensions.
    /// </summary>
    private const long UnknownAreaPerByte = 4;

    /// <summary>A known-size preview with a long side of at least this many pixels is viewable and always outranks unknown-size entries.</summary>
    private const int ViewablePreviewLongSide = 1000;

    /// <summary>Fill bytes (extra 0xFF before a marker) are skipped without using a segment iteration, up to this many in a row.</summary>
    private const int MaxFillBytes = 64 * 1024;

    /// <summary>
    /// Selects the best embedded preview matching <paramref name="requestBox"/> and <paramref name="orientation"/>.
    /// </summary>
    public static EmbeddedPreview? SelectPreview(
        IRawHeaderSource source,
        IReadOnlyList<EmbeddedPreview> previews,
        DecodeBox requestBox,
        int orientation) =>
        SelectPreview(source, previews, requestBox, orientation, out _);

    /// <summary>
    /// As above; <paramref name="resolvedPreviews"/> is <paramref name="previews"/> with every preview whose JPEG header was
    /// walked during the selection replaced by its resolved copy (dimensions, colour space, <see cref="EmbeddedPreview.HeaderResolved"/>),
    /// in the same order. A caller that caches the container info stores this list so the walk is never repeated.
    /// The dimension walk and the colour-space lookup share ONE pass over the JPEG header.
    /// </summary>
    public static EmbeddedPreview? SelectPreview(
        IRawHeaderSource source,
        IReadOnlyList<EmbeddedPreview> previews,
        DecodeBox requestBox,
        int orientation,
        out IReadOnlyList<EmbeddedPreview> resolvedPreviews)
    {
        var state = previews is null ? [] : previews.ToList();
        var chosen = SelectPreviewCore(source, state, requestBox, orientation);
        if (chosen is { Kind: EmbeddedPreviewKind.Jpeg })
            chosen = ResolveColorSpace(source, chosen, state);
        resolvedPreviews = state;
        return chosen;
    }

    private static void Replace(List<EmbeddedPreview> state, EmbeddedPreview original, EmbeddedPreview resolved)
    {
        if (ReferenceEquals(original, resolved)) return;
        int index = state.FindIndex(p => ReferenceEquals(p, original));
        if (index >= 0) state[index] = resolved;
    }

    private static EmbeddedPreview? SelectPreviewCore(
        IRawHeaderSource source,
        List<EmbeddedPreview> previews,
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
            return ResolveDimensions(source, jpegPreviews[0], previews);
        }

        // Resolve dimensions for every candidate (largest byte length first). There is deliberately no early stop:
        // the candidate chosen depends on the pixel size of ALL candidates (smallest one fitting the box, or the largest
        // area), which no byte-length order can bound. Known sizes cost nothing; walked headers are cached by the caller.
        // If dimensions are missing (0,0), walk the JPEG segments to the SOF
        var resolved = new List<EmbeddedPreview>(jpegPreviews.Count);
        foreach (var p in jpegPreviews.OrderByDescending(p => p.Length))
        {
            resolved.Add(ResolveDimensions(source, p, previews));
        }

        bool isTransposed = ExifOrientation.IsTransposed(orientation);

        // If requestBox is unbounded (or <= 0), pick largest
        if (requestBox.IsUnbounded || (requestBox.Width <= 0 && requestBox.Height <= 0))
        {
            return resolved.OrderByDescending(Score).ThenByDescending(p => p.Length).First();
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
            .OrderBy(Score)
            .ThenBy(p => p.Length)
            .ToList();

        if (candidates.Count > 0)
            return candidates[0];

        // Else largest available preview
        return resolved.OrderByDescending(Score).ThenByDescending(p => p.Length).First();
    }

    /// <summary>
    /// Ranking key (ordered by tier, then value). A known-size preview whose long side is at least
    /// <see cref="ViewablePreviewLongSide"/> pixels is tier 1 and ranks by its pixel area. Everything else is tier 0:
    /// a known tiny thumbnail ranks by its area and an unknown-size entry by an estimate from its byte length
    /// (see <see cref="UnknownAreaPerByte"/>), so a large unknown-size JPEG still beats a tiny thumbnail and unknown-size
    /// entries rank among themselves by bytes. An unknown-size entry can therefore never outrank a known viewable preview
    /// (a 7 MB unknown entry used to score 28M against a known 6000x4000-class preview and was chosen even though it could
    /// not be decoded).
    /// </summary>
    private static (int Tier, long Value) Score(EmbeddedPreview preview)
    {
        if (preview.Width > 0 && preview.Height > 0)
        {
            long area = (long)preview.Width * preview.Height;
            return (Math.Max(preview.Width, preview.Height) >= ViewablePreviewLongSide ? 1 : 0, area);
        }

        return (0, preview.Length <= 0 ? 0 : preview.Length * UnknownAreaPerByte);
    }

    private static EmbeddedPreview ResolveDimensions(IRawHeaderSource source, EmbeddedPreview preview, List<EmbeddedPreview> state)
    {
        if (preview.HeaderResolved || (preview.Width > 0 && preview.Height > 0))
            return preview;

        if (preview.Offset < 0 || preview.Offset >= source.Length || preview.Length < 4)
            return preview;

        bool resolved = false;
        bool walked = true;
        int width = 0;
        int height = 0;
        var colorSpace = PreviewColorSpace.Unknown;
        try
        {
            resolved = TryReadJpegFrame(source, preview.Offset, preview.Length, out width, out height, out colorSpace);
        }
        catch (InvalidDataException ex) when (!RawHeaderErrors.IsIoFailure(ex))
        {
            // Exhausted header budget or unreadable range: the dimensions stay unknown instead of failing the decode.
            walked = false;
        }

        // Even when the frame size cannot be read (lossless/arithmetic frame), the walk has looked at every APP1 before the
        // frame header, so a colour space it found is final and the preview is marked resolved. A failed walk (I/O or budget
        // exhaustion) leaves the preview untouched so a later call may try again.
        if (!walked)
            return preview;

        var result = preview with
        {
            Width = resolved ? width : preview.Width,
            Height = resolved ? height : preview.Height,
            ColorSpace = colorSpace != PreviewColorSpace.Unknown ? colorSpace : preview.ColorSpace,
            HeaderResolved = true,
        };
        Replace(state, preview, result);
        return result;
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
    private static EmbeddedPreview ResolveColorSpace(IRawHeaderSource source, EmbeddedPreview preview, List<EmbeddedPreview> state)
    {
        if (preview.HeaderResolved || preview.ColorSpace != PreviewColorSpace.Unknown
            || preview.Offset < 0 || preview.Offset >= source.Length || preview.Length < 4)
            return preview;

        PreviewColorSpace colorSpace;
        try
        {
            WalkJpeg(source, preview.Offset, preview.Length, needFrame: false, out _, out _, out colorSpace);
        }
        catch (InvalidDataException ex) when (!RawHeaderErrors.IsIoFailure(ex))
        {
            // Exhausted header budget: the colour space stays unknown instead of failing the decode.
            return preview;
        }

        var result = preview with
        {
            ColorSpace = colorSpace != PreviewColorSpace.Unknown ? colorSpace : preview.ColorSpace,
            HeaderResolved = true,
        };
        Replace(state, preview, result);
        return result;
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
        int fillBytes = 0;
        for (int segment = 0; segment < MaxJpegSegments && pos + 4 <= end; segment++)
        {
            var head = source.Read(pos, 4);
            if (head[0] != 0xFF) break;

            byte marker = head[1];
            if (marker == 0xFF)
            {
                // Fill byte: does not consume a segment iteration (only the separate fill cap bounds it).
                if (++fillBytes > MaxFillBytes) break;
                pos++;
                segment--;
                continue;
            }
            fillBytes = 0;
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
