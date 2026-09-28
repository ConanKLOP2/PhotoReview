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
        var previews = new List<EmbeddedPreview>();
        var exifBlocks = new List<ExifBlock>();

        // Top-level box scan
        long offset = 0;
        while (offset + 8 <= source.Length)
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
            previews,
            exifBlocks);
    }

    private static void ParsePreviewUuidBox(IRawHeaderSource source, in BmffBoxNavigator.BmffBox uuidBox, List<EmbeddedPreview> previews)
    {
        var children = BmffBoxNavigator.ReadChildBoxes(source, uuidBox);
        foreach (var child in children)
        {
            if (child.Type == "PRVW")
            {
                // PRVW box payload: 4 bytes version/flags, 4 bytes width, 4 bytes height, then JPEG payload
                if (child.PayloadSize > 14)
                {
                    var prvwHeader = source.Read(child.PayloadOffset, 14);
                    int w = BinaryPrimitives.ReadUInt16BigEndian(prvwHeader.Slice(6, 2));
                    int h = BinaryPrimitives.ReadUInt16BigEndian(prvwHeader.Slice(8, 2));

                    long jpegOffset = child.PayloadOffset + 14;
                    long jpegLength = child.PayloadSize - 14;

                    previews.Add(new EmbeddedPreview(
                        Index: previews.Count,
                        Offset: jpegOffset,
                        Length: jpegLength,
                        Kind: EmbeddedPreviewKind.Jpeg,
                        Width: w,
                        Height: h,
                        ColorSpace: PreviewColorSpace.Unknown));
                }
            }
        }
    }

    private static void AddPrvwBox(IRawHeaderSource source, in BmffBoxNavigator.BmffBox prvwBox, List<EmbeddedPreview> previews)
    {
        if (prvwBox.PayloadSize < 2) return;

        var probe = source.Read(prvwBox.PayloadOffset, 2);
        if (probe.Length == 2 && probe[0] == 0xFF && probe[1] == 0xD8)
        {
            // Direct JPEG payload inside PRVW (e.g. synthetic test)
            previews.Add(new EmbeddedPreview(
                Index: previews.Count,
                Offset: prvwBox.PayloadOffset,
                Length: prvwBox.PayloadSize,
                Kind: EmbeddedPreviewKind.Jpeg,
                Width: 0,
                Height: 0,
                ColorSpace: PreviewColorSpace.Unknown));
            return;
        }

        if (prvwBox.PayloadSize > 14)
        {
            var prvwHeader = source.Read(prvwBox.PayloadOffset, 14);
            int w = BinaryPrimitives.ReadUInt16BigEndian(prvwHeader.Slice(6, 2));
            int h = BinaryPrimitives.ReadUInt16BigEndian(prvwHeader.Slice(8, 2));

            long jpegOffset = prvwBox.PayloadOffset + 14;
            long jpegLength = prvwBox.PayloadSize - 14;

            previews.Add(new EmbeddedPreview(
                Index: previews.Count,
                Offset: jpegOffset,
                Length: jpegLength,
                Kind: EmbeddedPreviewKind.Jpeg,
                Width: w,
                Height: h,
                ColorSpace: PreviewColorSpace.Unknown));
        }
        else if (prvwBox.PayloadSize > 2)
        {
            // Raw JPEG payload directly inside PRVW
            previews.Add(new EmbeddedPreview(
                Index: previews.Count,
                Offset: prvwBox.PayloadOffset,
                Length: prvwBox.PayloadSize,
                Kind: EmbeddedPreviewKind.Jpeg,
                Width: 0,
                Height: 0,
                ColorSpace: PreviewColorSpace.Unknown));
        }
    }

    private static void ParseMoovBox(
        IRawHeaderSource source,
        in BmffBoxNavigator.BmffBox moovBox,
        List<EmbeddedPreview> previews,
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
        List<EmbeddedPreview> previews,
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
                    if (TiffStructure.TryReadHeader(tiffSpan, out bool little, out _, out uint ifd0))
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
                                sensorWidth = (int)w;
                            }
                            else if (e.Tag == 0x0101 && TiffHeaderNavigator.ReadTagUnsigned(source, e, little) is { } h)
                            {
                                sensorHeight = (int)h;
                            }
                        }
                    }
                }
            }
            else if (child.Type == "CMT2")
            {
                exifBlocks.Add(new ExifBlock(child.PayloadOffset, child.PayloadSize, IsTiffHeader: true));
            }
            else if (child.Type == "THMB")
            {
                // THMB box payload has a small header before JPEG
                if (child.PayloadSize > 12)
                {
                    var thmbSpan = source.Read(child.PayloadOffset, 32);
                    // Locate JPEG SOI (FF D8) in first 32 bytes
                    int soiIndex = -1;
                    for (int i = 0; i < thmbSpan.Length - 1; i++)
                    {
                        if (thmbSpan[i] == 0xFF && thmbSpan[i + 1] == 0xD8)
                        {
                            soiIndex = i;
                            break;
                        }
                    }

                    if (soiIndex >= 0)
                    {
                        long jpegOffset = child.PayloadOffset + soiIndex;
                        long jpegLength = child.PayloadSize - soiIndex;
                        previews.Add(new EmbeddedPreview(
                            Index: previews.Count,
                            Offset: jpegOffset,
                            Length: jpegLength,
                            Kind: EmbeddedPreviewKind.Jpeg,
                            Width: 160,
                            Height: 120,
                            ColorSpace: PreviewColorSpace.Unknown));
                    }
                }
            }
        }
    }

    private static void ParseTrackForJpegPreview(
        IRawHeaderSource source,
        in BmffBoxNavigator.BmffBox trakBox,
        List<EmbeddedPreview> previews)
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
                chunkOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(offsetData);
            }
        }

        if (chunkOffset > 0 && sampleSize > 0 && chunkOffset + sampleSize <= source.Length)
        {
            // Verify JPEG SOI
            var soiCheck = source.Read(chunkOffset, 2);
            if (soiCheck.Length == 2 && soiCheck[0] == 0xFF && soiCheck[1] == 0xD8)
            {
                previews.Add(new EmbeddedPreview(
                    Index: previews.Count,
                    Offset: chunkOffset,
                    Length: sampleSize,
                    Kind: EmbeddedPreviewKind.Jpeg,
                    Width: 0,
                    Height: 0,
                    ColorSpace: PreviewColorSpace.Unknown));
            }
        }
    }
}
