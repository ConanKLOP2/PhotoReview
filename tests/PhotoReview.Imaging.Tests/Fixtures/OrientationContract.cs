using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Tests.Quality;

namespace PhotoReview.Imaging.Tests.Fixtures;

/// <summary>
/// Eight 64x48 JPEGs (one per EXIF orientation 1-8), generated once per test class via
/// <see cref="IClassFixture{TFixture}"/> and shared by every decoder's orientation tests.
/// </summary>
public sealed class OrientationFixture : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "PhotoReview-OrientFixture-" + Guid.NewGuid().ToString("N"));

    public OrientationFixture()
    {
        Directory.CreateDirectory(_dir);
        for (ushort o = 1; o <= 8; o++)
        {
            FixtureGenerator.GenerateJpegWithOrientation(PathFor(o), 64, 48, o);
        }
    }

    public string PathFor(ushort orientation) => Path.Combine(_dir, $"orient_{orientation}.jpg");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The single source of truth for "what a 64x48 orientation-N fixture must look like after decode",
/// asserted identically against every backend (Wpf, WicDirect, TurboJpeg).
/// </summary>
public static class OrientationContract
{
    /// <summary>Orientations 1-8 as theory data.</summary>
    public static TheoryData<ushort> All => [1, 2, 3, 4, 5, 6, 7, 8];

    // Expected visual size and corner colours (TL, TR, BL, BR) of the fixture after applying the tag.
    private static readonly (int W, int H, string TL, string TR, string BL, string BR)[] Expected =
    [
        (64, 48, "Yellow", "Cyan", "Magenta", "White"),
        (64, 48, "Cyan", "Yellow", "White", "Magenta"),
        (64, 48, "White", "Magenta", "Cyan", "Yellow"),
        (64, 48, "Magenta", "White", "Yellow", "Cyan"),
        (48, 64, "Yellow", "Magenta", "Cyan", "White"),
        (48, 64, "Magenta", "Yellow", "White", "Cyan"),
        (48, 64, "White", "Cyan", "Magenta", "Yellow"),
        (48, 64, "Cyan", "White", "Yellow", "Magenta"),
    ];

    public static void AssertReadInfo(IImageDecoder decoder, string path, ushort orientation)
    {
        var expected = Expected[orientation - 1];
        var info = decoder.ReadInfo(path);
        Assert.Equal(64, info.PixelWidth);
        Assert.Equal(48, info.PixelHeight);
        Assert.Equal(orientation, info.Orientation);
        Assert.Equal(expected.W, info.Width);
        Assert.Equal(expected.H, info.Height);
    }

    public static void AssertDecodeCorners(IImageDecoder decoder, string path, ushort orientation)
    {
        var (w, h, tl, tr, bl, br) = Expected[orientation - 1];

        var decoded = decoder.Decode(new DecodeRequest(path, TargetWidth: 0, ApplyOrientation: true));
        Assert.Equal(w, decoded.PixelWidth);
        Assert.Equal(h, decoded.PixelHeight);
        // An undownscaled decode's OriginalWidth/Height must match its own pixel dimensions,
        // including for a transposing orientation (5-8), which swaps both.
        Assert.Equal(w, decoded.OriginalWidth);
        Assert.Equal(h, decoded.OriginalHeight);

        var bgra = ImageCompare.ToBgra32((BitmapSource)decoded.PlatformImage);
        AssertCorner(bgra, w, 1, 1, tl);
        AssertCorner(bgra, w, w - 2, 1, tr);
        AssertCorner(bgra, w, 1, h - 2, bl);
        AssertCorner(bgra, w, w - 2, h - 2, br);
    }

    private static void AssertCorner(byte[] bgra, int width, int x, int y, string color)
    {
        var offset = (y * width + x) * 4;
        byte b = bgra[offset], g = bgra[offset + 1], r = bgra[offset + 2];
        var ok = color switch
        {
            "Yellow" => r > 150 && g > 150 && b < 100,
            "Cyan" => r < 100 && g > 150 && b > 150,
            "Magenta" => r > 150 && g < 100 && b > 150,
            "White" => r > 180 && g > 180 && b > 180,
            _ => throw new ArgumentException($"Unknown color: {color}", nameof(color)),
        };
        Assert.True(ok, $"Expected {color} at ({x},{y}), got R={r},G={g},B={b}");
    }
}
