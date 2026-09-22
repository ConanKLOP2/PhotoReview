using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Caching;

namespace PhotoReview.TestSupport.Windows.Fixtures;

/// <summary>
/// TS02 TC01: Builds a folder of synthetic JPEG photos for hotpath tests.
/// Deterministic smooth content (gradient + seeded blocks), encoded once per distinct size.
/// Cached per test process (Lazy), deleted on process exit.
/// Purges stale PhotoReview-TC01-* folders older than 6 hours on first use.
/// </summary>
public sealed class PhotoFolderBuilder : IDisposable
{
    private static readonly object _lock = new();
    private static readonly Lazy<PhotoFolderBuilder> _instance = new(() => new PhotoFolderBuilder());

    // Master encoded JPEGs, one per size
    private readonly Dictionary<(int width, int height), byte[]> _masterEncodedJpegs = [];

    // Process-level cache folder (built once per process)
    private string? _cachedFolder;

    // Size config
    private static readonly (int width, int height)[] SizeCfg = [(4000, 3000), (6000, 4000), (8000, 6000)];

    static PhotoFolderBuilder()
    {
        // Register process exit handler to cleanup cached folder
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _instance.Value.Cleanup();
    }

    private PhotoFolderBuilder()
    {
        // Purge stale PhotoReview-TC01-* folders (older than 6 hours) on first instantiation
        PurgeStaleTemporaryFolders();

        // Encode master JPEGs (once per size)
        foreach (var (width, height) in SizeCfg)
        {
            _masterEncodedJpegs[(width, height)] = EncodeMasterJpeg(width, height);
        }
    }

    /// <summary>Build (or reuse cached) folder with N synthetic JPEG images.</summary>
    public static string BuildFolder(int imageCount, string? tempDir = null)
    {
        var builder = _instance.Value;
        lock (_lock)
        {
            // Reuse cached folder if it exists and has enough images
            if (builder._cachedFolder != null && Directory.Exists(builder._cachedFolder))
            {
                int existingCount = Directory.GetFiles(builder._cachedFolder, "*.jpg").Length;
                if (existingCount >= imageCount)
                    return builder._cachedFolder;
            }

            // Create new cache folder (use fixed name in temp to avoid leak on cache hit)
            tempDir ??= Path.Combine(Path.GetTempPath(), "PhotoReview-TC01-" + Environment.ProcessId + "-cache");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

            builder._cachedFolder = tempDir;

            // Generate JPEG files by cycling through sizes and copying from master with unique COM
            for (int i = 0; i < imageCount; i++)
            {
                var size = SizeCfg[i % SizeCfg.Length];
                var path = Path.Combine(tempDir, $"photo_{i:D3}.jpg");

                if (!File.Exists(path))
                {
                    // Copy master and insert unique COM segment (fingerprint)
                    var uniqueBytes = InsertUniqueComSegment(builder._masterEncodedJpegs[size], i);
                    File.WriteAllBytes(path, uniqueBytes);
                }
            }

            // Add one PNG
            var pngPath = Path.Combine(tempDir, "sample.png");
            if (!File.Exists(pngPath)) GeneratePng(pngPath);

            // Add one corrupted JPEG (truncated header)
            var corruptPath = Path.Combine(tempDir, "corrupt.jpg");
            if (!File.Exists(corruptPath)) File.WriteAllBytes(corruptPath, [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]);

            // Add one non-image file
            var txtPath = Path.Combine(tempDir, "notes.txt");
            if (!File.Exists(txtPath)) File.WriteAllText(txtPath, "not an image");

            return tempDir;
        }
    }

    /// <summary>Encode a master JPEG with deterministic smooth content (gradient + seeded blocks).</summary>
    private static byte[] EncodeMasterJpeg(int width, int height)
    {
        // Create deterministic content: smooth gradient + seeded blocks
        var bitmap = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];

        // Fill with smooth gradient (no random noise = much smaller JPEG)
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = (y * width + x) * 4;
                // Gradient: R increases horizontally, G increases vertically, B is fixed
                byte r = (byte)(255 * x / width);
                byte g = (byte)(255 * y / height);
                byte b = 128;
                byte a = 255;

