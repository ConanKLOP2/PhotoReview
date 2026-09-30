using System.Buffers.Binary;
using System.IO;
using System.Text;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Raw.Tiff;

namespace PhotoReview.Imaging.Raw.Bmff;

/// <summary>
/// Reader for Canon CR3 format (ISO-BMFF with 'crx ' brand).
/// Extracts:
/// - Orientation & Make/Model from CMT1 (TIFF structure in Canon uuid box).
/// - Small THMB JPEG preview (~160x120).
/// - Medium PRVW JPEG preview (~1620x1080).
/// - Full-size JPEG preview from track 1 (stsz + stco/co64 chunks in mdat).
/// Previews are returned PRVW first, then full-size track JPEGs, then THMB.
/// </summary>
public sealed class Cr3ContainerReader : IRawContainerReader
{
    public RawFormat Format => RawFormat.Cr3;

    // Canon UUID: 85c0b687-820f-11e0-8111-f4ce462b6a48
    private static readonly byte[] CanonMoovUuid =
    [
        0x85, 0xC0, 0xB6, 0x87, 0x82, 0x0F, 0x11, 0xE0, 0x81, 0x11, 0xF4, 0xCE, 0x46, 0x2B, 0x6A, 0x48
    ];

    // Preview UUID: eaf42b5e-1c98-4b88-b9fb-b7dc406e4d16
    private static readonly byte[] PreviewUuid =
    [
        0xEA, 0xF4, 0x2B, 0x5E, 0x1C, 0x98, 0x4B, 0x88, 0xB9, 0xFB, 0xB7, 0xDC, 0x40, 0x6E, 0x4D, 0x16
    ];

    // Canon preview box layouts (verified against real EOS R6 / EOS M50 files):
    //   uuid(eaf42b5e...) payload: u32 0, u32 1 (8-byte prefix), then child PRVW box(es).
    //   PRVW payload: u32 0, u16 1, u16 width, u16 height, u16 1, u32 jpegSize, JPEG (SOI) at +16.
    //   THMB payload: u32 0, u16 width, u16 height, u32 jpegSize, u16 1, u16 0, JPEG (SOI) at +16.
    private const int PreviewUuidPrefixBytes = 8;
    private const int PreviewHeaderBytes = 16;
    private const int SoiBytes = 2;

