using PhotoReview.Core.IO;

namespace PhotoReview.Benchmark.Cli;

/// <summary>
/// Containment checks shared by every CLI mode that writes reports or owns a cache folder. Paths are compared after
/// resolving existing symlinks/junctions (<see cref="PhysicalFileSystem.ResolveRealPath"/>), so a junction that points
/// into the photo folder cannot hide a write target from a purely lexical prefix test (T-B-03, T-B-13).
/// Violations throw <see cref="InvalidOperationException"/>, which every mode already maps to exit code 2.
/// </summary>
internal static class ToolPathGuard
{
    /// <summary>Marker a tool drops into a cache folder it owns; a non-empty <c>--cache-dir</c> without it is refused.</summary>
    internal const string CacheMarker = ".photoreview-benchmark-cache";

    private static readonly PhysicalFileSystem FileSystem = new();

    /// <summary>The absolute path with every existing reparse point resolved and no trailing separator.</summary>
    internal static string Real(string path) => Path.TrimEndingDirectorySeparator(FileSystem.ResolveRealPath(path));

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies below it (resolved, case-insensitive).</summary>
    internal static bool IsSameOrUnder(string path, string root)
    {
        var p = Real(path);
        var r = Real(root);
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when either path equals, contains or lies inside the other.</summary>
    internal static bool Overlaps(string a, string b) => IsSameOrUnder(a, b) || IsSameOrUnder(b, a);

    /// <summary>A report directory must not equal, sit inside or contain the photo folder: a same-named user file could be overwritten.</summary>
    internal static void EnsureOutputDirectory(string outputDirectory, string photoFolder)
    {
        if (Overlaps(outputDirectory, photoFolder))
            throw new InvalidOperationException($"output directory '{outputDirectory}' must not equal, contain or sit inside the photo folder '{photoFolder}'");
    }

    /// <summary>A report file must not live in the photo folder or anywhere below it.</summary>
    internal static void EnsureOutputFile(string outputFile, string photoFolder)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputFile)) ?? Path.GetFullPath(outputFile);
        if (IsSameOrUnder(directory, photoFolder))
            throw new InvalidOperationException($"output file '{outputFile}' must not be inside the photo folder '{photoFolder}'");
    }

    /// <summary>
    /// <c>--cache-dir</c> folders are pruned and cleaned (legacy *.png removal, LRU pruning), so the directory must be
    /// disposable: not overlapping any of <paramref name="mustNotOverlap"/> (source, outDir, app data folder), and either
    /// missing, empty, or already carrying <see cref="CacheMarker"/>.
    /// </summary>
    internal static void EnsureCacheDirectory(string cacheDir, params (string Path, string Name)[] mustNotOverlap)
    {
        foreach (var (path, name) in mustNotOverlap)
        {
            if (Overlaps(cacheDir, path))
                throw new InvalidOperationException($"--cache-dir '{cacheDir}' must not equal, contain or sit inside the {name} '{path}'");
        }

        if (Directory.Exists(cacheDir)
            && !File.Exists(Path.Combine(cacheDir, CacheMarker))
            && Directory.EnumerateFileSystemEntries(cacheDir).Any())
            throw new InvalidOperationException($"--cache-dir '{cacheDir}' is not empty and was not created by this tool (no {CacheMarker} marker); its cache folders would be pruned. Use an empty folder");
    }

    /// <summary>Creates the cache folder and its marker (idempotent) once <see cref="EnsureCacheDirectory"/> accepted it.</summary>
    internal static void ClaimCacheDirectory(string cacheDir)
    {
        Directory.CreateDirectory(cacheDir);
        var marker = Path.Combine(cacheDir, CacheMarker);
        if (!File.Exists(marker)) File.WriteAllText(marker, "created by the PhotoReview benchmark CLI; its cache and thumbnails folders are pruned\n");
    }

    /// <summary>
    /// Writes a report file without ever writing THROUGH its final path: the text goes to a sibling temp file which then
    /// replaces the leaf (rename semantics). A leaf that is a symlink or a hard link to a user's photo is therefore
    /// replaced by a regular file and the photo's bytes stay untouched (R13); directory-level checks alone cannot see this.
    /// </summary>
    internal static async Task WriteReportFileAsync(string path, string content, System.Text.Encoding? encoding = null)
    {
        var full = Path.GetFullPath(path);
        var temp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, encoding ?? new System.Text.UTF8Encoding(false));
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>The real app data folder (<c>%LOCALAPPDATA%\PhotoReview</c>), which no tool-owned folder may overlap.</summary>
    internal static string AppDataFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview");
}
