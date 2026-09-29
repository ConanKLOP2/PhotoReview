using System.IO;
using PhotoReview.Imaging.Raw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>Rewrites the orientation tag of a real RAW file's bytes, turning a landscape corpus sample into a portrait one.</summary>
internal static class RawOrientationPatcher
{
    /// <summary>Rewrites the Orientation tag in place: IFD0 of a TIFF-family RAW, else the embedded JPEG's EXIF (RAF, RW2 without IFD0 tag).</summary>
    internal static bool TryWriteOrientation(byte[] file, string extension, RawContainerInfo info, ushort orientation)
    {
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

    internal static bool TryWriteTiffOrientation(byte[] file, int tiffStart, ushort orientation)
    {
        if (tiffStart + 8 > file.Length) return false;
        var little = file[tiffStart] == (byte)'I';
        ushort ReadU16(int at) => little ? (ushort)(file[at] | (file[at + 1] << 8)) : (ushort)((file[at] << 8) | file[at + 1]);
        uint ReadU32(int at) => little
            ? (uint)(file[at] | (file[at + 1] << 8) | (file[at + 2] << 16) | (file[at + 3] << 24))
            : (uint)((file[at] << 24) | (file[at + 1] << 16) | (file[at + 2] << 8) | file[at + 3]);

        var ifd0 = checked(tiffStart + (int)ReadU32(tiffStart + 4));
        if (ifd0 + 2 > file.Length) return false;
        int count = ReadU16(ifd0);
        for (var i = 0; i < count && ifd0 + 2 + ((i + 1) * 12) <= file.Length; i++)
        {
            var entry = ifd0 + 2 + (i * 12);
            if (ReadU16(entry) != 0x0112 || ReadU16(entry + 2) != 3) continue;
            file[entry + 8] = little ? (byte)orientation : (byte)0;
            file[entry + 9] = little ? (byte)0 : (byte)orientation;
            return true;
        }
        return false;
    }
}
