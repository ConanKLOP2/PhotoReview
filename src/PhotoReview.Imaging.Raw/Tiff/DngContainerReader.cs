using System.IO;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Raw.Tiff;

/// <summary>
/// Reader for Adobe DNG format.
/// Previews: IFD0 or SubIFDs whose JPEG frame is lossy (SOF0/1/2). Compression=7 alone is not enough because
/// lossless JPEG (SOF3) sensor data uses it too.
/// Sensor size: the IFD that is not a preview or a secondary image (reduced, mask, enhanced); IFDs with a raw
/// PhotometricInterpretation (CFA / LinearRaw) win over others, then the largest; DefaultCropSize (0xC620) or ImageWidth/Length.
/// </summary>
public sealed class DngContainerReader : IRawContainerReader
{
    public RawFormat Format => RawFormat.Dng;

    private const uint SecondaryImageMask = 0x1 | 0x4 | 0x8 | 0x10;

    public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension)
    {
        if (!extension.Equals(".dng", StringComparison.OrdinalIgnoreCase))
            return false;

        if (first64Bytes.Length < 4) return false;

        return (first64Bytes[0] == 0x49 && first64Bytes[1] == 0x49 && first64Bytes[2] == 0x2A && first64Bytes[3] == 0x00) ||
               (first64Bytes[0] == 0x4D && first64Bytes[1] == 0x4D && first64Bytes[2] == 0x00 && first64Bytes[3] == 0x2A);
    }

    public RawContainerInfo Read(IRawHeaderSource source, CancellationToken ct)
    {
        if (source.Length < 16)
            throw new InvalidDataException("File too short for DNG container.");

        var headerSpan = source.Read(0, 16);
        if (!TiffStructure.TryReadHeader(headerSpan, out bool littleEndian, out ushort magic, out uint ifd0Offset) || magic != 42)
            throw new InvalidDataException("Invalid DNG TIFF header.");

        int orientation = 1;
        int sensorWidth = 0;
        int sensorHeight = 0;
        var previews = new List<EmbeddedPreview>();
        var exifBlocks = new List<ExifBlock>();

        var allIfdOffsets = new List<long> { ifd0Offset };
        var visited = new HashSet<long>();

        // Step 1: Scan IFD chain and collect SubIFDs
        long currentIfdOffset = ifd0Offset;
        while (currentIfdOffset > 0 && allIfdOffsets.Count < RawContainerLimits.MaxIfdCount)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(currentIfdOffset)) break;

            var entries = TiffHeaderNavigator.ReadIfdEntries(source, currentIfdOffset, littleEndian, out uint nextIfdOffset);
            foreach (var entry in entries)
            {
                if (entry.Tag == 0x014A) // SubIFDs
                {
                    var subs = TiffHeaderNavigator.ReadTagUnsignedArray(source, entry, littleEndian);
                    foreach (var s in subs)
                    {
                        if (s > 0 && !allIfdOffsets.Contains(s))
                            allIfdOffsets.Add(s);
                    }
                }
            }

            if (nextIfdOffset > 0 && !allIfdOffsets.Contains(nextIfdOffset))
                allIfdOffsets.Add(nextIfdOffset);

            currentIfdOffset = nextIfdOffset;
        }

        // Step 2: Parse each IFD
        int primaryArea = 0;
        bool primaryIsRaw = false;
        foreach (var ifdOffset in allIfdOffsets)
        {
            ct.ThrowIfCancellationRequested();
            var entries = TiffHeaderNavigator.ReadIfdEntries(source, ifdOffset, littleEndian, out _);

            uint subFileType = 0;
            ushort compression = 1;
            long photometric = 0;
            int width = 0;
            int height = 0;
            long? jpegOffset = null;
            long? jpegLength = null;

            foreach (var entry in entries)
            {
                switch (entry.Tag)
                {
                    case 0x00FE: // NewSubFileType: bit 0 = reduced-resolution version of another image
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } sft)
                            subFileType = (uint)sft;
                        break;

                    case 0x0100: // ImageWidth
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } w)
                            width = (int)Math.Min(w, int.MaxValue);
                        break;

                    case 0x0101: // ImageLength
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } h)
                            height = (int)Math.Min(h, int.MaxValue);
                        break;

                    case 0x0106: // PhotometricInterpretation: 32803 = CFA, 34892 = LinearRaw mark the raw image
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } photo)
                            photometric = photo;
                        break;

                    case 0x0103: // Compression: 1 = uncompressed, 6/7 = JPEG (7 is also lossless JPEG raw data)
                        if (TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian) is { } comp)
                            compression = (ushort)comp;
                        break;

                    case 0x0112: // Orientation
                        if (ifdOffset == ifd0Offset)
                        {
                            var val = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                            if (val is >= 1 and <= 8) orientation = (int)val;
                        }
                        break;

                    case 0x0201: // JPEGInterchangeFormat
                        jpegOffset = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                        break;

                    case 0x0202: // JPEGInterchangeFormatLength
                        jpegLength = TiffHeaderNavigator.ReadTagUnsigned(source, entry, littleEndian);
                        break;
                }
            }

            bool isPreview = false;
            if (jpegOffset is { } jo && jpegLength is { } jl && TiffHeaderNavigator.IsRangeInFile(jo, jl, source.Length))
            {
                previews.Add(new EmbeddedPreview(previews.Count, jo, jl, EmbeddedPreviewKind.Jpeg, width, height, PreviewColorSpace.Unknown));
                isPreview = true;
            }
            else if (compression is 6 or 7 &&
                     TiffHeaderNavigator.TryReadSingleStrip(source, entries, littleEndian, out long stripOffset, out long stripLength) &&
                     JpegMarkerProbe.TryReadLossyFrame(source, stripOffset, stripLength, out int jpegWidth, out int jpegHeight))
            {
                // Compression 7 is shared by real previews and lossless-JPEG (SOF3) sensor data: only a lossy
                // SOF0/1/2 frame is a preview, whatever NewSubFileType says.
                previews.Add(new EmbeddedPreview(
                    Index: previews.Count,
                    Offset: stripOffset,
                    Length: stripLength,
                    Kind: EmbeddedPreviewKind.Jpeg,
                    Width: width > 0 ? width : jpegWidth,
                    Height: height > 0 ? height : jpegHeight,
                    ColorSpace: PreviewColorSpace.Unknown));
                isPreview = true;
            }

            // The full-resolution raw image is the IFD that is neither a JPEG preview nor a secondary image:
            // reduced-resolution (bit 0), transparency mask (bit 2), bit 3, or DNG 1.6 enhanced/super-resolution data (bit 4).
            bool isSecondary = (subFileType & SecondaryImageMask) != 0;
            if (!isPreview && !isSecondary)
            {
                int candidateWidth = width;
                int candidateHeight = height;
                if (TiffHeaderNavigator.TryReadDefaultCropSize(source, entries, littleEndian, out int cropWidth, out int cropHeight))
                {
                    candidateWidth = cropWidth;
                    candidateHeight = cropHeight;
                }

                int area = (int)Math.Min((long)candidateWidth * candidateHeight, int.MaxValue);
                // An IFD with a raw PhotometricInterpretation beats a larger one without; area breaks ties.
                bool isRaw = photometric is 32803 or 34892;
                if (candidateWidth > 0 && candidateHeight > 0 &&
                    ((isRaw && !primaryIsRaw) || (isRaw == primaryIsRaw && area > primaryArea)))
                {
                    primaryArea = area;
                    primaryIsRaw = isRaw;
                    sensorWidth = candidateWidth;
                    sensorHeight = candidateHeight;
                }
            }
        }

        if (sensorWidth == 0 && previews.Count > 0)
        {
            var largest = previews.OrderByDescending(p => (long)p.Width * p.Height).First();
            sensorWidth = largest.Width;
            sensorHeight = largest.Height;
        }

        exifBlocks.Add(TiffHeaderNavigator.ComputeExifBlock(source, littleEndian, ifd0Offset));

        return new RawContainerInfo(
            RawFormat.Dng,
            sensorWidth,
            sensorHeight,
            orientation,
            previews,
            exifBlocks);
    }
}
