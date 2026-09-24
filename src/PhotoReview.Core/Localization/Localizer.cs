using System.Collections.Frozen;

namespace PhotoReview.Core.Localization;

/// <summary>
/// Immutable, merged view of one UI language. Lookup order per key: user file → shipped file → built-in
/// English → the key itself. <see cref="Current"/> is the ambient instance used by the generated <c>Tr</c> API
/// (ADR 0006); swapping it is atomic and raises <see cref="CurrentChanged"/>.
/// </summary>
public sealed class Localizer
{
    public const string EnglishCode = "en";

    private static Localizer? s_current;
    private readonly FrozenDictionary<string, LocTemplate> _templates;

    private Localizer(string code, string nativeName, PluralRule plural, FrozenDictionary<string, LocTemplate> templates,
        IReadOnlyList<string> warnings)
    {
        Code = code;
        NativeName = nativeName;
        Plural = plural;
        _templates = templates;
        Warnings = warnings;
    }

    /// <summary>Ambient localizer. Defaults to built-in English until the app (or a test) sets another one.</summary>
    public static Localizer Current => Volatile.Read(ref s_current) ?? SetDefault();

    /// <summary>Raised after <see cref="SetCurrent"/> replaced the ambient localizer (any thread).</summary>
    public static event EventHandler? CurrentChanged;

    public static void SetCurrent(Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        Volatile.Write(ref s_current, localizer);
        CurrentChanged?.Invoke(null, EventArgs.Empty);
    }

    private static Localizer SetDefault()
    {
        Interlocked.CompareExchange(ref s_current, BuiltInCatalog.EnglishLocalizer, null);
        return s_current!;
    }

    public string Code { get; }

    public string NativeName { get; }

    public PluralRule Plural { get; }

    /// <summary>Problems found while building this localizer (rejected keys, unknown keys, broken files).</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>All keys this localizer can resolve (the English key set).</summary>
    public IEnumerable<string> Keys => _templates.Keys;

    public bool Contains(string key) => _templates.ContainsKey(key);

    public string Get(string key) => _templates.TryGetValue(key, out var t) ? t.Render([]) : key;

    public string Format(string key, params ReadOnlySpan<LocArg> args) =>
        _templates.TryGetValue(key, out var t) ? t.Render(args) : key;

    /// <summary>Picks <c>baseKey.one</c> or <c>baseKey.other</c> by <see cref="Plural"/>, then formats it.</summary>
    public string FormatPlural(string baseKey, long count, params ReadOnlySpan<LocArg> args)
    {
        ArgumentNullException.ThrowIfNull(baseKey);
        var key = Plural == PluralRule.OneOther && count == 1 ? baseKey + ".one" : baseKey + ".other";
        if (!_templates.ContainsKey(key)) key = baseKey + ".other";
        return Format(key, args);
    }

    /// <summary>
    /// Builds a localizer from English plus zero or more overlay catalogs of one language, applied in order
    /// (later overlays win, so pass shipped first, user last). Overlay entries are validated against English:
    /// unknown keys are ignored, a template with bad braces or a placeholder English does not have is rejected
    /// (English is used), a missing placeholder only warns.
    /// </summary>
    public static Localizer Create(LanguageCatalog english, IReadOnlyList<LanguageCatalog> overlays, IEnumerable<string>? priorWarnings = null)
    {
        ArgumentNullException.ThrowIfNull(english);
        ArgumentNullException.ThrowIfNull(overlays);
        var warnings = new List<string>(priorWarnings ?? []);
        var templates = new Dictionary<string, LocTemplate>(StringComparer.Ordinal);
        foreach (var (key, text) in english.Entries)
        {
            if (LocTemplate.TryParse(text, out var t)) templates[key] = t;
            else
            {
                warnings.Add($"en: '{key}' has invalid placeholders");
                templates[key] = LocTemplate.Literal(text);
            }
        }

        var englishTemplates = templates.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var code = english.Code;
        var nativeName = english.NativeName;
        var plural = english.Plural;
        foreach (var overlay in overlays)
        {
            code = overlay.Code;
            nativeName = overlay.NativeName;
            plural = overlay.Plural;
            foreach (var (key, text) in overlay.Entries)
            {
                if (!englishTemplates.TryGetValue(key, out var source))
                {
                    warnings.Add($"{overlay.Code}: unknown key '{key}' ignored");
                    continue;
                }
                if (!LocTemplate.TryParse(text, out var translated))
                {
                    warnings.Add($"{overlay.Code}: '{key}' has unbalanced braces or an invalid placeholder; English used");
                    continue;
                }
                var allowed = source.PlaceholderNames;
                var unknown = translated.PlaceholderNames.FirstOrDefault(n => !allowed.Contains(n));
                if (unknown is not null)
                {
                    warnings.Add($"{overlay.Code}: '{key}' uses unknown placeholder {{{unknown}}}; English used");
                    continue;
                }
                var missing = allowed.FirstOrDefault(n => !translated.PlaceholderNames.Contains(n));
                if (missing is not null) warnings.Add($"{overlay.Code}: '{key}' does not use placeholder {{{missing}}}");
                templates[key] = translated;
            }
        }

        return new Localizer(code, nativeName, plural, templates.ToFrozenDictionary(StringComparer.Ordinal), warnings);
    }
}
