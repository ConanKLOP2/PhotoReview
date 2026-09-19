using System.IO;

namespace PhotoReview.App.Tests;

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