                // BGRA format for WriteableBitmap
                pixels[idx] = b;
                pixels[idx + 1] = g;
                pixels[idx + 2] = r;
                pixels[idx + 3] = a;
            }
        }

        // Add seeded deterministic blocks (make it slightly less uniform)
        var rng = new Random(width * height); // Seed based on size for determinism
        int blockSize = 100;
        for (int by = 0; by < height; by += blockSize * 2)
        {
            for (int bx = 0; bx < width; bx += blockSize * 2)
            {
                int seed = by * width + bx;
                byte val = (byte)(128 + (seed % 128));

                for (int y = by; y < Math.Min(by + blockSize, height); y++)
                {
                    for (int x = bx; x < Math.Min(bx + blockSize, width); x++)
                    {
                        int idx = (y * width + x) * 4;
                        // Blend with existing gradient
                        pixels[idx] = (byte)((pixels[idx] + val) / 2); // B
                        pixels[idx + 1] = (byte)((pixels[idx + 1] + val) / 2); // G
                        pixels[idx + 2] = (byte)((pixels[idx + 2] + val) / 2); // R
                    }
                }
            }
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);

        // Encode to JPEG
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using (var ms = new MemoryStream())
        {
            encoder.Save(ms);
            return ms.ToArray();
        }
    }

    /// <summary>Insert a unique COM segment after SOI to make each file have different bytes.</summary>
    private static byte[] InsertUniqueComSegment(byte[] masterJpeg, int fileIndex)
    {
        // JPEG structure: FFD8 (SOI) ... FFIXX (other segments) ... FFD9 (EOI)
        // Insert COM segment (FFFE) right after SOI with unique content

        var result = new MemoryStream();

        // Find SOI and copy it
        if (masterJpeg.Length < 2 || masterJpeg[0] != 0xFF || masterJpeg[1] != 0xD8)
            return masterJpeg; // Fallback: return original if not a valid JPEG

        result.Write(masterJpeg, 0, 2); // Write SOI

        // Create COM segment with unique index
        byte[] comment = System.Text.Encoding.UTF8.GetBytes($"PhotoReview-TS02-{fileIndex:D6}");
        ushort comLength = (ushort)(comment.Length + 2); // +2 for length field itself

        // COM marker: FFFE
        result.WriteByte(0xFF);
        result.WriteByte(0xFE);

        // Length (big-endian)
        result.WriteByte((byte)(comLength >> 8));
        result.WriteByte((byte)(comLength & 0xFF));

        // Comment data
        result.Write(comment, 0, comment.Length);

        // Copy rest of JPEG (starting after SOI)
        result.Write(masterJpeg, 2, masterJpeg.Length - 2);

        return result.ToArray();
    }

    /// <summary>Generate a minimal PNG (100x100).</summary>
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

    /// <summary>Purge stale PhotoReview-TC01-* folders older than 6 hours.</summary>
    private static void PurgeStaleTemporaryFolders()
    {
        var tempPath = Path.GetTempPath();
        var cutoffTime = DateTime.Now.AddHours(-6);

        try
        {
            var staleDirectories = Directory.GetDirectories(tempPath, "PhotoReview-TC01-*")
                .Where(dir =>
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        return dirInfo.CreationTime < cutoffTime;
                    }
                    catch
                    {
                        return false;
                    }
                })
                .ToList();

            foreach (var dir in staleDirectories)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // Best effort; log silently
                }
            }
        }
        catch
        {
            // Failure to purge is not fatal
        }
    }

    /// <summary>Clean up cached folder on process exit.</summary>
    private void Cleanup()
    {
        lock (_lock)
        {
            if (_cachedFolder is not null && Directory.Exists(_cachedFolder))
            {
                try
                {
                    Directory.Delete(_cachedFolder, recursive: true);
                }
                catch
                {
                    // Best effort
                }
            }
            _cachedFolder = null;
        }
    }

    public void Dispose()
    {
        Cleanup();
    }
}
