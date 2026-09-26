using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Tests.Fixtures;
using Xunit.Abstractions;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// The persist worker swallows every write failure (a lost write is just a cache miss), so a pixel format the JPEG cache
/// cannot encode would silently never be cached. Every opaque format a decoder can hand over must round-trip.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreviewCachePixelFormatTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempRoot _root = new("pv4-formats");

    public void Dispose() => _root.Dispose();

    private static BitmapSource Convert(PixelFormat format, BitmapPalette? palette = null)
    {
        var source = FixtureGenerator.CreateGradientCheckerboard(17, 11);
        // Materialized (frozen, palette kept) like a real decoder's output; an unfrozen FormatConvertedBitmap would be thread-affine.
        return WpfImageAdapter.Materialize(new FormatConvertedBitmap(source, format, palette, 0));
    }

    public static TheoryData<string> Formats() =>
    [
        "Gray8", "Gray16", "Gray32Float", "Bgr24", "Rgb24", "Bgr32", "Bgr101010", "Rgb48", "Bgr555", "Bgr565", "BlackWhite", "Indexed8", "Indexed4", "Cmyk32",
    ];

    private static (PixelFormat Format, BitmapPalette? Palette) Resolve(string name) => name switch
    {
        "Gray8" => (PixelFormats.Gray8, null),
        "Gray16" => (PixelFormats.Gray16, null),
        "Gray32Float" => (PixelFormats.Gray32Float, null),
        "Bgr24" => (PixelFormats.Bgr24, null),
        "Rgb24" => (PixelFormats.Rgb24, null),
        "Bgr32" => (PixelFormats.Bgr32, null),
        "Bgr101010" => (PixelFormats.Bgr101010, null),
        "Rgb48" => (PixelFormats.Rgb48, null),
        "Bgr555" => (PixelFormats.Bgr555, null),
        "Bgr565" => (PixelFormats.Bgr565, null),
        "BlackWhite" => (PixelFormats.BlackWhite, BitmapPalettes.BlackAndWhite),
        "Indexed8" => (PixelFormats.Indexed8, BitmapPalettes.Halftone256),
        "Indexed4" => (PixelFormats.Indexed4, BitmapPalettes.Halftone8),
        "Cmyk32" => (PixelFormats.Cmyk32, null),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory(DisplayName = "Every opaque pixel format a decoder can produce round-trips through the .pv4 writer and reader")]
    [MemberData(nameof(Formats))]
    public async Task OpaqueFormat_RoundTrips(string name)
    {
        var (format, palette) = Resolve(name);
        var bitmap = Convert(format, palette);
        var path = _root.Combine(name + ".pv4");

        await PreviewCacheFile.WriteAtomicallyAsync(bitmap, DecoderBackend.Wpf, orientation: 1, originalWidth: 0, originalHeight: 0, path);
        var read = PreviewCacheFile.Read(path);

        output.WriteLine($"{name}: {bitmap.Format} -> {read.Bitmap.Format}");
        Assert.Equal((17, 11), (read.Bitmap.PixelWidth, read.Bitmap.PixelHeight));
        Assert.Empty(Directory.GetFiles(_root.Path, "*.tmp"));
    }
}
