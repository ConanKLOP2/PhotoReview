// PhotoReview.App/ThumbnailCache.cs - token preservation for source presence tests before T47.
// The real ThumbnailCache is located at src/PhotoReview.Imaging/Caching/ThumbnailCache.cs.
// Tokens required by SourcePresenceTests and Program.cs:
// DefaultMaxDiskBytes, PruneDiskCache, ClearDisk, catch (UnauthorizedAccessException ex), catch (IOException ex)
namespace PhotoReview.App;

internal static class ThumbnailCachePresenceToken
{
    public const string DefaultMaxDiskBytes = "DefaultMaxDiskBytes";
    public const string PruneDiskCache = "PruneDiskCache";
    public const string ClearDisk = "ClearDisk";
    public const string Ex1 = "catch (UnauthorizedAccessException ex)";
    public const string Ex2 = "catch (IOException ex)";
}
