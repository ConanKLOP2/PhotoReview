using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Caching;

namespace PhotoReview.TestSupport.Windows.Fixtures;

/// <summary>
/// TC01: Builds a folder of synthetic JPEG photos for hotpath tests.
/// Generates varied sizes (4k, 6k, 8k), EXIF orientations, PNG, corrupted, and non-image files.
/// Does NOT commit images; stored in temp folder, cached per test session.
/// </summary>
public sealed class PhotoFolderBuilder
{
    private static readonly object _lock = new();
    private static string? _cachedFolder;
    private static readonly HashSet<string> _cachedHashes = [];

    /// <summary>Build (or reuse cached) folder with N synthetic JPEG images.</summary>
    public static string BuildFolder(int imageCount, string? tempDir = null)
    {
        lock (_lock)
        {
            tempDir ??= Path.Combine(Path.GetTempPath(), "PhotoReview-TC01-" + Guid.NewGuid().ToString("N"));
            if (_cachedFolder != null && Directory.Exists(_cachedFolder) && Directory.GetFiles(_cachedFolder, "*.jpg").Length >= imageCount)
                return _cachedFolder;

            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            _cachedFolder = tempDir;
            _cachedHashes.Clear();

            var sizes = new[] { (4000, 3000), (6000, 4000), (8000, 6000) };
            for (var i = 0; i < imageCount; i++)
            {
                var size = sizes[i % sizes.Length];
                var path = Path.Combine(tempDir, $"photo_{i:D3}.jpg");
                GenerateJpeg(path, size.Item1, size.Item2, i);
            }

            // Add one PNG
            var pngPath = Path.Combine(tempDir, "sample.png");
            if (!File.Exists(pngPath)) GeneratePng(pngPath);

            // Add one corrupted JPEG
            var corruptPath = Path.Combine(tempDir, "corrupt.jpg");
            if (!File.Exists(corruptPath)) File.WriteAllBytes(corruptPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 }); // Truncated JPEG header

            // Add one non-image file
            var txtPath = Path.Combine(tempDir, "notes.txt");
            if (!File.Exists(txtPath)) File.WriteAllText(txtPath, "not an image");

            return tempDir;
        }
    }

    private static void GenerateJpeg(string path, int width, int height, int seed)
    {
        if (File.Exists(path)) return;

        var bitmap = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];
        var rng = new Random(seed);
        rng.NextBytes(pixels);
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);

        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using (var fs = File.Create(path))
            encoder.Save(fs);
    }

    private static void GeneratePng(string path)
    {
        var bitmap = new WriteableBitmap(100, 100, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var pixels = new byte[100 * 100 * 4];
        Array.Fill(pixels, (byte)128); // Gray
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, 100, 100), pixels, 400, 0);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using (var fs = File.Create(path))
            encoder.Save(fs);
    }

    /// <summary>Clean up cached folder (called at session end, not per-test).</summary>
    public static void Cleanup()
    {
        lock (_lock)
        {
            if (_cachedFolder is not null && Directory.Exists(_cachedFolder))
            {
                try { Directory.Delete(_cachedFolder, recursive: true); }
                catch { /* best effort */ }
            }
            _cachedFolder = null;
            _cachedHashes.Clear();
        }
    }
}
