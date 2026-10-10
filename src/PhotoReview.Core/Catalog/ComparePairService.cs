using System.Text.RegularExpressions;

namespace PhotoReview.Core.Catalog;

public static partial class ComparePairService
{
    // Shared by Find (query-time) and BuildIndex (catalog-wide). P-1 startup: source-generated (built and
    // ReadyToRun-compiled with the assembly) instead of RegexOptions.Compiled, whose IL emit cost ~15-20 ms on the UI
    // thread the first time an image was presented (dotnet-trace, between Assign and the first frame).
    [GeneratedRegex(@"^(.*) \(\d+\)$")]
    private static partial Regex NumberedStemPattern();

    [GeneratedRegex(@"\((\d+)\)$")]
    private static partial Regex NumberedSuffixPattern();

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
        var match = NumberedStemPattern().Match(stem);
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
        ArgumentNullException.ThrowIfNull(files);
        var result = new Dictionary<string, (string Left, string Right)>(StringComparer.OrdinalIgnoreCase);
        // P-1 startup: plain arrays and dictionaries instead of LINQ OrderBy/ThenBy/GroupBy. The first image of a folder
        // builds this index on the UI thread before its first frame, and the LINQ iterators over value tuples are not
        // ReadyToRun-precompiled (cross-assembly generic instantiations: JIT, ~25 ms for 2867 files, dotnet-trace).
        // Same order as before: by full path ignoring case, then ordinally, then (stable, like OrderBy) input order.
        var ordered = new List<(string Path, string Full, int Input)>();
        foreach (var path in files) ordered.Add((path, Path.GetFullPath(path), ordered.Count));
        ordered.Sort(static (a, b) =>
        {
            var cmp = StringComparer.OrdinalIgnoreCase.Compare(a.Full, b.Full);
            if (cmp == 0) cmp = string.CompareOrdinal(a.Full, b.Full);
            return cmp != 0 ? cmp : a.Input.CompareTo(b.Input);
        });

        // Find compares folder and extension with StringComparison.OrdinalIgnoreCase; an ordinal
        // case-SENSITIVE grouping would silently stop pairing e.g. "DSC0001.JPG" with
        // "DSC0001 (1).jpg", or two entries whose folder differs only by case.
        // Groups keep the sorted order of their members (the order GroupBy kept).
        var groups = new Dictionary<(string? Folder, string Extension), List<(string Path, string Full, int Input)>>(FolderExtensionComparer.Instance);
        foreach (var f in ordered)
        {
            var key = (Folder: Path.GetDirectoryName(f.Full), Extension: Path.GetExtension(f.Full));
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
            group.Add(f);
        }

        foreach (var members in groups.Values)
        {
            // Same rules as Find, evaluated per file: the "original" is the first file whose OWN stem equals the base stem
            // and a numbered file may itself be the original of a deeper numbering ("a (1)" is the numbered variant of "a"
            // and the original of "a (1) (2)"), so files cannot be grouped by base stem alone.
            var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var variants = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in members)
            {
                var stem = Path.GetFileNameWithoutExtension(m.Path);
                originals.TryAdd(stem, m.Path);
                var match = NumberedStemPattern().Match(stem);
                if (!match.Success) continue;
                var baseStem = match.Groups[1].Value;
                if (!variants.TryGetValue(baseStem, out var list)) variants[baseStem] = list = [];
                list.Add(m.Path);
            }
            foreach (var list in variants.Values) list.Sort(CompareBySuffixStable(list));

            foreach (var m in members)
            {
                var stem = Path.GetFileNameWithoutExtension(m.Path);
                var match = NumberedStemPattern().Match(stem);
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
        var match = NumberedStemPattern().Match(candidateStem);
        return match.Success && match.Groups[1].Value.Equals(baseStem, StringComparison.OrdinalIgnoreCase);
    }

    private static int NumberedSuffix(string candidate)
    {
        var match = NumberedSuffixPattern().Match(Path.GetFileNameWithoutExtension(candidate));
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
