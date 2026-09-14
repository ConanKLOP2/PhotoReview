using System.IO;
using System.Runtime.InteropServices;

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
        catch { return -1; }
    }

    public static string NaturalKey(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "\\d+", match => match.Value.PadLeft(12, '0'));

    private sealed class ExplorerComparer : StringComparer
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string x, string y);

        public override int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            try { return StrCmpLogicalW(x, y); }
            catch (DllNotFoundException) { return StringComparer.OrdinalIgnoreCase.Compare(NaturalKey(x), NaturalKey(y)); }
            catch (EntryPointNotFoundException) { return StringComparer.OrdinalIgnoreCase.Compare(NaturalKey(x), NaturalKey(y)); }
        }

        public override bool Equals(string? x, string? y) => Compare(x, y) == 0;
        public override int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj);
    }

}
