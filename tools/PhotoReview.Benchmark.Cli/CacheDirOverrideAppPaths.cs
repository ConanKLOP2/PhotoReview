using PhotoReview.Core.Abstractions;

/// <summary>
/// perf(harness): wraps a real <see cref="IAppPaths"/> and overrides only the two disk-cache
/// directories (preview + thumbnail), so <c>--perf-session --cache-dir</c> can point them at a
/// batch-owned folder instead of the fixed <c>%LOCALAPPDATA%\PhotoReview\{cache,thumbnails}</c>
/// that <see cref="PhotoReview.Core.AppPaths"/> always uses -- even with
/// <c>PHOTOREVIEW_DATA_ROOT</c> set, those two directories never moved (see PerfSession.cs's
/// header comment on this). Every other path (config, journal, sessions, log, window placement)
/// is passed straight through so behavior there is unchanged.
/// </summary>
internal sealed class CacheDirOverrideAppPaths(IAppPaths inner, string cacheDirRoot) : IAppPaths
{
    public string ConfigFile => inner.ConfigFile;
    public string JournalFile => inner.JournalFile;
    public string SessionsDir => inner.SessionsDir;
    public string LogFile => inner.LogFile;
    public string WindowPlacementFile => inner.WindowPlacementFile;

    public string PreviewCacheDir { get; } = Path.Combine(cacheDirRoot, "cache");
    public string ThumbnailCacheDir { get; } = Path.Combine(cacheDirRoot, "thumbnails");
}
