using System.IO;
using System.Text;
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

        return mode switch
        {
            ImageSortMode.SizeDescending => entries
                .OrderByDescending(entry => entry.Length ?? -1L)
                .ThenBy(entry => Path.GetFileName(entry.Path), comparer)
                .ToList(),
            ImageSortMode.SizeAscending => entries
                .OrderBy(entry => entry.Length ?? -1L)
                .ThenBy(entry => Path.GetFileName(entry.Path), comparer)
                .ToList(),
            _ => entries
                .OrderBy(entry => Path.GetFileName(entry.Path), comparer)
                .ToList()
        };
    }

    public static List<string> Sort(
        IEnumerable<string> files,
        ImageSortMode mode,
        INaturalComparer? naturalComparer = null,
        ILog? log = null)
    {
        var comparer = naturalComparer ?? ManagedNaturalComparer.Instance;

        return mode switch
        {
            ImageSortMode.SizeDescending => files
                .OrderByDescending(path => GetFileSize(path, log))
                .ThenBy(Path.GetFileName, comparer)
                .ToList(),
            ImageSortMode.SizeAscending => files
                .OrderBy(path => GetFileSize(path, log))
                .ThenBy(Path.GetFileName, comparer)
                .ToList(),
            _ => files
                .OrderBy(Path.GetFileName, comparer)
                .ToList()
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

    public static string NaturalKey(string name)
    {
        var builder = new StringBuilder(name.Length);
        for (var index = 0; index < name.Length;)
        {
            if (!char.IsDigit(name[index]))
            {
                builder.Append(char.ToLowerInvariant(name[index++]));
                continue;
            }

            var end = index;
            while (end < name.Length && char.IsDigit(name[end])) end++;
            var digits = name[index..end].TrimStart('0');
            if (digits.Length == 0) digits = "0";
            builder.Append(digits.Length.ToString("D10", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(digits);
            builder.Append('\0');
            index = end;
        }
        return builder.ToString();
    }
}
