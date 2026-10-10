using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Tests.Metadata;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// WP-03 (C-03): the WPF-free half of <see cref="ExifOrientation"/> -- <c>Normalize</c>, <c>ReadFromWic</c> -- and the WIC
/// metadata reader agreeing with the WPF one (orientation and <see cref="ExifSummary"/>) on the EXIF fixtures.
/// </summary>
public sealed class ExifOrientationPureTests : IDisposable
{
    private readonly TempRoot _root = new("wp03-exif");

    public void Dispose() => _root.Dispose();

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 5)]
    [InlineData(8, 8)]
    [InlineData(0, 1)]
    [InlineData(9, 1)]
    [InlineData(-3, 1)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(int.MinValue, 1)]
    public void Normalize_KeepsValidValues_AndMapsTheRestToOne(int orientation, int expected) =>
        Assert.Equal(expected, ExifOrientation.Normalize(orientation));

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public void IsTransposed_IsFiveToEight(int orientation, bool expected) =>
        Assert.Equal(expected, ExifOrientation.IsTransposed(orientation));

    private static Func<string, object?> Query(object? exif, object? windows) => name => name switch
    {
        ExifOrientation.ExifOrientationQuery => exif,
        ExifOrientation.WindowsOrientationQuery => windows,
        _ => throw new InvalidOperationException("unexpected query " + name),
    };

    [Theory]
    [InlineData((ushort)6, null, 6)]
    [InlineData((ushort)1, (ushort)8, 1)]   // a valid EXIF tag wins; the Windows policy is not consulted
    [InlineData((ushort)0, (ushort)3, 3)]   // invalid EXIF value -> Windows policy
    [InlineData((ushort)9, (ushort)7, 7)]
    [InlineData(null, (ushort)2, 2)]
    [InlineData(null, (ushort)9, 1)]
    [InlineData(null, null, 1)]
    public void ReadFromWic_ExifTagFirst_ThenTheWindowsPolicy(object? exif, object? windows, int expected) =>
        Assert.Equal(expected, ExifOrientation.ReadFromWic(Query(exif, windows)));

    [Fact]
    public void ReadFromWic_ArrayValue_UsesTheFirstElement() =>
        Assert.Equal(5, ExifOrientation.ReadFromWic(Query(new ushort[] { 5, 2 }, null)));

    [Fact]
    public void ReadFromWic_ThrowingQuery_IsOne_ButOutOfMemoryPropagates()
    {
        Assert.Equal(1, ExifOrientation.ReadFromWic(_ => throw new InvalidCastException()));
        Assert.Throws<InsufficientMemoryException>(() => ExifOrientation.ReadFromWic(_ => throw new InsufficientMemoryException())); // an OutOfMemoryException
        Assert.Throws<ArgumentNullException>(() => ExifOrientation.ReadFromWic(null!));
    }

    [Fact]
    public void ReadFromWic_TheWindowsQueryIsOnlyAskedWhenTheExifTagIsUnusable()
    {
        var asked = new List<string>();
        ExifOrientation.ReadFromWic(name => { asked.Add(name); return (ushort)6; });
        Assert.Equal([ExifOrientation.ExifOrientationQuery], asked);
    }

    public static TheoryData<string> ExifFixtures() => new() { "full", "partial", "none", "o3", "o6", "o8" };

    [Theory]
    [MemberData(nameof(ExifFixtures))]
    public void WicDecode_OrientationAndExifSummary_EqualTheWpfMetadataRead(string fixture)
    {
        var bytes = fixture switch
        {
            "full" => ExifTestData.EncodeJpegWithExif(),
            "partial" => ExifTestData.EncodeJpegWithPartialExif(),
            "none" => ExifTestData.EncodeJpegWithExif(withExif: false),
            _ => ExifTestData.EncodeJpegWithExif(orientation: ushort.Parse(fixture[1..], System.Globalization.CultureInfo.InvariantCulture)),
        };
        var path = _root.File(fixture + ".jpg", bytes);

        BitmapMetadata? metadata;
        using (var stream = File.OpenRead(path))
        {
            metadata = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad).Frames[0].Metadata as BitmapMetadata;
        }

        var wpfOrientation = ExifOrientation.Read(metadata);
        var wpfExif = WpfExifReader.Read(metadata);
        var decoded = new WicDirectDecoder(PixelBufferImageCodec.Instance).Decode(new DecodeRequest(path, new DecodeBox(16, 16)));
        ((IDisposable)decoded.PlatformImage).Dispose();
        var info = new WicDirectDecoder(PixelBufferImageCodec.Instance).ReadInfo(path);

        Assert.Equal(wpfOrientation, decoded.Orientation);
        Assert.Equal(wpfOrientation, info.Orientation);
        Assert.Equal(wpfExif, decoded.Exif);
    }
}
