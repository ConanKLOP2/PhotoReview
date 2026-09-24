using System.IO;

namespace PhotoReview.Core.Localization;

/// <summary>English catalog embedded in PhotoReview.Core: always complete, the final fallback for every key.</summary>
public static class BuiltInCatalog
{
    private const string ResourceName = "PhotoReview.Core.Localization.en.json";

    private static readonly Lazy<LanguageCatalog> s_english = new(LoadEnglish);
    private static readonly Lazy<Localizer> s_englishLocalizer = new(() => Localizer.Create(English, []));

    public static LanguageCatalog English => s_english.Value;

    public static Localizer EnglishLocalizer => s_englishLocalizer.Value;

    private static LanguageCatalog LoadEnglish()
    {
        using var stream = typeof(BuiltInCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        var warnings = new List<string>();
        return LanguageCatalog.TryParse(reader.ReadToEnd(), "en (built-in)", out var catalog, warnings)
            ? catalog
            : throw new InvalidOperationException("Built-in English catalog is invalid: " + string.Join("; ", warnings));
    }
}
