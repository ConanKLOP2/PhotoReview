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

        var byFolderAndExtension = ordered.GroupBy(f =>
            (Folder: Path.GetDirectoryName(f.Full), Extension: Path.GetExtension(f.Full)));

        foreach (var group in byFolderAndExtension)
        {
            var byBaseStem = group.GroupBy(
                f => BaseStemOf(f.Path),
                StringComparer.OrdinalIgnoreCase);

            foreach (var stemGroup in byBaseStem)
            {
                var members = stemGroup.ToList();
                var original = members.FirstOrDefault(m =>
                    Path.GetFileNameWithoutExtension(m.Path).Equals(stemGroup.Key, StringComparison.OrdinalIgnoreCase));
                if (original.Path is null) continue;

                var numbered = members
                    .Where(m => IsNumberedVariantOf(m.Path, stemGroup.Key))
                    .OrderBy(m => NumberedSuffix(m.Path))
                    .ToList();
                if (numbered.Count == 0) continue;

                result[original.Path] = (original.Path, numbered[0].Path);
                foreach (var n in numbered) result[n.Path] = (original.Path, n.Path);
            }
        }

        return result;
    }

    private static string BaseStemOf(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var match = NumberedStemPattern.Match(stem);
        return match.Success ? match.Groups[1].Value : stem;
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
}
