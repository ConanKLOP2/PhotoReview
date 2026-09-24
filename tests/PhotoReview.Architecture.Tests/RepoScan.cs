using System.Collections.Concurrent;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// Shared, cached view of the repository's C# sources for the source-scanning rules: one repo-root
/// lookup, one directory listing per subtree and one file read per file, regardless of how many
/// rules scan it. Build output (obj/bin) is never part of a listing.
/// </summary>
internal static class RepoScan
{
    public static string Root { get; } = FindRoot();

    private static readonly ConcurrentDictionary<string, string[]> Listings = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string[]> LineCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> TextCache = new(StringComparer.Ordinal);

    /// <summary>All *.cs files under <paramref name="relativeDir"/> (repo-relative), excluding obj/bin.</summary>
    public static IReadOnlyList<string> CsFiles(string relativeDir) =>
        Listings.GetOrAdd(relativeDir, static dir =>
        {
            var full = Path.Combine(Root, dir);
            if (!Directory.Exists(full)) return [];
            return Directory.GetFiles(full, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsBuildOutput(f))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
        });

    public static string[] Lines(string file) => LineCache.GetOrAdd(file, static f => File.ReadAllLines(f));

    public static string Text(string file) => TextCache.GetOrAdd(file, static f => File.ReadAllText(f));

    public static string Relative(string file) => Path.GetRelativePath(Root, file).Replace('\\', '/');

    /// <summary>
    /// Every line of every *.cs file under the given subtrees for which <paramref name="isViolation"/>
    /// returns true, formatted as "relative/path.cs:line: trimmed text". Files for which
    /// <paramref name="skipFile"/> (given the repo-relative path) is true are not scanned.
    /// </summary>
    public static List<string> FindLineViolations(
        Func<string, bool> isViolation, Func<string, bool>? skipFile = null, params string[] relativeDirs)
    {
        var violations = new List<string>();
        foreach (var dir in relativeDirs)
        {
            foreach (var file in CsFiles(dir))
            {
                var relative = Relative(file);
                if (skipFile?.Invoke(relative) == true) continue;
                var lines = Lines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (isViolation(lines[i]))
                        violations.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }
        return violations;
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PhotoReview.slnx")) ||
                Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root (looking for PhotoReview.slnx or .git)");
    }
}
