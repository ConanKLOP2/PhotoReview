using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Catalog;

public static class ImageSortService
{
    public static List<CatalogEntry> SortEntries(
        IEnumerable<CatalogEntry> entries,
        ImageSortMode mode,
        INaturalComparer? naturalComparer = null)
    {
        var comparer = naturalComparer ?? ManagedNaturalComparer.Instance;
        string? NameOf(CatalogEntry entry) => Path.GetFileName(entry.Path);

        return mode switch
        {
            ImageSortMode.SizeDescending => ThenByName(entries.OrderByDescending(entry => entry.Length ?? -1L), NameOf, comparer).ToList(),
            ImageSortMode.SizeAscending => ThenByName(entries.OrderBy(entry => entry.Length ?? -1L), NameOf, comparer).ToList(),
            _ => OrderByName(entries, NameOf, comparer).ToList()
        };
    }

    // perf: ManagedNaturalComparer.Compare builds two natural keys per call, i.e. O(n log n) StringBuilder allocations
    // (6 s for 200 000 files). For that comparer the key of each name is computed once and compared allocation-free;
    // the resulting order is identical (same key, then case-insensitive, then ordinal tie-breaks). Any other comparer
    // (e.g. a Win32 StrCmpLogical wrapper) keeps the generic path.
    private static IOrderedEnumerable<T> OrderByName<T>(IEnumerable<T> items, Func<T, string?> nameOf, INaturalComparer comparer) =>
        comparer is ManagedNaturalComparer
            ? items.OrderBy(item => NameKey.Of(nameOf(item)), NameKeyComparer.Instance)
            : items.OrderBy(nameOf, comparer);

    private static IOrderedEnumerable<T> ThenByName<T>(IOrderedEnumerable<T> ordered, Func<T, string?> nameOf, INaturalComparer comparer) =>
        comparer is ManagedNaturalComparer
            ? ordered.ThenBy(item => NameKey.Of(nameOf(item)), NameKeyComparer.Instance)
            : ordered.ThenBy(nameOf, comparer);

    private readonly record struct NameKey(string? Name, string? Key)
    {
        public static NameKey Of(string? name) => new(name, name is null ? null : ManagedNaturalComparer.BuildNaturalKey(name));
    }

    private sealed class NameKeyComparer : IComparer<NameKey>
    {
        public static readonly NameKeyComparer Instance = new();

        public int Compare(NameKey x, NameKey y)
        {
            if (ReferenceEquals(x.Name, y.Name)) return 0;
            if (x.Name is null) return -1;
            if (y.Name is null) return 1;
            var cmp = string.Compare(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            cmp = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(x.Name, y.Name, StringComparison.Ordinal);
        }
    }

    public static List<string> Sort(
        IEnumerable<string> files,
        ImageSortMode mode,
        INaturalComparer? naturalComparer = null,
        ILog? log = null)
    {
        var comparer = naturalComparer ?? ManagedNaturalComparer.Instance;

        static string? NameOf(string path) => Path.GetFileName(path);

        return mode switch
        {
            ImageSortMode.SizeDescending => ThenByName(files.OrderByDescending(path => GetFileSize(path, log)), NameOf, comparer).ToList(),
            ImageSortMode.SizeAscending => ThenByName(files.OrderBy(path => GetFileSize(path, log)), NameOf, comparer).ToList(),
            _ => OrderByName(files, NameOf, comparer).ToList()
        };
    }

    public static List<string> Sort(
        IEnumerable<string> files,
        string? mode,
        INaturalComparer? naturalComparer = null,
        ILog? log = null)
    {
        var sortMode = mode?.ToUpperInvariant() switch
        {
            "SIZE" or "SIZEDESCENDING" => ImageSortMode.SizeDescending,
            "SIZEASCENDING" => ImageSortMode.SizeAscending,
            _ => ImageSortMode.Name
        };

        return Sort(files, sortMode, naturalComparer, log);
    }

    private static long GetFileSize(string path, ILog? log)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            log?.Error($"Failed to read file size for {path}", ex);
            return -1;
        }
    }
}
