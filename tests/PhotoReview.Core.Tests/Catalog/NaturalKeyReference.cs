using System.Globalization;
using System.Text;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>
/// The original (pre-optimisation) natural-key builder and comparer, kept verbatim as the oracle the optimised
/// <see cref="PhotoReview.Core.Catalog.ManagedNaturalComparer"/> must match for every input, and as the baseline of the benchmark.
/// </summary>
internal static class NaturalKeyReference
{
    public static int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var cmp = string.Compare(BuildNaturalKey(x), BuildNaturalKey(y), StringComparison.OrdinalIgnoreCase);
        if (cmp != 0) return cmp;
        var caseInsensitiveCmp = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        if (caseInsensitiveCmp != 0) return caseInsensitiveCmp;
        return string.Compare(x, y, StringComparison.Ordinal);
    }

    public static string BuildNaturalKey(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        var sb = new StringBuilder(name.Length + 16);
        for (var i = 0; i < name.Length;)
        {
            if (char.IsDigit(name[i]))
            {
                var start = i;
                while (i < name.Length && char.IsDigit(name[i])) i++;

                var digits = name.Substring(start, i - start).TrimStart('0');
                sb.Append(digits.Length.ToString("D10", CultureInfo.InvariantCulture));
                sb.Append(digits);
                sb.Append('\0');
            }
            else
            {
                sb.Append(char.ToLowerInvariant(name[i]));
                i++;
            }
        }

        return sb.ToString();
    }
}
