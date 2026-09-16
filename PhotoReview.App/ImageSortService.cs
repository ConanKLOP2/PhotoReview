using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PhotoReview.App;

public static class ImageSortService
{
    private static readonly StringComparer ExplorerNameComparer = new ExplorerComparer();

    public static List<string> Sort(IEnumerable<string> files, string? mode)
    {
        if (string.Equals(mode, "Size", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "SizeDescending", StringComparison.OrdinalIgnoreCase))
            return files.OrderByDescending(GetFileSize).ThenBy(path => Path.GetFileName(path), ExplorerNameComparer).ToList();
        if (string.Equals(mode, "SizeAscending", StringComparison.OrdinalIgnoreCase))
            return files.OrderBy(GetFileSize).ThenBy(path => Path.GetFileName(path), ExplorerNameComparer).ToList();
        var byName = files.OrderBy(path => Path.GetFileName(path), ExplorerNameComparer);
        return byName.ToList();
    }

    private static long GetFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) { AppLog.Error($"Failed to read file size for {path}", ex); return -1; }
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

    private sealed class ExplorerComparer : StringComparer
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string x, string y);

        public override int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var result = CompareWithWindowsOrNatural(x, y);
            return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(x, y);
        }

        public override bool Equals(string? x, string? y) => Compare(x, y) == 0;
        public override int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(NaturalKey(obj));

        private static int CompareNatural(string x, string y)
        {
            var left = NaturalKey(x);
            var right = NaturalKey(y);
            return StringComparer.OrdinalIgnoreCase.Compare(left, right);
        }

        private static int CompareWithWindowsOrNatural(string x, string y)
        {
            try { return StrCmpLogicalW(x, y); }
            catch (DllNotFoundException) { return CompareNatural(x, y); }
            catch (EntryPointNotFoundException) { return CompareNatural(x, y); }
        }
    }

}
