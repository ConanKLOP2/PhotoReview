using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoReview.Imaging.Tests.Fixtures;

/// <summary>
/// Generates procedural test fixtures (JPEG gradients, checkerboards, EXIF orientations, ICC, corrupt files)
/// on the fly in temporary test directories without committing large binary images to source control.
/// </summary>
public static class FixtureGenerator
{
    public const string DisplayP3ProfileSha256 = "CB51DE38E482EE974C0C76B9689E16AAD04BAD16E226FED2F30C842D15FF3A3D";

    private static readonly string SystemColorDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "spool", "drivers", "color");

    /// <summary>
    /// Creates a procedural BGRA32 <see cref="BitmapSource"/> with a color gradient, checkerboard pattern,
    /// and 4 uniquely colored corners (useful for orientation and rotation verification).
    /// </summary>
    public static BitmapSource CreateGradientCheckerboard(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        var tileSize = Math.Max(4, Math.Min(width, height) / 8);
        var cornerSize = Math.Max(2, Math.Min(8, Math.Min(width, height) / 4));

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            var g = (byte)(y * 255 / Math.Max(1, height - 1));

            for (var x = 0; x < width; x++)
            {
                var offset = rowOffset + (x * 4);
                var r = (byte)(x * 255 / Math.Max(1, width - 1));
                var isTileEven = ((x / tileSize) + (y / tileSize)) % 2 == 0;
                var b = isTileEven ? (byte)210 : (byte)45;

                // Color 4 corners distinctly:
                // Top-Left: Yellow (R=255, G=255, B=0)
                if (x < cornerSize && y < cornerSize)
                {
                    r = 255; g = 255; b = 0;
                }
                // Top-Right: Cyan (R=0, G=255, B=255)
                else if (x >= width - cornerSize && y < cornerSize)
                {
                    r = 0; g = 255; b = 255;
                }
                // Bottom-Left: Magenta (R=255, G=0, B=255)
                else if (x < cornerSize && y >= height - cornerSize)
                {
                    r = 255; g = 0; b = 255;
                }
                // Bottom-Right: White (R=255, G=255, B=255)
                else if (x >= width - cornerSize && y >= height - cornerSize)
                {
                    r = 255; g = 255; b = 255;
                }

                pixels[offset] = b;
                pixels[offset + 1] = g;
                pixels[offset + 2] = r;
                pixels[offset + 3] = 255; // Alpha
            }
        }

        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Generates a JPEG file from a gradient checkerboard bitmap.
    /// </summary>
    public static string GenerateGradientJpeg(string targetPath, int width, int height, int quality = 90)
    {
        var bitmap = CreateGradientCheckerboard(width, height);
        SaveJpeg(bitmap, targetPath, quality);
        return targetPath;
    }

    /// <summary>
    /// Generates a PNG file (24-bit Bgr24 or 32-bit Bgra32).
    /// </summary>
    public static string GeneratePng(string targetPath, int width, int height, bool is32Bit = true)
    {
        var bitmap = CreateGradientCheckerboard(width, height);
        SavePng(bitmap, targetPath, is32Bit);
        return targetPath;
    }

    /// <summary>
    /// Generates a JPEG file with a specific EXIF orientation tag (1 to 8).
    /// </summary>
    public static string GenerateJpegWithOrientation(string targetPath, int width, int height, ushort orientation, int quality = 90)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(orientation, (ushort)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(orientation, (ushort)8);

        var bitmap = CreateGradientCheckerboard(width, height);
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);

        SaveJpeg(bitmap, targetPath, quality, metadata);
        return targetPath;
    }

    /// <summary>
    /// Generates a JPEG file with an embedded ICC profile.
    /// </summary>
    public static string GenerateJpegWithIcc(string targetPath, int width, int height, string? iccPath = null, int quality = 90)
    {
        var bitmap = CreateGradientCheckerboard(width, height);

        var profilePath = iccPath ?? GetBundledDisplayP3ProfilePath();
        if (!File.Exists(profilePath))
            throw new FileNotFoundException("The deterministic ICC test profile is missing.", profilePath);

        var colorContext = new ColorContext(new Uri(profilePath));
        var colorContexts = new ReadOnlyCollection<ColorContext>(new[] { colorContext });

        SaveJpeg(bitmap, targetPath, quality, metadata: null, colorContexts: colorContexts);
        return targetPath;
    }

    public static string GetBundledDisplayP3ProfilePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Fixtures", "ColorProfiles", "DisplayP3-v4.icc");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate tests/Fixtures/ColorProfiles/DisplayP3-v4.icc from the test output directory.");
    }

    public static void AssertBundledDisplayP3ProfileIntegrity()
    {
        var bytes = File.ReadAllBytes(GetBundledDisplayP3ProfilePath());
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actual, DisplayP3ProfileSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Unexpected Display P3 profile SHA-256: {actual}.");
    }

    public static string GeneratePngWithIcc(string targetPath, int width, int height)
    {
        var opaque = CreateGradientCheckerboard(width, height);
        int stride = width * 4;
        var pixels = new byte[stride * height];
        opaque.CopyPixels(pixels, stride, 0);
        pixels[3] = 64; // deterministic semi-transparent top-left pixel
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        var colorContext = new ColorContext(new Uri(GetBundledDisplayP3ProfilePath()));
        var frame = BitmapFrame.Create(
            bitmap,
            thumbnail: null,
            metadata: null,
            colorContexts: new ReadOnlyCollection<ColorContext>(new[] { colorContext }));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(frame);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        using var stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
        return targetPath;
    }

    /// <summary>
    /// Creates a corrupted JPEG file by truncating an existing valid JPEG.
    /// </summary>
    public static string GenerateTruncatedJpeg(string sourceJpegPath, string targetPath, double ratio = 0.5)
    {
        var bytes = File.ReadAllBytes(sourceJpegPath);
        var truncatedLength = Math.Max(1, (int)(bytes.Length * ratio));
        var truncatedBytes = new byte[truncatedLength];
        Array.Copy(bytes, truncatedBytes, truncatedLength);

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllBytes(targetPath, truncatedBytes);
        return targetPath;
    }

    /// <summary>
    /// Creates a 0-byte file with an image extension.
    /// </summary>
    public static string GenerateZeroByteFile(string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllBytes(targetPath, Array.Empty<byte>());
        return targetPath;
    }

    /// <summary>
    /// Creates a text file with a .jpg extension.
    /// </summary>
    public static string GenerateTextFile(string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(targetPath, "This is plain text, not a valid JPEG or image format.");
        return targetPath;
    }

    /// <summary>
    /// Saves a <see cref="BitmapSource"/> as a JPEG file.
    /// </summary>
    public static void SaveJpeg(
        BitmapSource source,
        string path,
        int quality = 90,
        BitmapMetadata? metadata = null,
        ReadOnlyCollection<ColorContext>? colorContexts = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var encoder = new JpegBitmapEncoder
        {
            QualityLevel = quality
        };

        var frame = BitmapFrame.Create(
            source,
            thumbnail: null,
            metadata: metadata,
            colorContexts: colorContexts);

        encoder.Frames.Add(frame);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    /// <summary>
    /// Saves a <see cref="BitmapSource"/> as a PNG file.
    /// </summary>
    public static void SavePng(BitmapSource source, string path, bool is32Bit = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        BitmapSource outputSource = source;
        if (!is32Bit && source.Format != PixelFormats.Bgr24)
        {
            outputSource = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(outputSource));

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    /// <summary>
    /// Attempts to find a standard sRGB or display color profile from the Windows system color directory.
    /// </summary>
    public static string? FindDefaultSystemIccProfile()
    {
        if (!Directory.Exists(SystemColorDir))
        {
            return null;
        }

        var srgb = Path.Combine(SystemColorDir, "sRGB Color Space Profile.icm");
        if (File.Exists(srgb))
        {
            return srgb;
        }

        var candidates = Directory.GetFiles(SystemColorDir, "*.ic*");
        return candidates.Length > 0 ? candidates[0] : null;
    }
}
