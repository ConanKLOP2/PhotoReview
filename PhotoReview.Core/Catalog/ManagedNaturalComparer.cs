using System.Globalization;
using System.Text;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Catalog;

/// <summary>
/// Bộ so sánh chuỗi số tự nhiên thuần túy (managed C#), không phụ thuộc vào Win32 P/Invoke.
/// Đảm bảo a2 &lt; a10, a (1) &lt; a (2) và không phân biệt hoa thường.
/// </summary>
public sealed class ManagedNaturalComparer : INaturalComparer
{
    public static readonly ManagedNaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var keyX = BuildNaturalKey(x);
        var keyY = BuildNaturalKey(y);

        var cmp = string.Compare(keyX, keyY, StringComparison.OrdinalIgnoreCase);
        if (cmp != 0)
        {
            return cmp;
        }

        var caseInsensitiveCmp = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        if (caseInsensitiveCmp != 0)
        {
            return caseInsensitiveCmp;
        }

        return string.Compare(x, y, StringComparison.Ordinal);
    }

    public static string BuildNaturalKey(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(name.Length + 16);
        for (var i = 0; i < name.Length;)
        {
            if (char.IsDigit(name[i]))
            {
                var start = i;
                while (i < name.Length && char.IsDigit(name[i]))
                {
                    i++;
                }

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
