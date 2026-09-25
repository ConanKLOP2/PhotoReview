using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Helper for reading and applying standard EXIF Orientation (tags 1 to 8).
/// </summary>
public static class ExifOrientation
{
    private const string ExifOrientationQuery = "/app1/ifd/{ushort=274}";
    private const string WindowsOrientationQuery = "System.Photo.Orientation";

    /// <summary>
    /// Reads the EXIF orientation tag from image metadata.
    /// Returns 1 (normal) if not found, unsupported, or invalid.
    /// </summary>
    public static int Read(BitmapMetadata? metadata)
    {
        if (metadata is null) return 1;
        var orientation = TryReadQuery(metadata, ExifOrientationQuery);
        return orientation is >= 1 and <= 8 ? (int)orientation : ReadFallback(metadata);
    }

    private static int ReadFallback(BitmapMetadata metadata)
    {
        var orientation = TryReadQuery(metadata, WindowsOrientationQuery);
        return orientation is >= 1 and <= 8 ? (int)orientation : 1;
    }

    /// <summary>
    /// One metadata query as an integer (the first element when the tag holds several values, exactly as WicDirect and the
    /// TurboJpeg byte parser read it), or null when absent or unreadable. WIC surfaces corrupt metadata as several exception
    /// types (a count-2 tag as <c>ushort[]</c>, an offset overflow as <see cref="OverflowException"/>); none of them may fail
    /// the decode of an otherwise valid image.
    /// </summary>
    private static long? TryReadQuery(BitmapMetadata metadata, string query)
    {
        try
        {
            return metadata.ContainsQuery(query) ? Metadata.ExifQueryInterpreter.AsInteger(metadata.GetQuery(query)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException
            or OverflowException or InvalidCastException or FormatException or IOException or COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies geometric transformations (rotations / flips) to match the specified EXIF orientation.
    /// Returns the original <paramref name="source"/> if orientation is 1 or invalid.
    /// The returned <see cref="BitmapSource"/> is always frozen. A rotated/flipped result is
    /// materialized on the calling thread (not a lazy <see cref="TransformedBitmap"/>), so decode
    /// workers pay the rotation cost instead of the UI thread at first render.
    /// </summary>
    public static BitmapSource Apply(BitmapSource source, int orientation)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (orientation is <= 1 or > 8)
        {
            if (source.CanFreeze && !source.IsFrozen)
            {
                source.Freeze();
            }
            return source;
        }

        Transform transform = CreateTransform(orientation);
        var transformed = new TransformedBitmap(source, transform);
        return WpfImageAdapter.Materialize(transformed);
    }

    /// <summary>
    /// Builds the WPF transform corresponding to an EXIF orientation value (1–8).
    /// </summary>
    public static Transform CreateTransform(int orientation)
    {
        return orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => CreateGroup(new ScaleTransform(-1, 1), new RotateTransform(270)),
            6 => new RotateTransform(90),
            7 => CreateGroup(new ScaleTransform(-1, 1), new RotateTransform(90)),
            8 => new RotateTransform(270),
            _ => Transform.Identity
        };
    }

    private static TransformGroup CreateGroup(Transform first, Transform second)
    {
        var group = new TransformGroup();
        group.Children.Add(first);
        group.Children.Add(second);
        return group;
    }
}
