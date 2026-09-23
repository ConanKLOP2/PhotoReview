namespace PhotoReview.Imaging.Caching;

/// <summary>
/// Single decision point for whether <see cref="SourceBytesCache"/> is enabled. Resolved once from
/// <c>AppSettings.UseSourceBytesCache</c> in <c>App.ConfigureServices</c>; consumers read
/// <see cref="Cache"/> instead of each re-checking the setting.
/// </summary>
public sealed class SourceBytesCachePolicy(SourceBytesCache? cache)
{
    public SourceBytesCache? Cache { get; } = cache;

    public bool Enabled => Cache is not null;
}
