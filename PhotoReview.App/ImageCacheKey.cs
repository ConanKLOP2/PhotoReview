using System.IO;

namespace PhotoReview.App;

/// <summary>Identity of a decoded bitmap, including its source version and decode quality.</summary>
// Constructor is private so Create(...) is the only way to build a valid instance;
// a public one would let callers bypass the full-path/uppercase normalization below.
public readonly record struct ImageCacheKey
{
    public string Path { get; }
    public long Length { get; }
    public long LastWriteUtcTicks { get; }
    public bool IsOriginal { get; }
    public int TargetWidth { get; }

    private ImageCacheKey(string path, long length, long lastWriteUtcTicks, bool isOriginal, int targetWidth)
    {
        Path = path;
        Length = length;
        LastWriteUtcTicks = lastWriteUtcTicks;
        IsOriginal = isOriginal;
        TargetWidth = targetWidth;
    }

    public static ImageCacheKey Create(string path, bool isOriginal, int targetWidth) =>
        Create(new FileInfo(path), isOriginal, targetWidth);

    /// <summary>Reuses a FileInfo the caller already fetched instead of stat-ing the path again.</summary>
    public static ImageCacheKey Create(FileInfo info, bool isOriginal, int targetWidth)
    {
        if (!info.Exists) throw new FileNotFoundException("Image source no longer exists", info.FullName);
        var fullPath = System.IO.Path.GetFullPath(info.FullName).ToUpperInvariant();
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
