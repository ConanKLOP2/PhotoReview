using System.IO;
using PhotoReview.Imaging.Raw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>Rewrites the orientation tag of a real RAW file's bytes, turning a landscape corpus sample into a portrait one.</summary>
internal static class RawOrientationPatcher
{
    /// <summary>
    /// Rewrites the Orientation tag in place: the CMT1 TIFF block of a CR3, IFD0 of a TIFF-family RAW, else the embedded
    /// JPEG's EXIF (RAF, RW2 without IFD0 tag).
    /// </summary>
    internal static bool TryWriteOrientation(byte[] file, string extension, RawContainerInfo info, ushort orientation)
    {
        if (extension == ".cr3") return TryFindCr3Cmt1Tiff(file, out var cmt1Tiff) && TryWriteTiffOrientation(file, cmt1Tiff, orientation);
        if (extension != ".raf" && TryWriteTiffOrientation(file, 0, orientation)) return true;
        if (info.Previews.Count == 0) return false;

        var jpegStart = checked((int)info.Previews[0].Offset);
        var offset = jpegStart + 2;
        while (offset + 4 <= file.Length && file[offset] == 0xFF)
        {
            var marker = file[offset + 1];
            var length = (file[offset + 2] << 8) | file[offset + 3];
            if (marker == 0xE1 && length >= 8 && file.AsSpan(offset + 4, 6).SequenceEqual("Exif\0\0"u8))
                return TryWriteTiffOrientation(file, offset + 10, orientation);
            if (marker is 0xDA or 0xD9) break;
            offset += 2 + length;
        }
        return false;
    }

    /// <summary>
    /// Locates the TIFF block of a CR3's Canon CMT1 box (payload of an ISO-BMFF box whose type is "CMT1", inside
    /// moov/uuid within the first megabyte), where the camera stores the IFD0 orientation.
    /// </summary>
    internal static bool TryFindCr3Cmt1Tiff(byte[] file, out int tiffStart)
    {
        tiffStart = 0;
        var head = file.AsSpan(0, Math.Min(file.Length, 1 << 20));
        var typeAt = head.IndexOf("CMT1"u8);
        if (typeAt < 4 || typeAt + 12 > head.Length || head.Slice(typeAt + 4, 2) is not ([(byte)'I', (byte)'I'] or [(byte)'M', (byte)'M']))
            return false;

        tiffStart = typeAt + 4;
        return true;
    }

    /// <summary>Renames an IFD0 tag of the TIFF block at <paramref name="tiffStart"/>, hiding it from readers.</summary>
    internal static bool TryRenameIfd0Tag(byte[] file, int tiffStart, ushort tag, ushort replacement)
    {
        if (!TryFindIfd0Entry(file, tiffStart, tag, out var entry, out var little)) return false;
        file[entry] = little ? (byte)replacement : (byte)(replacement >> 8);
        file[entry + 1] = little ? (byte)(replacement >> 8) : (byte)replacement;
        return true;
    }

    internal static bool TryWriteTiffOrientation(byte[] file, int tiffStart, ushort orientation)
    {
        if (!TryFindIfd0Entry(file, tiffStart, 0x0112, out var entry, out var little)) return false;
        if ((little ? file[entry + 2] | (file[entry + 3] << 8) : (file[entry + 2] << 8) | file[entry + 3]) != 3) return false; // must be a SHORT
        file[entry + 8] = little ? (byte)orientation : (byte)0;
        file[entry + 9] = little ? (byte)0 : (byte)orientation;
        return true;
    }

    private static bool TryFindIfd0Entry(byte[] file, int tiffStart, ushort tag, out int entryOffset, out bool little)
    {
        entryOffset = 0;
        little = false;
        if (tiffStart + 8 > file.Length) return false;
        little = file[tiffStart] == (byte)'I';
        var isLittle = little;
        ushort ReadU16(int at) => isLittle ? (ushort)(file[at] | (file[at + 1] << 8)) : (ushort)((file[at] << 8) | file[at + 1]);
        uint ReadU32(int at) => isLittle
            ? (uint)(file[at] | (file[at + 1] << 8) | (file[at + 2] << 16) | (file[at + 3] << 24))
            : (uint)((file[at] << 24) | (file[at + 1] << 16) | (file[at + 2] << 8) | file[at + 3]);

        var ifd0 = checked(tiffStart + (int)ReadU32(tiffStart + 4));
        if (ifd0 + 2 > file.Length) return false;
        int count = ReadU16(ifd0);
        for (var i = 0; i < count && ifd0 + 2 + ((i + 1) * 12) <= file.Length; i++)
        {
            var entry = ifd0 + 2 + (i * 12);
            if (ReadU16(entry) != tag) continue;
            entryOffset = entry;
            return true;
        }
        return false;
    }
}
