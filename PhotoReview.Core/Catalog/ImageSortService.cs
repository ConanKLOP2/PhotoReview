using System.IO;
using System.Text;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Catalog;

public static class ImageSortService
{
    public static List<string> Sort(
        IEnumerable<string> files,
        string? mode,
        INaturalComparer? naturalComparer = null,
        ILog? log = null)
    {
        var comparer = naturalComparer ?? ManagedNaturalComparer.Instance;

        if (string.Equals(mode, "Size", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "SizeDescending", StringComparison.OrdinalIgnoreCase))
        {
            return files
                .OrderByDescending(path => GetFileSize(path, log))
                .ThenBy(Path.GetFileName, comparer)
                .ToList();
        }

        if (string.Equals(mode, "SizeAscending", StringComparison.OrdinalIgnoreCase))
        {
            return files
                .OrderBy(path => GetFileSize(path, log))
                .ThenBy(Path.GetFileName, comparer)
                .ToList();
        }

        return files
            .OrderBy(Path.GetFileName, comparer)
            .ToList();
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
