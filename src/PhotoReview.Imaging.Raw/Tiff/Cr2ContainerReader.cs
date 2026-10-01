using System.IO;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Raw.Tiff;

/// <summary>
/// Reader for Canon CR2 format (TIFF container with 'CR' at offset 8).
/// IFD0: StripOffsets (0x0111) / StripByteCounts (0x0117) contain full JPEG preview (IFD0 is not the raw image).
/// Sensor size: Exif PixelXDimension/PixelYDimension, else the largest IFD.
/// IFD1: Small JPEG preview / thumbnail (0x0201 / 0x0202).
/// Orientation in IFD0 (0x0112).
/// </summary>
public sealed class Cr2ContainerReader : IRawContainerReader
{
    public RawFormat Format => RawFormat.Cr2;

    public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension)
    {
        if (!extension.Equals(".cr2", StringComparison.OrdinalIgnoreCase))
            return false;

        if (first64Bytes.Length < 10) return false;

        // TIFF header: II (0x49 0x49), magic 42, CR at offset 8, 9
        if (first64Bytes[0] == 0x49 && first64Bytes[1] == 0x49 &&
            first64Bytes[2] == 0x2A && first64Bytes[3] == 0x00 &&
            first64Bytes[8] == (byte)'C' && first64Bytes[9] == (byte)'R')
        {
            return true;
        }

        return false;
    }

    public RawContainerInfo Read(IRawHeaderSource source, CancellationToken ct)
    {
        if (source.Length < 16)
            throw new InvalidDataException("File too short for CR2 container.");

        var headerSpan = source.Read(0, 16);
        if (!TiffStructure.TryReadHeader(headerSpan, out bool littleEndian, out ushort magic, out uint ifd0Offset) || magic != 42)
            throw new InvalidDataException("Invalid CR2 TIFF header.");

        int orientation = 1;
        int exifWidth = 0;
        int exifHeight = 0;
        long largestArea = 0;
        int largestWidth = 0;
        int largestHeight = 0;
        var previews = new List<EmbeddedPreview>();
        var exifBlocks = new List<ExifBlock>();

        // Visited IFD offsets to guard against cycles
        var visited = new HashSet<long>();
        long currentIfdOffset = ifd0Offset;
        int ifdIndex = 0;

        while (currentIfdOffset > 0 && ifdIndex < RawContainerLimits.MaxIfdCount)
        {
            ct.ThrowIfCancellationRequested();

            if (!visited.Add(currentIfdOffset))
                break; // Cycle detected

            var entries = TiffHeaderNavigator.ReadIfdEntries(source, currentIfdOffset, littleEndian, out uint nextIfdOffset);
            TiffHeaderNavigator.ReadImageSize(source, entries, littleEndian, out int ifdWidth, out int ifdHeight);

            if (ifdIndex == 0 && TiffHeaderNavigator.ReadTagValue(source, entries, 0x0112, littleEndian) is >= 1 and <= 8 and var o)
                orientation = (int)o;

            if (TiffHeaderNavigator.ReadTagValue(source, entries, 0x8769, littleEndian) is { } exifOffset && exifOffset > 0)
            {
                exifBlocks.Add(TiffHeaderNavigator.ComputeExifBlock(source, littleEndian, ifd0Offset));
                if (exifWidth == 0 &&
                    TiffHeaderNavigator.TryReadExifPixelDimensions(source, exifOffset, littleEndian, out int pixelWidth, out int pixelHeight))
                {
                    exifWidth = pixelWidth;
                    exifHeight = pixelHeight;
                }
            }

            long area = (long)ifdWidth * ifdHeight;
            if (area > largestArea)
            {
                largestArea = area;
                largestWidth = ifdWidth;
                largestHeight = ifdHeight;
            }

            if (ifdIndex == 0)
            {
                // Full size preview in IFD0 StripOffsets/ByteCounts (a JPEG split over several strips is rejected).
                if (TiffHeaderNavigator.TryReadSingleStrip(source, entries, littleEndian, out long stripOffset, out long stripLength) &&
                    TiffHeaderNavigator.StartsWithSoi(source, stripOffset, stripLength))
                {
                    int stripWidth = ifdWidth;
                    int stripHeight = ifdHeight;
                    TiffHeaderNavigator.ReconcileJpegSize(source, stripOffset, stripLength, ref stripWidth, ref stripHeight);
                    previews.Add(new EmbeddedPreview(
                        Index: previews.Count,
                        Offset: stripOffset,
                        Length: stripLength,
                        Kind: EmbeddedPreviewKind.Jpeg,
                        Width: stripWidth,
                        Height: stripHeight,
                        ColorSpace: PreviewColorSpace.Unknown));
                }
            }
            else if (TiffHeaderNavigator.TryReadJpegInterchange(source, entries, littleEndian, out long jpegOffset, out long jpegLength))
            {
                // Secondary preview / thumbnail (IFD1, etc.)
                int previewWidth = ifdWidth;
                int previewHeight = ifdHeight;
                TiffHeaderNavigator.ReconcileJpegSize(source, jpegOffset, jpegLength, ref previewWidth, ref previewHeight);
                previews.Add(new EmbeddedPreview(
                    Index: previews.Count,
                    Offset: jpegOffset,
                    Length: jpegLength,
                    Kind: EmbeddedPreviewKind.Jpeg,
                    Width: previewWidth,
                    Height: previewHeight,
                    ColorSpace: PreviewColorSpace.Unknown));
            }

            currentIfdOffset = nextIfdOffset;
            ifdIndex++;
        }

        // IFD0 is only a JPEG preview (for sRAW it is the full-size preview, larger than the sensor data), and the
        // raw IFD may lack ImageWidth/Length. The Exif pixel size is the real image size; else the largest IFD.
        int sensorWidth = exifWidth > 0 ? exifWidth : largestWidth;
        int sensorHeight = exifWidth > 0 ? exifHeight : largestHeight;

        if (exifBlocks.Count == 0 && source.Length > 0)
        {
            exifBlocks.Add(TiffHeaderNavigator.ComputeExifBlock(source, littleEndian, ifd0Offset));
        }

        return new RawContainerInfo(
            RawFormat.Cr2,
            sensorWidth,
            sensorHeight,
            orientation,
            previews,
            exifBlocks);
    }
}
