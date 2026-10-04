using System.IO;
using System.Security.Cryptography;
using System.Text;
using PhotoReview.Imaging.Caching;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>Test-side helper that places a PNG at the path <see cref="ThumbnailCache"/> reads for a source file (the cache no longer writes it itself).</summary>
internal static class ThumbnailCacheFiles
{
    public static string PathFor(string diskDirectory, string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        var stamp = $"{sourcePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{ThumbnailCache.MaxThumbnailWidth}|orient=1";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp))).ToLowerInvariant();
        return Path.Combine(diskDirectory, key + ".png");
    }

    public static string Write(string diskDirectory, string sourcePath, byte[] pngBytes)
    {
        Directory.CreateDirectory(diskDirectory);
        var path = PathFor(diskDirectory, sourcePath);
        File.WriteAllBytes(path, pngBytes);
        return path;
    }
}
