using System.IO;
using System.Text.RegularExpressions;

namespace PhotoReview.App;

public static class ComparePairService
{
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
        var match = Regex.Match(stem, "^(.*) \\(\\d+\\)$");
        var baseStem = match.Success ? match.Groups[1].Value : stem;
        var original = available.FirstOrDefault(candidate =>
            Path.GetFileNameWithoutExtension(candidate).Equals(baseStem, StringComparison.OrdinalIgnoreCase));
        var fullPath = Path.GetFullPath(path);
        var selected = match.Success ? available.FirstOrDefault(candidate => string.Equals(Path.GetFullPath(candidate), fullPath, StringComparison.OrdinalIgnoreCase)) : null;
        var numbered = selected ?? available
            .Where(candidate => Regex.IsMatch(Path.GetFileNameWithoutExtension(candidate), $"^{Regex.Escape(baseStem)} \\(\\d+\\)$", RegexOptions.IgnoreCase))
            .OrderBy(NumberedSuffix)
            .FirstOrDefault();
        return original is not null && numbered is not null ? (original, numbered) : null;
    }

    private static int NumberedSuffix(string candidate)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(candidate), "\\((\\d+)\\)$");
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : int.MaxValue;
    }
}
