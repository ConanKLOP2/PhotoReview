using System.Runtime.InteropServices;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Decoding.Wic;

/// <summary>
/// WP-03: EXIF orientation and photo-information fields read through the WIC metadata query reader of a frame the caller
/// already opened (<see cref="WicDirectDecoder"/>, <see cref="EmbeddedThumbnailReader"/>) -- no extra stream read, no WPF
/// <c>BitmapMetadata</c>. PROPVARIANT values are mapped to the managed shapes WPF's <c>BitmapMetadata.GetQuery</c> returns, so
/// <see cref="ExifQueryInterpreter"/> and <see cref="ExifOrientation.ReadFromWic"/> serve both paths. Never throws for a
/// metadata fault: any failure gives orientation 1 / no EXIF.
/// </summary>
internal static partial class WicExifReader
{
    private const int PropVariantSize = 24;

    /// <summary>IFD root of the EXIF block for JPEG/TIFF containers; null (no EXIF read) for anything else.</summary>
    public static string? ExifIfdRootOf(IWICBitmapDecoder decoder)
    {
        try
        {
            decoder.GetContainerFormat(out Guid container);
            if (container == WicGuids.GUID_ContainerFormatJpeg) return ExifQueryInterpreter.JpegIfdRoot;
            if (container == WicGuids.GUID_ContainerFormatTiff) return ExifQueryInterpreter.TiffIfdRoot;
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>EXIF orientation of <paramref name="frame"/> (1 when absent, invalid or unreadable).</summary>
    public static int ReadOrientation(IWICBitmapFrameDecode frame) =>
        ReadFrameMetadata(frame, readOrientation: true, exifIfdRoot: null, out _);

    /// <summary>
    /// Reads the EXIF orientation (when <paramref name="readOrientation"/>) and, when <paramref name="exifIfdRoot"/> is
    /// set, the photo-information fields through ONE metadata query reader of the frame being decoded -- the same
    /// metadata block WIC parses for the orientation anyway, so no extra stream read. Never throws: any metadata
    /// failure gives orientation 1 / no EXIF.
    /// </summary>
    public static int ReadFrameMetadata(IWICBitmapFrameDecode frame, bool readOrientation, string? exifIfdRoot, out ExifSummary? exif)
    {
        exif = null;
        IWICMetadataQueryReader? reader = null;
        IntPtr pvar = IntPtr.Zero;
        try
        {
            frame.GetMetadataQueryReader(out reader);
            pvar = Marshal.AllocHGlobal(PropVariantSize);
            ZeroPropVariant(pvar);
            var queryReader = reader;
            var queryBuffer = pvar;
            return ReadMetadataValues(name => QueryValue(queryReader, name, queryBuffer), readOrientation, exifIfdRoot, out exif);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No metadata reader at all (or the buffer could not be set up).
            exif = null;
            return 1;
        }
        finally
        {
            if (pvar != IntPtr.Zero)
            {
                _ = PropVariantClear(pvar);
                Marshal.FreeHGlobal(pvar);
            }
            SafeRelease(reader);
        }
    }

    /// <summary>
    /// The orientation and the photo-information read are independent: a failure in one (a damaged EXIF field) must not
    /// discard the other, or a photo whose orientation was read fine would silently show unrotated.
    /// </summary>
    internal static int ReadMetadataValues(Func<string, object?> query, bool readOrientation, string? exifIfdRoot, out ExifSummary? exif)
    {
        var orientation = readOrientation ? ExifOrientation.ReadFromWic(query) : 1;
        exif = null;
        if (exifIfdRoot is not null)
        {
            try
            {
                exif = ExifQueryInterpreter.Read(query, exifIfdRoot);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                exif = null;
            }
        }

        return orientation;
    }

    /// <summary>One metadata query into a managed value (see <see cref="ReadVariant"/>); null when absent or unreadable.</summary>
    /// <remarks><paramref name="pvar"/> must be zeroed on entry; it is cleared and zeroed again before returning.</remarks>
    private static object? QueryValue(IWICMetadataQueryReader reader, string name, IntPtr pvar)
    {
        try
        {
            return reader.GetMetadataByName(name, pvar) < 0 ? null : ReadVariant(pvar);
        }
        finally
        {
            _ = PropVariantClear(pvar);
            ZeroPropVariant(pvar);
        }
    }

    private static void ZeroPropVariant(IntPtr pvar)
    {
        for (var i = 0; i < PropVariantSize; i += 8) Marshal.WriteInt64(pvar, i, 0);
    }

    /// <summary>
    /// PROPVARIANT to the managed shapes WPF's BitmapMetadata.GetQuery returns for the same tags (ushort, uint,
    /// ulong-packed rational, string, first element of a vector), so <see cref="ExifQueryInterpreter"/> serves both.
    /// </summary>
    private static object? ReadVariant(IntPtr pvar)
    {
        const int data = 8;
        const ushort vtVector = 0x1000;
        var vt = (ushort)Marshal.ReadInt16(pvar);
        switch (vt)
        {
            case 2: return Marshal.ReadInt16(pvar, data);              // VT_I2
            case 3: return Marshal.ReadInt32(pvar, data);              // VT_I4
            case 17: return Marshal.ReadByte(pvar, data);              // VT_UI1
            case 18: return (ushort)Marshal.ReadInt16(pvar, data);     // VT_UI2
            case 19: return (uint)Marshal.ReadInt32(pvar, data);       // VT_UI4
            case 20: return Marshal.ReadInt64(pvar, data);             // VT_I8 (SRATIONAL)
            case 21: return (ulong)Marshal.ReadInt64(pvar, data);      // VT_UI8 (RATIONAL)
            case 30:                                                   // VT_LPSTR (EXIF ASCII)
            {
                var text = Marshal.ReadIntPtr(pvar, data);
                return text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text);
            }
            case 31:                                                   // VT_LPWSTR
            {
                var text = Marshal.ReadIntPtr(pvar, data);
                return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text);
            }
        }

        if ((vt & vtVector) == 0) return null;
        // CA* vector: { ULONG cElems; T* pElems } -- only the first element is used.
        var count = Marshal.ReadInt32(pvar, data);
        var elements = Marshal.ReadIntPtr(pvar, data + IntPtr.Size);
        if (count <= 0 || elements == IntPtr.Zero) return null;
        return (vt & ~vtVector) switch
        {
            18 => new[] { (ushort)Marshal.ReadInt16(elements) },
            19 => new[] { (uint)Marshal.ReadInt32(elements) },
            20 => new[] { Marshal.ReadInt64(elements) },
            21 => new[] { (ulong)Marshal.ReadInt64(elements) },
            _ => null,
        };
    }

    private static void SafeRelease(object? comObj) => WicCom.Release(comObj);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(nint pvar);
}
