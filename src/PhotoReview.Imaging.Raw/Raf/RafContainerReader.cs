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
/// </summary>
public sealed class RafContainerReader : IRawContainerReader
{
    public RawFormat Format => RawFormat.Raf;

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

        // RAF has no orientation of its own: the camera writes it into the embedded JPEG's EXIF.
        int orientation = ExifParser.TryReadOrientationFromJpeg(source.Read(jpegOffset, (int)exifLength)) ?? 1;

        return new RawContainerInfo(
            RawFormat.Raf,
            SensorWidth: 0,
            SensorHeight: 0,
            Orientation: orientation,
            Previews: previews,
            ExifBlocks: exifBlocks);
    }
}
