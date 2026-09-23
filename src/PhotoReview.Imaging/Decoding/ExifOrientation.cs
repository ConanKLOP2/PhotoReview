using System;
using System.Globalization;
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

        try
        {
            if (metadata.ContainsQuery(ExifOrientationQuery))
            {
                var val = metadata.GetQuery(ExifOrientationQuery);
                if (val is not null)
                {
                    var orient = Convert.ToInt32(val, CultureInfo.InvariantCulture);
                    if (orient is >= 1 and <= 8) return orient;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            // Fall through to secondary query
        }

        try
        {
            if (metadata.ContainsQuery(WindowsOrientationQuery))
            {
                var val = metadata.GetQuery(WindowsOrientationQuery);
                if (val is not null)
                {
                    var orient = Convert.ToInt32(val, CultureInfo.InvariantCulture);
                    if (orient is >= 1 and <= 8) return orient;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            // Ignored, default to 1
        }

        return 1;
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
