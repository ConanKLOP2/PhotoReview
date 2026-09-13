namespace PhotoReview.App;

/// <summary>Options controlling thumbnail decoding and disk caching.</summary>
public sealed record ThumbnailCacheOptions
{
    public int MaxPixelSize { get; init; } = 800;
    public string? CacheDirectory { get; init; }
}

/// <summary>Result metadata returned with a decoded thumbnail.</summary>
public sealed record ThumbnailResult(string SourcePath, int RequestedMaxPixelSize, System.Windows.Media.Imaging.BitmapImage Image);
