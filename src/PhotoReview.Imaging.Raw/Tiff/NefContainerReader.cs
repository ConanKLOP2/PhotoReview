using System.IO;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Raw.Tiff;

/// <summary>
/// Reader for Nikon NEF format (TIFF container with SubIFDs 0x014A pointing to JpgFromRaw and the raw image).
/// Orientation in IFD0. Sensor size comes from the largest non-thumbnail IFD (IFD0 is a 160x120 thumbnail).
/// Previews: JPEG pairs in IFD0/SubIFDs plus the Nikon MakerNote PreviewIFD (tag 0x0011).
/// </summary>
public sealed class NefContainerReader : IRawContainerReader
{
    public RawFormat Format => RawFormat.Nef;

    public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension)
    {
        if (!extension.Equals(".nef", StringComparison.OrdinalIgnoreCase))
            return false;

        if (first64Bytes.Length < 4) return false;

        // Check standard TIFF magic
        if ((first64Bytes[0] == 0x49 && first64Bytes[1] == 0x49 && first64Bytes[2] == 0x2A && first64Bytes[3] == 0x00) ||
            (first64Bytes[0] == 0x4D && first64Bytes[1] == 0x4D && first64Bytes[2] == 0x00 && first64Bytes[3] == 0x2A))
        {
            return true;
        }

        return false;
    }

    public RawContainerInfo Read(IRawHeaderSource source, CancellationToken ct)
    {
        if (source.Length < 16)
            throw new InvalidDataException("File too short for NEF container.");

        var headerSpan = source.Read(0, 16);
        if (!TiffStructure.TryReadHeader(headerSpan, out bool littleEndian, out ushort magic, out uint ifd0Offset) || magic != 42)
            throw new InvalidDataException("Invalid NEF TIFF header.");

        int orientation = 1;
        var previews = new List<EmbeddedPreview>();
        var exifBlocks = new List<ExifBlock>();

        var pendingIfds = new Queue<long>();
        pendingIfds.Enqueue(ifd0Offset);
        var visited = new HashSet<long>();
        long? makerNoteOffset = null;
        int ifdCount = 0;
        int ifd0Width = 0;
        int ifd0Height = 0;
        long rawArea = 0;
        int sensorWidth = 0;
        int sensorHeight = 0;
        int cropWidth = 0;
        int cropHeight = 0;
        int exifPixelWidth = 0;
        int exifPixelHeight = 0;

        // IFD0 chain first, then the SubIFDs it announces (raw image, JpgFromRaw and reduced previews live there).
        while (pendingIfds.Count > 0 && ifdCount < RawContainerLimits.MaxIfdCount)
        {
            ct.ThrowIfCancellationRequested();
            long ifdOffset = pendingIfds.Dequeue();
            if (ifdOffset <= 0 || !visited.Add(ifdOffset)) continue;

            var entries = TiffHeaderNavigator.ReadIfdEntries(source, ifdOffset, littleEndian, out uint nextIfdOffset);
            bool isIfd0 = ifdCount == 0;
            ifdCount++;

            TiffHeaderNavigator.ReadImageSize(source, entries, littleEndian, out int width, out int height);
            uint subFileType = (uint)(TiffHeaderNavigator.ReadTagValue(source, entries, 0x00FE, littleEndian) ?? 0);
            long compression = TiffHeaderNavigator.ReadTagValue(source, entries, 0x0103, littleEndian) ?? 1;

            if (isIfd0)
            {
                if (TiffHeaderNavigator.ReadTagValue(source, entries, 0x0112, littleEndian) is >= 1 and <= 8 and var o)
                    orientation = (int)o;
                ifd0Width = width;
                ifd0Height = height;
            }

            if (TiffHeaderNavigator.TryGetEntry(entries, 0x014A, out var subIfdEntry))
            {
                foreach (long sub in TiffHeaderNavigator.ReadTagUnsignedArray(source, subIfdEntry, littleEndian))
                    pendingIfds.Enqueue(sub);
            }

            if (TiffHeaderNavigator.TryGetEntry(entries, 0x8769, out var exifEntry) &&
                TiffHeaderNavigator.ReadTagUnsigned(source, exifEntry, littleEndian) is { } exifOffset && exifOffset > 0)
            {
                foreach (var exifTag in TiffHeaderNavigator.ReadIfdEntries(source, exifOffset, littleEndian, out _))
                {
                    if (exifTag.Tag == 0x927C && exifTag.Count > 4)
                        makerNoteOffset ??= exifTag.ValueOrOffset;
                }

                if (exifPixelWidth == 0 &&
                    TiffHeaderNavigator.TryReadExifPixelDimensions(source, exifOffset, littleEndian, out int pixelWidth, out int pixelHeight))
                {
                    exifPixelWidth = pixelWidth;
                    exifPixelHeight = pixelHeight;
                }
            }

            bool isPreview = false;
            if (TiffHeaderNavigator.TryReadJpegInterchange(source, entries, littleEndian, out long jpegOffset, out long jpegLength))
            {
                int previewWidth = width;
                int previewHeight = height;
                TiffHeaderNavigator.ReconcileJpegSize(source, jpegOffset, jpegLength, ref previewWidth, ref previewHeight);
                AddPreview(previews, jpegOffset, jpegLength, previewWidth, previewHeight);
                isPreview = true;
            }
            else if (compression is 6 or 7 &&
                     TiffHeaderNavigator.TryReadSingleStrip(source, entries, littleEndian, out long stripOffset, out long stripLength) &&
                     JpegMarkerProbe.TryReadLossyFrame(source, stripOffset, stripLength, out int jpegWidth, out int jpegHeight))
            {
                AddPreview(previews, stripOffset, stripLength, jpegWidth, jpegHeight); // the probed frame size wins over a declared IFD size
                isPreview = true;
            }

            // The raw image is the largest IFD that is neither a JPEG preview nor a reduced-resolution image
            // (in real NEFs IFD0 is a 160x120 thumbnail and the sensor size lives in a SubIFD).
            long area = (long)width * height;
            if (!isPreview && (subFileType & 1) == 0 && area > rawArea)
            {
                rawArea = area;
                sensorWidth = width;
                sensorHeight = height;
                TiffHeaderNavigator.TryReadDefaultCropSize(source, entries, littleEndian, out cropWidth, out cropHeight);
            }

            if (nextIfdOffset > 0)
                pendingIfds.Enqueue(nextIfdOffset);
        }

        if (sensorWidth == 0)
        {
            sensorWidth = ifd0Width;
            sensorHeight = ifd0Height;
        }

        if (makerNoteOffset is > 0)
            ParseNikonMakerNotePreview(source, makerNoteOffset.Value, previews);

        // The raw IFD size includes the masked margins (Z 7: 8288x5520 vs the 8256x5504 active area). Prefer
        // DefaultCropSize, then the Exif pixel size, then the full-size JpgFromRaw frame (Nikon writes it at the active
        // area size and no Exif pixel size), and only then the raw IFD size.
        (sensorWidth, sensorHeight) = TiffHeaderNavigator.ChooseActiveSensorSize(
            sensorWidth, sensorHeight, cropWidth, cropHeight, exifPixelWidth, exifPixelHeight);
        if (cropWidth == 0 && exifPixelWidth == 0 &&
            TryReadFullSizeJpegFrame(source, previews, sensorWidth, sensorHeight, out int jpegFrameWidth, out int jpegFrameHeight))
        {
            sensorWidth = jpegFrameWidth;
            sensorHeight = jpegFrameHeight;
        }

        exifBlocks.Add(TiffHeaderNavigator.ComputeExifBlock(source, littleEndian, ifd0Offset));

        return new RawContainerInfo(
            RawFormat.Nef,
            sensorWidth,
            sensorHeight,
            orientation,
            previews,
            exifBlocks);
    }

    /// <summary>Largest masked margin (pixels per axis) between the raw IFD and a JpgFromRaw frame that still counts as the same image.</summary>
    private const int MaxMaskedMargin = 256;

    /// <summary>
    /// Finds the largest embedded JPEG whose frame is at most <see cref="MaxMaskedMargin"/> pixels smaller than the raw IFD
    /// size on both axes (a full-size JpgFromRaw), reading its dimensions from the SOF header when the container gave none.
    /// </summary>
    private static bool TryReadFullSizeJpegFrame(IRawHeaderSource source, List<EmbeddedPreview> previews, int rawWidth, int rawHeight, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (rawWidth <= 0 || rawHeight <= 0) return false;

        long bestArea = 0;
        foreach (var preview in previews.OrderByDescending(p => p.Length).Take(4))
        {
            int w = preview.Width;
            int h = preview.Height;
            if (w <= 0 || h <= 0)
            {
                bool read;
                try
                {
                    read = PreviewSelector.TryReadJpegFrame(source, preview.Offset, preview.Length, out w, out h, out _);
                }
                catch (InvalidDataException ex) when (!RawHeaderErrors.IsIoFailure(ex))
                {
                    continue; // exhausted header budget: this preview cannot confirm a full-size frame
                }

                if (!read) continue;
            }

            int marginX = rawWidth - w;
            int marginY = rawHeight - h;
            if (marginX is < 0 or > MaxMaskedMargin || marginY is < 0 or > MaxMaskedMargin) continue;

            long area = (long)w * h;
            if (area > bestArea)
            {
                bestArea = area;
                width = w;
                height = h;
            }
        }

        return bestArea > 0;
    }

    private static void AddPreview(List<EmbeddedPreview> previews, long offset, long length, int width, int height)
    {
        if (previews.Any(p => p.Offset == offset)) return;
        previews.Add(new EmbeddedPreview(
            Index: previews.Count,
            Offset: offset,
            Length: length,
            Kind: EmbeddedPreviewKind.Jpeg,
            Width: width,
            Height: height,
            ColorSpace: PreviewColorSpace.Unknown));
    }

    /// <summary>
    /// Nikon type-3 MakerNote: "Nikon\0" + version + 2 pad bytes, then an embedded TIFF header at +10; every offset
    /// inside it (including the PreviewIFD pointer, tag 0x0011, and its 0x0201/0x0202 pair) is relative to that header.
    /// </summary>
    private static void ParseNikonMakerNotePreview(IRawHeaderSource source, long makerNoteOffset, List<EmbeddedPreview> previews)
    {
        const int TiffHeaderDelta = 10;
        if (makerNoteOffset < 8 || makerNoteOffset > source.Length - 18) return;

        var head = source.Read(makerNoteOffset, 18);
        ReadOnlySpan<byte> signature = "Nikon\0"u8;
        if (!head[..6].SequenceEqual(signature) || head[6] != 2) return;

        long tiffBase = makerNoteOffset + TiffHeaderDelta;
        var tiffHeader = head[TiffHeaderDelta..];
        if (!TiffStructure.TryReadHeader(tiffHeader, out bool noteLittleEndian, out ushort magic, out uint ifdOffset) || magic != 42)
            return;

        var noteEntries = TiffHeaderNavigator.ReadIfdEntries(source, tiffBase + ifdOffset, noteLittleEndian, out _);
        if (!TiffHeaderNavigator.TryGetEntry(noteEntries, 0x0011, out var previewIfdEntry) ||
            TiffHeaderNavigator.ReadTagUnsigned(source, previewIfdEntry, noteLittleEndian) is not { } previewIfdOffset)
            return;

        var previewEntries = TiffHeaderNavigator.ReadIfdEntries(source, tiffBase + previewIfdOffset, noteLittleEndian, out _);
        if (TiffHeaderNavigator.ReadTagValue(source, previewEntries, 0x0201, noteLittleEndian) is not { } start ||
            TiffHeaderNavigator.ReadTagValue(source, previewEntries, 0x0202, noteLittleEndian) is not { } length)
            return;

        long absoluteStart = tiffBase + start;
        if (!TiffHeaderNavigator.IsRangeInFile(absoluteStart, length, source.Length) ||
            source.Read(absoluteStart, 2) is not [0xFF, 0xD8])
            return;

        AddPreview(previews, absoluteStart, length, 0, 0);
    }
}
