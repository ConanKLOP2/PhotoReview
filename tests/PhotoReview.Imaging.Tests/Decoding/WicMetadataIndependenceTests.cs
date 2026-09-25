using System;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Metadata;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>The orientation and the photo-information reads of one WIC frame fail independently.</summary>
[Trait("Category", "HotPath")]
public sealed class WicMetadataIndependenceTests
{
    private const string OrientationQuery = "/app1/ifd/{ushort=274}";

    [Fact(DisplayName = "An orientation already read survives a failure while reading the photo information")]
    public void ExifFailure_KeepsTheOrientation()
    {
        object? Query(string name) => name == OrientationQuery
            ? (ushort)6
            : throw new FormatException("damaged exif block");

        var orientation = WicDirectDecoder.ReadMetadataValues(Query, readOrientation: true, ExifQueryInterpreter.JpegIfdRoot, out var exif);

        Assert.Equal(6, orientation);
        Assert.Null(exif);
    }

    [Fact(DisplayName = "A failing orientation query still lets the photo information through, with orientation 1")]
    public void OrientationFailure_KeepsTheExif()
    {
        object? Query(string name) => name.Contains("274", StringComparison.Ordinal) || name == "System.Photo.Orientation"
            ? throw new InvalidCastException("bad orientation tag")
            : name.EndsWith("{ushort=271}", StringComparison.Ordinal) ? "Canon" : null;

        var orientation = WicDirectDecoder.ReadMetadataValues(Query, readOrientation: true, ExifQueryInterpreter.JpegIfdRoot, out var exif);

        Assert.Equal(1, orientation);
        Assert.Equal("Canon", exif?.CameraMake);
    }
}