    public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension)
    {
        if (!extension.Equals(".cr3", StringComparison.OrdinalIgnoreCase))
            return false;

        if (first64Bytes.Length < 12) return false;

        // Must be an ftyp box with 'crx ' major brand
        if (first64Bytes[4] == (byte)'f' && first64Bytes[5] == (byte)'t' &&
            first64Bytes[6] == (byte)'y' && first64Bytes[7] == (byte)'p' &&
            first64Bytes[8] == (byte)'c' && first64Bytes[9] == (byte)'r' &&
            first64Bytes[10] == (byte)'x' && first64Bytes[11] == (byte)' ')
        {
            return true;
        }

        return false;
    }

    public RawContainerInfo Read(IRawHeaderSource source, CancellationToken ct)
    {
        if (source.Length < 16)
            throw new InvalidDataException("File too short for CR3 container.");

        int orientation = 1;
        int sensorWidth = 0;
        int sensorHeight = 0;
        var previews = new PreviewCollector();
        var exifBlocks = new List<ExifBlock>();

        // Top-level box scan
        long offset = 0;
        while (offset <= source.Length - 8)
        {
            ct.ThrowIfCancellationRequested();
            if (!BmffBoxNavigator.TryReadBox(source, offset, out var box))
                break;

            if (box.TotalSize <= 0) break;

            if ((box.Type == "uuid" && box.Uuid != null && box.Uuid.SequenceEqual(PreviewUuid)))
            {
                // PRVW box inside preview uuid
                ParsePreviewUuidBox(source, box, previews);
            }
            else if (box.Type.Equals("PRVW", StringComparison.OrdinalIgnoreCase))
            {
                // Direct PRVW box
                AddPrvwBox(source, box, previews);
            }
            else if (box.Type == "moov")
            {
                ParseMoovBox(source, box, previews, exifBlocks, ref orientation, ref sensorWidth, ref sensorHeight);
            }

            offset += box.TotalSize;
        }

        return new RawContainerInfo(
            RawFormat.Cr3,
            sensorWidth,
            sensorHeight,
            orientation,
            previews.ToOrderedList(),
            exifBlocks);
    }

    /// <summary>
    /// Previews grouped by origin. The final list is PRVW (medium) first, then full-size track JPEGs, then THMB
    /// (tiny thumbnail), so consumers that fall back to the first entry get the most useful one.
    /// </summary>
    private sealed class PreviewCollector
    {
        public List<EmbeddedPreview> Prvw { get; } = [];
        public List<EmbeddedPreview> Track { get; } = [];
        public List<EmbeddedPreview> Thumb { get; } = [];

        public List<EmbeddedPreview> ToOrderedList()
        {
            var ordered = new List<EmbeddedPreview>(Prvw.Count + Track.Count + Thumb.Count);
            foreach (var p in Prvw.Concat(Track).Concat(Thumb))
                ordered.Add(p with { Index = ordered.Count });
            return ordered;
        }
    }

    private static EmbeddedPreview NewJpeg(long offset, long length, int width, int height) =>
        new(Index: 0, Offset: offset, Length: length, Kind: EmbeddedPreviewKind.Jpeg,
            Width: width, Height: height, ColorSpace: PreviewColorSpace.Unknown);

    /// <summary>Reads up to <paramref name="maxCount"/> bytes at <paramref name="offset"/>, clamped to the source; empty if out of range.</summary>
    private static ReadOnlySpan<byte> ReadClamped(IRawHeaderSource source, long offset, long maxCount)
    {
        if (offset < 0 || offset >= source.Length || maxCount <= 0)
            return ReadOnlySpan<byte>.Empty;

        int count = (int)Math.Min(maxCount, source.Length - offset);
        return source.Read(offset, count);
    }

    private static bool HasSoi(IRawHeaderSource source, long offset) =>
        ReadClamped(source, offset, SoiBytes) is [0xFF, 0xD8];

    /// <summary>
    /// Reads the JPEG that follows a 16-byte Canon preview header (PRVW or THMB layout) if it is valid:
    /// SOI present and the advertised size fits in the box and the file (an inconsistent size is clamped to the box).
    /// </summary>
    private static EmbeddedPreview? TryReadHeaderedJpeg(
        IRawHeaderSource source, in BmffBoxNavigator.BmffBox box, int sizeFieldOffset, int widthOffset, int heightOffset)
    {
        if (box.PayloadSize < PreviewHeaderBytes + SoiBytes)
            return null;

        var header = source.Read(box.PayloadOffset, PreviewHeaderBytes);
        int width = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(widthOffset, 2));
        int height = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(heightOffset, 2));
        uint declared = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(sizeFieldOffset, 4));

        long jpegOffset = box.PayloadOffset + PreviewHeaderBytes;
        long available = box.PayloadSize - PreviewHeaderBytes;
        long jpegLength = declared > 0 && declared <= available ? declared : available;

        if (jpegLength < SoiBytes || jpegOffset > source.Length - jpegLength || !HasSoi(source, jpegOffset))
            return null;

        return NewJpeg(jpegOffset, jpegLength, width, height);
    }

    private static void ParsePreviewUuidBox(IRawHeaderSource source, in BmffBoxNavigator.BmffBox uuidBox, PreviewCollector previews)
    {
        // The Canon preview uuid has an 8-byte prefix before its children; tolerate producers that omit it.
        var children = BmffBoxNavigator.ReadChildBoxes(source, uuidBox, PreviewUuidPrefixBytes);
        if (children.Count == 0)
            children = BmffBoxNavigator.ReadChildBoxes(source, uuidBox);

        foreach (var child in children)
        {
            if (child.Type == "PRVW" &&
                TryReadHeaderedJpeg(source, child, sizeFieldOffset: 12, widthOffset: 6, heightOffset: 8) is { } preview)
            {
                previews.Prvw.Add(preview);
            }
        }
    }

    private static void AddPrvwBox(IRawHeaderSource source, in BmffBoxNavigator.BmffBox prvwBox, PreviewCollector previews)
    {
        if (prvwBox.PayloadSize < SoiBytes) return;

        if (HasSoi(source, prvwBox.PayloadOffset))
        {
            // Direct JPEG payload inside PRVW (e.g. synthetic test)
            previews.Prvw.Add(NewJpeg(prvwBox.PayloadOffset, prvwBox.PayloadSize, 0, 0));
            return;
        }

        if (TryReadHeaderedJpeg(source, prvwBox, sizeFieldOffset: 12, widthOffset: 6, heightOffset: 8) is { } preview)
            previews.Prvw.Add(preview);
    }

    private static void ParseMoovBox(
        IRawHeaderSource source,
        in BmffBoxNavigator.BmffBox moovBox,
        PreviewCollector previews,
        List<ExifBlock> exifBlocks,
        ref int orientation,
        ref int sensorWidth,
        ref int sensorHeight)
    {
        var children = BmffBoxNavigator.ReadChildBoxes(source, moovBox);
        foreach (var child in children)
        {
            if (child.Type == "uuid" && child.Uuid != null && child.Uuid.SequenceEqual(CanonMoovUuid))
            {
                // Canon moov uuid contains CMT1..CMT4 and THMB
                ParseCanonMoovUuid(source, child, previews, exifBlocks, ref orientation, ref sensorWidth, ref sensorHeight);
            }
            else if (child.Type == "trak")
            {
                // Track 1 contains full JPEG preview in mdat
                ParseTrackForJpegPreview(source, child, previews);
            }
        }
    }

    private static void ParseCanonMoovUuid(
        IRawHeaderSource source,
        in BmffBoxNavigator.BmffBox uuidBox,
        PreviewCollector previews,
        List<ExifBlock> exifBlocks,
        ref int orientation,
        ref int sensorWidth,
        ref int sensorHeight)
    {
        var children = BmffBoxNavigator.ReadChildBoxes(source, uuidBox);
        foreach (var child in children)
        {
            if (child.Type == "CMT1")
            {
                // Standalone TIFF block with IFD0 (orientation, camera make/model)
                exifBlocks.Add(new ExifBlock(child.PayloadOffset, child.PayloadSize, IsTiffHeader: true));
                if (child.PayloadSize >= 16)
                {
                    var tiffSpan = source.Read(child.PayloadOffset, (int)Math.Min(child.PayloadSize, 4096));
                    if (TiffStructure.TryReadHeader(tiffSpan, out bool little, out _, out uint ifd0)
                        && ifd0 >= 8 && ifd0 <= child.PayloadSize - 2) // IFD0 must lie inside the CMT1 payload
                    {
                        var entries = TiffHeaderNavigator.ReadIfdEntries(source, child.PayloadOffset + ifd0, little, out _);
                        foreach (var e in entries)
                        {
                            if (e.Tag == 0x0112 && TiffHeaderNavigator.ReadTagUnsigned(source, e, little) is { } orient)
                            {
                                if (orient is >= 1 and <= 8) orientation = (int)orient;
                            }
                            else if (e.Tag == 0x0100 && TiffHeaderNavigator.ReadTagUnsigned(source, e, little) is { } w)
                            {
                                if (w is > 0 and <= int.MaxValue) sensorWidth = (int)w;
                            }
                            else if (e.Tag == 0x0101 && TiffHeaderNavigator.ReadTagUnsigned(source, e, little) is { } h)
                            {
                                if (h is > 0 and <= int.MaxValue) sensorHeight = (int)h;
                            }
                        }
                    }
                }
            }
            else if (child.Type == "CMT2")
            {
                // CMT2 IFD0 holds the exposure fields directly (Exif IFD without a 0x8769 pointer).
                exifBlocks.Add(new ExifBlock(child.PayloadOffset, child.PayloadSize, IsTiffHeader: true, IfdIsExif: true));
            }
            else if (child.Type == "THMB" &&
                TryReadHeaderedJpeg(source, child, sizeFieldOffset: 8, widthOffset: 4, heightOffset: 6) is { } thumb)
            {
                previews.Thumb.Add(thumb);
            }
        }
    }

    private static void ParseTrackForJpegPreview(
        IRawHeaderSource source,
        in BmffBoxNavigator.BmffBox trakBox,
        PreviewCollector previews)
    {
        // Walk trak -> mdia -> minf -> stbl
        var mdia = BmffBoxNavigator.ReadChildBoxes(source, trakBox).FirstOrDefault(b => b.Type == "mdia");
        if (mdia.TotalSize == 0) return;

        var minf = BmffBoxNavigator.ReadChildBoxes(source, mdia).FirstOrDefault(b => b.Type == "minf");
        if (minf.TotalSize == 0) return;

        var stbl = BmffBoxNavigator.ReadChildBoxes(source, minf).FirstOrDefault(b => b.Type == "stbl");
        if (stbl.TotalSize == 0) return;

        var stblChildren = BmffBoxNavigator.ReadChildBoxes(source, stbl);
        var stsz = stblChildren.FirstOrDefault(b => b.Type == "stsz");
        var stco = stblChildren.FirstOrDefault(b => b.Type == "stco");
        var co64 = stblChildren.FirstOrDefault(b => b.Type == "co64");

        long sampleSize = 0;
        long chunkOffset = 0;

        if (stsz.TotalSize > 0 && stsz.PayloadSize >= 12)
        {
            var stszData = source.Read(stsz.PayloadOffset, 12);
            uint uniformSize = BinaryPrimitives.ReadUInt32BigEndian(stszData.Slice(4, 4));
            uint count = BinaryPrimitives.ReadUInt32BigEndian(stszData.Slice(8, 4));
            if (uniformSize > 0)
            {
                sampleSize = uniformSize;
            }
            else if (count >= 1 && stsz.PayloadSize >= 16)
            {
                var entryData = source.Read(stsz.PayloadOffset + 12, 4);
                sampleSize = BinaryPrimitives.ReadUInt32BigEndian(entryData);
            }
        }

        if (stco.TotalSize > 0 && stco.PayloadSize >= 8)
        {
            var stcoData = source.Read(stco.PayloadOffset, 8);
            uint count = BinaryPrimitives.ReadUInt32BigEndian(stcoData.Slice(4, 4));
            if (count >= 1 && stco.PayloadSize >= 12)
            {
                var offsetData = source.Read(stco.PayloadOffset + 8, 4);
                chunkOffset = BinaryPrimitives.ReadUInt32BigEndian(offsetData);
            }
        }
        else if (co64.TotalSize > 0 && co64.PayloadSize >= 8)
        {
            var co64Data = source.Read(co64.PayloadOffset, 8);
            uint count = BinaryPrimitives.ReadUInt32BigEndian(co64Data.Slice(4, 4));
            if (count >= 1 && co64.PayloadSize >= 16)
            {
                var offsetData = source.Read(co64.PayloadOffset + 8, 8);
                ulong rawOffset = BinaryPrimitives.ReadUInt64BigEndian(offsetData);
                chunkOffset = rawOffset <= long.MaxValue ? (long)rawOffset : 0;
            }
        }

        // sampleSize <= Length - chunkOffset is the overflow-free form of chunkOffset + sampleSize <= Length.
        if (chunkOffset > 0 && sampleSize > 0 && chunkOffset < source.Length && sampleSize <= source.Length - chunkOffset
            && HasSoi(source, chunkOffset))
        {
            previews.Track.Add(NewJpeg(chunkOffset, sampleSize, 0, 0));
        }
    }
}