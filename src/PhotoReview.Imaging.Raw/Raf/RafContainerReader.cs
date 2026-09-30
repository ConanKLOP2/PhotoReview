using System.Buffers.Binary;
using System.IO;
using System.Text;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Raw.Raf;

/// <summary>
/// Reader for Fujifilm RAF format.
/// Fixed header: magic "FUJIFILMCCD-RAW " (16 bytes).
/// Offset 84: JPEG preview offset (uint32 big-endian).
/// Offset 88: JPEG preview length (uint32 big-endian).
/// Embedded JPEG contains its own EXIF APP1 with camera/orientation metadata.
/// Offset 92/96: CFA header offset/length (uint32 big-endian) holding records (tag u16, size u16, data; big-endian):
/// 0x0100 = RawImageFullSize (height, width incl. masked margins), 0x0111 = RawImageCroppedSize (height, width).
/// The sensor size is the cropped size, else the full size. LibRaw derives its own margins and reports a slightly
/// different size (X-T2: LibRaw 6032x4032, cropped record 6000x4000, full record 6160x4032); this reader reports the
/// camera-declared cropped size, which is within a few percent of LibRaw and is never the embedded JPEG size.
/// </summary>
public sealed class RafContainerReader : IRawContainerReader
{
    public RawFormat Format => RawFormat.Raf;

    // A hostile CFA header length must not force a large read or an unbounded record walk.
    private const int MaxCfaHeaderBytes = 64 * 1024;
    private const int MaxCfaRecords = 256;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FUJIFILMCCD-RAW ");

    public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension)
    {
        if (!extension.Equals(".raf", StringComparison.OrdinalIgnoreCase))
            return false;

        if (first64Bytes.Length < 16) return false;

        return first64Bytes[..16].SequenceEqual(Magic);
    }

    public RawContainerInfo Read(IRawHeaderSource source, CancellationToken ct)
    {
        if (source.Length < 92)
            throw new InvalidDataException("File too short for RAF container.");

        var headerSpan = source.Read(0, 92);
        if (headerSpan.Length < 92 || !headerSpan[..16].SequenceEqual(Magic))
            throw new InvalidDataException("Invalid RAF magic header.");

        uint jpegOffset = BinaryPrimitives.ReadUInt32BigEndian(headerSpan.Slice(84, 4));
        uint jpegLength = BinaryPrimitives.ReadUInt32BigEndian(headerSpan.Slice(88, 4));

        if (jpegOffset == 0 || jpegLength == 0 || (long)jpegOffset + jpegLength > source.Length)
            throw new InvalidDataException("Invalid embedded JPEG range in RAF header.");

        var previews = new List<EmbeddedPreview>
        {
            new(
                Index: 0,
                Offset: jpegOffset,
                Length: jpegLength,
                Kind: EmbeddedPreviewKind.Jpeg,
                Width: 0,
                Height: 0,
                ColorSpace: PreviewColorSpace.Unknown)
        };

        // EXIF block inside embedded JPEG APP1
        long exifLength = Math.Min(jpegLength, 128 * 1024);
        var exifBlocks = new List<ExifBlock>
        {
            new(jpegOffset, exifLength, IsTiffHeader: false)
        };

        var (sensorWidth, sensorHeight) = ReadCfaSize(source);

        // RAF has no orientation of its own: the camera writes it into the embedded JPEG's EXIF.
        int orientation = ExifParser.TryReadOrientationFromJpeg(source.Read(jpegOffset, (int)exifLength)) ?? 1;

        return new RawContainerInfo(
            RawFormat.Raf,
            SensorWidth: sensorWidth,
            SensorHeight: sensorHeight,
            Orientation: orientation,
            Previews: previews,
            ExifBlocks: exifBlocks);
    }

    /// <summary>Reads the raw image size from the CFA header; (0, 0) when it is absent or malformed.</summary>
    private static (int Width, int Height) ReadCfaSize(IRawHeaderSource source)
    {
        var header = source.Read(92, 8);
        if (header.Length < 8) return (0, 0);

        uint cfaOffset = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        uint cfaLength = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
        if (cfaOffset == 0 || cfaLength < 4 || (long)cfaOffset + cfaLength > source.Length) return (0, 0);

        var cfa = source.Read(cfaOffset, (int)Math.Min(cfaLength, MaxCfaHeaderBytes));
        if (cfa.Length < 4) return (0, 0);

        uint count = BinaryPrimitives.ReadUInt32BigEndian(cfa[..4]);
        (int Width, int Height) full = (0, 0);
        (int Width, int Height) cropped = (0, 0);
        int position = 4;
        for (uint i = 0; i < Math.Min(count, MaxCfaRecords) && position + 4 <= cfa.Length; i++)
        {
            ushort tag = BinaryPrimitives.ReadUInt16BigEndian(cfa.Slice(position, 2));
            int size = BinaryPrimitives.ReadUInt16BigEndian(cfa.Slice(position + 2, 2));
            int data = position + 4;
            if (data + size > cfa.Length) break;

            if (size >= 4 && tag is 0x0100 or 0x0111)
            {
                int height = BinaryPrimitives.ReadUInt16BigEndian(cfa.Slice(data, 2));
                int width = BinaryPrimitives.ReadUInt16BigEndian(cfa.Slice(data + 2, 2));
                if (width > 0 && height > 0)
                {
                    if (tag == 0x0100) full = (width, height);
                    else cropped = (width, height);
                }
            }

            position = data + size;
        }

        // The cropped size can only shrink the full readout; ignore it when it claims more.
        bool croppedFits = cropped.Width > 0 && (full.Width == 0 || (cropped.Width <= full.Width && cropped.Height <= full.Height));
        return croppedFits ? cropped : full;
    }
}
