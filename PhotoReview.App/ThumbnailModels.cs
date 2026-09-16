namespace PhotoReview.App;

public sealed record ThumbnailCacheOptions
{
    public int MaxPixelSize { get; init; } = 800;
    public string? CacheDirectory { get; init; }
}

public sealed record ThumbnailResult(string SourcePath, int RequestedMaxPixelSize, System.Windows.Media.Imaging.BitmapImage Image);
