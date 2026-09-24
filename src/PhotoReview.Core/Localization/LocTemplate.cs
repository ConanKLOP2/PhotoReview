using System.Globalization;
using System.Text;

namespace PhotoReview.Core.Localization;

/// <summary>
/// A translation string parsed once into literal and <c>{name}</c> placeholder segments.
/// <c>{{</c> and <c>}}</c> are literal braces. Rendering never throws: a placeholder without a
/// matching argument is written back as <c>{name}</c>.
/// </summary>
public sealed class LocTemplate
{
    private readonly string[] _literals;      // _literals.Length == _names.Length + 1
    private readonly string[] _names;

    private LocTemplate(string text, string[] literals, string[] names)
    {
        Text = text;
        _literals = literals;
        _names = names;
    }

    /// <summary>The original, unparsed translation text.</summary>
    public string Text { get; }

    /// <summary>Placeholder names in order of appearance (may repeat).</summary>
    public IReadOnlyList<string> PlaceholderNames => _names;

    public bool HasPlaceholders => _names.Length > 0;

    /// <summary>Parses <paramref name="text"/>; returns false for unbalanced braces or invalid placeholder names.</summary>
    public static bool TryParse(string text, out LocTemplate template)
    {
        ArgumentNullException.ThrowIfNull(text);
        var literals = new List<string>();
        var names = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{')
                {
                    current.Append('{');
                    i++;
                    continue;
                }
                var end = text.IndexOf('}', i + 1);
                if (end < 0 || !IsValidName(text.AsSpan(i + 1, end - i - 1)))
                {
                    template = null!;
                    return false;
                }
                literals.Add(current.ToString());
                current.Clear();
                names.Add(text.Substring(i + 1, end - i - 1));
                i = end;
            }
            else if (c == '}')
            {
                if (i + 1 < text.Length && text[i + 1] == '}')
                {
                    current.Append('}');
                    i++;
                    continue;
                }
                template = null!;
                return false;
            }
            else
            {
                current.Append(c);
            }
        }
        literals.Add(current.ToString());
        template = new LocTemplate(text, [.. literals], [.. names]);
        return true;
    }

    /// <summary>Template for text that is shown verbatim (e.g. a key used as its own fallback).</summary>
    public static LocTemplate Literal(string text) => new(text, [text], []);

    public string Render(ReadOnlySpan<LocArg> args)
    {
        if (_names.Length == 0) return _literals[0];
        var sb = new StringBuilder(Text.Length + 16);
        for (var i = 0; i < _names.Length; i++)
        {
            sb.Append(_literals[i]);
            var found = false;
            foreach (var arg in args)
            {
                if (!string.Equals(arg.Name, _names[i], StringComparison.Ordinal)) continue;
                sb.Append(FormatValue(arg.Value));
                found = true;
                break;
            }
            if (!found) sb.Append('{').Append(_names[i]).Append('}');
        }
        sb.Append(_literals[^1]);
        return sb.ToString();
    }

    // UI text: numbers follow the user's regional format (AGENTS.md rule 4 allows CurrentCulture for display).
    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
        _ => value.ToString() ?? string.Empty,
    };

    internal static bool IsValidName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || !char.IsAsciiLetter(name[0])) return false;
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c)) return false;
        }
        return true;
    }
}
