using System.IO;

namespace PhotoReview.App;

/// <summary>Identity of a decoded bitmap, including its source version and decode quality.</summary>
public readonly record struct ImageCacheKey(string Path, long Length, long LastWriteUtcTicks, bool IsOriginal, int TargetWidth)
{
    public static ImageCacheKey Create(string path, bool isOriginal, int targetWidth)
    {
        var fullPath = System.IO.Path.GetFullPath(path).ToUpperInvariant();
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Image source no longer exists", fullPath);
        return new ImageCacheKey(fullPath, info.Length, info.LastWriteTimeUtc.Ticks, isOriginal, isOriginal ? 0 : targetWidth);
    }

    public bool MatchesCurrentSource()
    {
        try
        {
            var info = new FileInfo(Path);
            return info.Exists && info.Length == Length && info.LastWriteTimeUtc.Ticks == LastWriteUtcTicks;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
