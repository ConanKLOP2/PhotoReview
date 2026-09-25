using System.Buffers;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Catalog;

/// <summary>
/// Bộ so sánh chuỗi số tự nhiên thuần túy (managed C#), không phụ thuộc vào Win32 P/Invoke.
/// Đảm bảo a2 &lt; a10, a (1) &lt; a (2) và không phân biệt hoa thường.
/// </summary>
public sealed class ManagedNaturalComparer : INaturalComparer
{
    public static readonly ManagedNaturalComparer Instance = new();

    /// <summary>Width of the zero-padded digit-count prefix in a key (the former <c>ToString("D10")</c>).</summary>
    private const int LengthPrefixWidth = 10;

    /// <summary>Keys up to this many chars are built on the stack while comparing; longer ones rent from the array pool.</summary>
    private const int StackKeyChars = 256;

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

        // Both keys are built into stack/pooled buffers: sorting or scanning N names no longer allocates two
        // StringBuilders + two strings per comparison. The comparison itself is unchanged.
        var lengthX = KeyLength(x);
        var lengthY = KeyLength(y);
        char[]? rentedX = null;
        char[]? rentedY = null;
        try
        {
            Span<char> stackX = stackalloc char[lengthX <= StackKeyChars ? lengthX : 0];
            Span<char> stackY = stackalloc char[lengthY <= StackKeyChars ? lengthY : 0];
            var keyX = lengthX <= StackKeyChars ? stackX : (rentedX = ArrayPool<char>.Shared.Rent(lengthX)).AsSpan(0, lengthX);
            var keyY = lengthY <= StackKeyChars ? stackY : (rentedY = ArrayPool<char>.Shared.Rent(lengthY)).AsSpan(0, lengthY);
            WriteKey(x, keyX);
            WriteKey(y, keyY);

            var cmp = keyX.CompareTo(keyY, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0)
            {
                return cmp;
            }
        }
        finally
        {
            if (rentedX is not null) ArrayPool<char>.Shared.Return(rentedX);
            if (rentedY is not null) ArrayPool<char>.Shared.Return(rentedY);
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

        return string.Create(KeyLength(name), name, static (span, source) => WriteKey(source, span));
    }

    /// <summary>Exact length of <see cref="BuildNaturalKey"/> for <paramref name="name"/>: one char per non-digit, 10 + digits + 1 per digit run.</summary>
    private static int KeyLength(string name)
    {
        var length = 0;
        for (var i = 0; i < name.Length;)
        {
            if (char.IsDigit(name[i]))
            {
                var start = i;
                while (i < name.Length && char.IsDigit(name[i])) i++;
                length += LengthPrefixWidth + SignificantDigits(name, start, i) + 1;
            }
            else
            {
                length++;
                i++;
            }
        }

        return length;
    }

    /// <summary>Digits of the run [start, end) after trimming leading ASCII '0' (an all-zero run has none).</summary>
    private static int SignificantDigits(string name, int start, int end)
    {
        while (start < end && name[start] == '0') start++;
        return end - start;
    }

    private static void WriteKey(string name, Span<char> destination)
    {
        var o = 0;
        for (var i = 0; i < name.Length;)
        {
            if (char.IsDigit(name[i]))
            {
                var start = i;
                while (i < name.Length && char.IsDigit(name[i])) i++;
                while (start < i && name[start] == '0') start++;
                var count = i - start;

                // Zero-padded decimal digit count, LengthPrefixWidth wide (counts stay far below 10^10).
                for (var p = o + LengthPrefixWidth - 1; p >= o; p--)
                {
                    destination[p] = (char)('0' + (count % 10));
                    count /= 10;
                }

                o += LengthPrefixWidth;
                name.AsSpan(start, i - start).CopyTo(destination[o..]);
                o += i - start;
                destination[o++] = '\0';
            }
            else
            {
                destination[o++] = char.ToLowerInvariant(name[i]);
                i++;
            }
        }
    }
}
