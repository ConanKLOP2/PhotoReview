using System.IO;
using System.Text.RegularExpressions;

namespace PhotoReview.App;

public static class ComparePairService
{
    public static (string Left, string Right)? Find(IEnumerable<string> files, string path)
    {
        var available = files.ToList();
        var stem = Path.GetFileNameWithoutExtension(path);
        var match = Regex.Match(stem, "^(.*) \\(\\d+\\)$");
        var baseStem = match.Success ? match.Groups[1].Value : stem;
        var original = available.FirstOrDefault(candidate =>
            Path.GetFileNameWithoutExtension(candidate).Equals(baseStem, StringComparison.OrdinalIgnoreCase));
        var numbered = available.FirstOrDefault(candidate =>
            Regex.IsMatch(Path.GetFileNameWithoutExtension(candidate), $"^{Regex.Escape(baseStem)} \\(\\d+\\)$", RegexOptions.IgnoreCase));
        return original is not null && numbered is not null ? (original, numbered) : null;
    }
}
