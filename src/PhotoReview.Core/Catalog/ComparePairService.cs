using System.IO;
using System.Text.RegularExpressions;

namespace PhotoReview.Core.Catalog;

public static class ComparePairService
{
    // Compiled once and reused by both Find (query-time) and BuildIndex (catalog-wide) instead
    // of a fresh Regex.Match/IsMatch pattern string per call.
    private static readonly Regex NumberedStemPattern = new(@"^(.*) \(\d+\)$", RegexOptions.Compiled);
    private static readonly Regex NumberedSuffixPattern = new(@"\((\d+)\)$", RegexOptions.Compiled);

    public static (string Left, string Right)? Find(IEnumerable<string> files, string path)
    {
        var selectedFolder = Path.GetDirectoryName(Path.GetFullPath(path));
        var selectedExtension = Path.GetExtension(path);
        var available = files.Where(candidate =>
                string.Equals(Path.GetDirectoryName(Path.GetFullPath(candidate)), selectedFolder, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetExtension(candidate), selectedExtension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => Path.GetFullPath(candidate), StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => Path.GetFullPath(candidate), StringComparer.Ordinal).ToList();
        var stem = Path.GetFileNameWithoutExtension(path);
        var match = NumberedStemPattern.Match(stem);
        var baseStem = match.Success ? match.Groups[1].Value : stem;
        var original = available.FirstOrDefault(candidate =>
            Path.GetFileNameWithoutExtension(candidate).Equals(baseStem, StringComparison.OrdinalIgnoreCase));
        var fullPath = Path.GetFullPath(path);
        var selected = match.Success ? available.FirstOrDefault(candidate => string.Equals(Path.GetFullPath(candidate), fullPath, StringComparison.OrdinalIgnoreCase)) : null;
        var numbered = selected ?? available
            .Where(candidate => IsNumberedVariantOf(candidate, baseStem))
            .OrderBy(NumberedSuffix)
            .FirstOrDefault();
        return original is not null && numbered is not null ? (original, numbered) : null;
    }

    /// <summary>
    /// Builds a lookup equivalent to calling <see cref="Find"/> for every path in
    /// <paramref name="files"/>, in a single O(n log n) pass instead of the O(n log n) that
    /// <see cref="Find"/> repeats on every call. Intended for callers that look up a pair on
    /// every navigation over the same, rarely-changing catalog (see ImagePresenter) --
    /// rebuild only when the catalog's membership/order actually changes.
    /// </summary>
    public static Dictionary<string, (string Left, string Right)> BuildIndex(IEnumerable<string> files)
    {
        var result = new Dictionary<string, (string Left, string Right)>(StringComparer.OrdinalIgnoreCase);
        var ordered = files
            .Select(path => (Path: path, Full: Path.GetFullPath(path)))
            .OrderBy(f => f.Full, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Full, StringComparer.Ordinal);

        // Find compares folder and extension with StringComparison.OrdinalIgnoreCase; the
        // default tuple/string comparer GroupBy would otherwise use here is ordinal
        // case-SENSITIVE, which would silently stop pairing e.g. "DSC0001.JPG" with
        // "DSC0001 (1).jpg", or two entries whose folder differs only by case.
        var byFolderAndExtension = ordered.GroupBy(f =>
            (Folder: Path.GetDirectoryName(f.Full), Extension: Path.GetExtension(f.Full)),
            FolderExtensionComparer.Instance);

        foreach (var group in byFolderAndExtension)
        {
            // Same rules as Find, evaluated per file: the "original" is the first file whose OWN stem equals the base stem
            // and a numbered file may itself be the original of a deeper numbering ("a (1)" is the numbered variant of "a"
            // and the original of "a (1) (2)"), so files cannot be grouped by base stem alone.
            var members = group.ToList();
            var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var variants = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in members)
            {
                var stem = Path.GetFileNameWithoutExtension(m.Path);
                originals.TryAdd(stem, m.Path);
                var match = NumberedStemPattern.Match(stem);
                if (!match.Success) continue;
                var baseStem = match.Groups[1].Value;
                if (!variants.TryGetValue(baseStem, out var list)) variants[baseStem] = list = [];
                list.Add(m.Path);
            }
            foreach (var list in variants.Values) list.Sort(CompareBySuffixStable(list));

            foreach (var m in members)
            {
                var stem = Path.GetFileNameWithoutExtension(m.Path);
                var match = NumberedStemPattern.Match(stem);
                var baseStem = match.Success ? match.Groups[1].Value : stem;
                if (!originals.TryGetValue(baseStem, out var original)) continue;
                if (match.Success) result[m.Path] = (original, m.Path);
                else if (variants.TryGetValue(baseStem, out var numbered)) result[m.Path] = (original, numbered[0]);
            }
        }

        return result;
    }

    // List.Sort is unstable; ties on the suffix keep their (path-sorted) input order like Find's OrderBy.
    private static Comparison<string> CompareBySuffixStable(List<string> original)
    {
        var position = new Dictionary<string, int>(original.Count, StringComparer.Ordinal);
        for (var i = 0; i < original.Count; i++) position.TryAdd(original[i], i);
        return (a, b) =>
        {
            var cmp = NumberedSuffix(a).CompareTo(NumberedSuffix(b));
            return cmp != 0 ? cmp : position[a].CompareTo(position[b]);
        };
    }

    private static bool IsNumberedVariantOf(string candidate, string baseStem)
    {
        var candidateStem = Path.GetFileNameWithoutExtension(candidate);
        if (candidateStem.Length <= baseStem.Length) return false;
        var match = NumberedStemPattern.Match(candidateStem);
        return match.Success && match.Groups[1].Value.Equals(baseStem, StringComparison.OrdinalIgnoreCase);
    }

    private static int NumberedSuffix(string candidate)
    {
        var match = NumberedSuffixPattern.Match(Path.GetFileNameWithoutExtension(candidate));
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : int.MaxValue;
    }

    private sealed class FolderExtensionComparer : IEqualityComparer<(string? Folder, string Extension)>
    {
        public static readonly FolderExtensionComparer Instance = new();

        public bool Equals((string? Folder, string Extension) x, (string? Folder, string Extension) y) =>
            string.Equals(x.Folder, y.Folder, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Extension, y.Extension, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string? Folder, string Extension) obj) => HashCode.Combine(
            obj.Folder is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Folder),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Extension));
    }
}
