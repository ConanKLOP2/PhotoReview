using System.IO;
using Xunit;

// Several migrated assertions mutate process-global state (the PHOTOREVIEW_DATA_ROOT
// environment variable and AppLog's static writer thread); those are grouped into the
// "GlobalState" collection below (DisableParallelization = true) instead of relying on
// assembly-wide serialization.
//
// T13b: assembly-wide parallelization is enabled. It used to be disabled because two
// PreviewImageServiceTests tests raced PreviewImageService's fire-and-forget background
// persist/prune workers against their own assertions:
//   - "Eviction forces a fresh source read on the next request" shared the downscaled-mode
//     _service; if the first GetPreviewAsync's background disk-cache write finished before
//     EvictCachedPath's RAM-only eviction and the second GetPreviewAsync call, the "fresh"
//     request was served from disk instead of doing a real source read. Fixed by giving that
//     test its own original-loading-mode service, which never persists to disk (see
//     PreviewImageServiceDiskCacheTests.OriginalModeDoesNotWriteToDiskCache), removing the race.
//   - "The disk cache directory is pruned instead of growing unbounded" polled
//     DirectoryBytes(diskDir) against a fixed per-iteration timeout that could trip before a
//     prune pass finished under heavy parallel load. Fixed by shutting down each iteration's
//     short-lived service (ShutdownPersistWorkersAsync) and awaiting
//     DiskCacheStore.WaitForPruneAsync before moving on, instead of a fixed poll deadline.
// Every test's own cache/data directory is already a fresh TempRoot per test instance, so no
// two tests share a directory. No assembly-level CollectionBehavior override is needed:
// xUnit's default (parallelization enabled) is safe now.

namespace PhotoReview.Tests.Unit;

/// <summary>Collection definition for tests that mutate global state (PHOTOREVIEW_DATA_ROOT, AppLog).</summary>
[CollectionDefinition("GlobalState", DisableParallelization = true)]
public sealed class GlobalStateCollection
{
}

/// <summary>A disposable temporary directory, mirroring the console suite's per-run root.</summary>
public sealed class TempRoot : IDisposable
{
    public string Path { get; }

    public TempRoot(string? name = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "PhotoReview-Test-" + (name is null ? string.Empty : name + "-") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string File(string relative, params byte[] content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content);
        return full;
    }

    public string Dir(string relative) => Directory.CreateDirectory(Combine(relative)).FullName;

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { }
    }
}

/// <summary>
/// A temporary directory that is also installed as PHOTOREVIEW_DATA_ROOT for the
/// duration of the test, so SessionStore/OperationJournal/AppLog write into it.
/// </summary>
public sealed class DataRootFixture : IDisposable
{
    private readonly string? _previous;

    public TempRoot Root { get; } = new("data");

    public DataRootFixture()
    {
        _previous = Environment.GetEnvironmentVariable("PHOTOREVIEW_DATA_ROOT");
        Environment.SetEnvironmentVariable("PHOTOREVIEW_DATA_ROOT", Root.Combine("app-data"));
    }

    public string Path => Root.Path;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PHOTOREVIEW_DATA_ROOT", _previous);
        Root.Dispose();
    }
}

/// <summary>
/// Locates the repository checkout that contains PhotoReview.App and caches the
/// source text that the "source presence" checks inspect.
/// </summary>
public static class ProjectSources
{
    public static string ProjectRoot { get; } = ResolveProjectRoot();

    private static string ResolveProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "PhotoReview.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    public static string AppPath(string fileName) =>
        System.IO.Path.Combine(ProjectRoot, "src", "PhotoReview.App", fileName);

    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Read(string fileName)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(fileName, out var text))
            {
                text = File.ReadAllText(AppPath(fileName));
                Cache[fileName] = text;
            }
            return text;
        }
    }

    public static string MainWindow => Read("MainWindow.xaml.cs");
    public static string MainWindowXaml => Read("MainWindow.xaml");
    public static string AppSettingsSource => Read("AppSettings.cs");
    public static string SettingsWindow => Read("SettingsWindow.xaml.cs");
    public static string SettingsWindowXaml => Read("SettingsWindow.xaml");
    public static string ImageSortService => Read("ImageSortService.cs");
    public static string RecoveryWindowXaml => Read("RecoveryWindow.xaml");
    public static string RecoveryWindow => Read("RecoveryWindow.xaml.cs");
    public static string WindowPlacementService => Read("WindowPlacementService.cs");
    public static string ThumbnailCache => Read("ThumbnailCache.cs");
    public static string OperationJournalSource =>
        File.Exists(AppPath("OperationJournal.cs"))
            ? Read("OperationJournal.cs")
            : File.ReadAllText(System.IO.Path.Combine(ProjectRoot, "src", "PhotoReview.Core", "FileActions", "OperationJournal.cs"));
    public static string PreviewImageServiceSource => Read("PreviewImageService.cs");
    public static string DiagnosticsWindowXaml => Read("DiagnosticsWindow.xaml");
    public static string AppCsproj => Read("PhotoReview.App.csproj");
}
