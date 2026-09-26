using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Reads the photo-information EXIF fields from the <see cref="BitmapMetadata"/> of a frame the WPF decoder has
/// already opened for its header (orientation/size) -- no extra stream read. JPEG and TIFF only; never throws.
/// </summary>
internal static class WpfExifReader
{
    public static ExifSummary? Read(BitmapMetadata? metadata)
    {
        if (metadata is null) return null;
        try
        {
            var root = metadata.Format switch
            {
                "jpg" => ExifQueryInterpreter.JpegIfdRoot,
                "tiff" => ExifQueryInterpreter.TiffIfdRoot,
                _ => null,
            };
            return root is null ? null : ExifQueryInterpreter.Read(query => Get(metadata, query), root);
        }
        catch (Exception ex) when (IsMetadataFailure(ex))
        {
            return null;
        }
    }

    private static object? Get(BitmapMetadata metadata, string query)
    {
        try
        {
            return metadata.GetQuery(query);
        }
        catch (Exception ex) when (IsMetadataFailure(ex))
        {
            return null;
        }
    }

    private static bool IsMetadataFailure(Exception ex) =>
        ex is ArgumentException or NotSupportedException or InvalidOperationException or COMException or IOException
            or FileFormatException or OverflowException or InvalidCastException or FormatException;
}
