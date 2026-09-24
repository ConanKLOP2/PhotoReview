using System.Text.Json;

namespace PhotoReview.Core.Localization;

/// <summary>How a language picks between <c>key.one</c> and <c>key.other</c>.</summary>
public enum PluralRule
{
    /// <summary><c>.one</c> when the count is exactly 1, otherwise <c>.other</c> (English and most European languages).</summary>
    OneOther,

    /// <summary>Always <c>.other</c> (Vietnamese, Japanese, Chinese, …).</summary>
    None,
}

/// <summary>
/// One translation file, parsed but not yet validated against English. Translation files are untrusted:
/// <see cref="TryParse"/> never throws and reports every problem as a warning.
/// </summary>
public sealed class LanguageCatalog
{
    /// <summary>Largest translation file that is read (a full catalog is ~30 KB).</summary>
    public const long MaxFileBytes = 1024 * 1024;

    public const string MetaKey = "_meta";

    private LanguageCatalog(string code, string name, string nativeName, PluralRule plural, IReadOnlyList<string> authors,
        IReadOnlyDictionary<string, string> entries)
    {
        Code = code;
        Name = name;
        NativeName = nativeName;
        Plural = plural;
        Authors = authors;
        Entries = entries;
    }

    /// <summary>Language code from <c>_meta.code</c>, lower-case (e.g. <c>vi</c>, <c>pt-br</c>).</summary>
    public string Code { get; }

    /// <summary>English name of the language (e.g. "Vietnamese").</summary>
    public string Name { get; }

    /// <summary>Name in the language itself (e.g. "Tiếng Việt"); shown in the language picker.</summary>
    public string NativeName { get; }

    public PluralRule Plural { get; }

    public IReadOnlyList<string> Authors { get; }

    /// <summary>Raw key → text pairs; keys starting with <c>_</c> (comments, meta) are already removed.</summary>
    public IReadOnlyDictionary<string, string> Entries { get; }

    public static bool TryParse(string json, string source, out LanguageCatalog catalog, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(warnings);
        catalog = null!;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            warnings.Add($"{source}: invalid JSON ({ex.Message})");
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"{source}: root must be a JSON object");
                return false;
            }

            string? code = null, name = null, nativeName = null;
            var plural = PluralRule.OneOther;
            var authors = new List<string>();
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.NameEquals(MetaKey))
                {
                    ReadMeta(property.Value, source, warnings, ref code, ref name, ref nativeName, ref plural, authors);
                    continue;
                }
                if (property.Name.StartsWith('_')) continue;
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    warnings.Add($"{source}: '{property.Name}' is not a string, ignored");
                    continue;
                }
                entries[property.Name] = property.Value.GetString()!;
            }

            if (string.IsNullOrWhiteSpace(code) || !IsValidCode(code))
            {
                warnings.Add($"{source}: _meta.code is missing or invalid");
                return false;
            }

            code = code.ToLowerInvariant();
            catalog = new LanguageCatalog(code, name ?? code, nativeName ?? name ?? code, plural, authors, entries);
            return true;
        }
    }

    private static void ReadMeta(JsonElement meta, string source, ICollection<string> warnings,
        ref string? code, ref string? name, ref string? nativeName, ref PluralRule plural, List<string> authors)
    {
        if (meta.ValueKind != JsonValueKind.Object)
        {
            warnings.Add($"{source}: _meta must be an object");
            return;
        }
        foreach (var p in meta.EnumerateObject())
        {
            switch (p.Name)
            {
                case "code" when p.Value.ValueKind == JsonValueKind.String: code = p.Value.GetString(); break;
                case "name" when p.Value.ValueKind == JsonValueKind.String: name = p.Value.GetString(); break;
                case "nativeName" when p.Value.ValueKind == JsonValueKind.String: nativeName = p.Value.GetString(); break;
                case "plural" when p.Value.ValueKind == JsonValueKind.String:
                    plural = string.Equals(p.Value.GetString(), "none", StringComparison.OrdinalIgnoreCase) ? PluralRule.None : PluralRule.OneOther;
                    break;
                case "authors" when p.Value.ValueKind == JsonValueKind.Array:
                    foreach (var a in p.Value.EnumerateArray())
                    {
                        if (a.ValueKind == JsonValueKind.String) authors.Add(a.GetString()!);
                    }
                    break;
            }
        }
    }

    /// <summary>BCP-47-ish: letters, digits and '-', 2..16 chars, starting with a letter.</summary>
    public static bool IsValidCode(string code) =>
        code.Length is >= 2 and <= 16 && char.IsAsciiLetter(code[0]) && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
