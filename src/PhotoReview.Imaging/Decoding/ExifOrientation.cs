namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Helper for reading and applying standard EXIF Orientation (tags 1 to 8).
/// <para>WP-03 (C-03) / WP-06: pure (no WPF type). The members that take or return WPF types
/// (<c>Read(BitmapMetadata)</c>, <c>Apply(BitmapSource,int)</c>, <c>CreateTransform</c>) are <c>WpfExifOrientation</c> in
/// <c>PhotoReview.Imaging.Wpf</c>. Pixel rotation without WPF is <see cref="Pixels.PixelOps.ApplyOrientation"/>.</para>
/// </summary>
public static class ExifOrientation
{
    /// <summary>EXIF IFD0 orientation tag (274) in a JPEG APP1 block, the query WPF and WIC answer the same way.</summary>
    internal const string ExifOrientationQuery = "/app1/ifd/{ushort=274}";

    /// <summary>Photo-metadata policy name WIC maps onto the container's own orientation (TIFF, HEIF, ...).</summary>
    internal const string WindowsOrientationQuery = "System.Photo.Orientation";

    /// <summary>True for the orientations (5 to 8) that rotate by 90 degrees and so swap width and height.</summary>
    public static bool IsTransposed(int orientation) => orientation is >= 5 and <= 8;

    /// <summary>The orientation itself when it is a valid EXIF value (1 to 8); 1 (normal) for anything else.</summary>
    public static int Normalize(int orientation) => orientation is >= 1 and <= 8 ? orientation : 1;

    /// <summary>
    /// Reads the orientation through a metadata query function (WIC <c>IWICMetadataQueryReader</c>, see
    /// <see cref="Wic.WicExifReader"/>): the EXIF tag first, then the Windows photo policy, exactly the order of the WPF
    /// <c>Read(BitmapMetadata)</c>. A value outside 1..8, an absent tag or a throwing query gives 1: a damaged tag never fails a
    /// decode. <paramref name="query"/> returns the managed shape WPF's <c>GetQuery</c> returns (or null).
    /// </summary>
    internal static int ReadFromWic(Func<string, object?> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            var value = Metadata.ExifQueryInterpreter.AsInteger(query(ExifOrientationQuery));
            if (value is not (>= 1 and <= 8))
                value = Metadata.ExifQueryInterpreter.AsInteger(query(WindowsOrientationQuery));
            return value is >= 1 and <= 8 ? (int)value.Value : 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 1;
        }
    }
}
