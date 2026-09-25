using System.Globalization;
using System.IO;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Localization;

/// <summary>A language the user can pick: found in the shipped folder, the user folder, or built in.</summary>
public sealed record LanguageInfo(string Code, string Name, string NativeName, bool IsUserProvided);

/// <summary>
/// Finds and loads translation files from the shipped folder (<c>&lt;app&gt;\Languages</c>) and the user
/// folder (<c>%LocalAppData%\PhotoReview\Languages</c>). Files are untrusted: a broken or oversized file is
/// skipped with a warning, never an exception.
/// </summary>
public sealed class LanguageLoader
{
    /// <summary>Setting value that follows the Windows UI language.</summary>
    public const string AutoCode = "auto";

    private readonly IFileSystem _fileSystem;
    private readonly string _shippedDir;
    private readonly string? _userDir;

    public LanguageLoader(IFileSystem fileSystem, string shippedDir, string? userDir)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _shippedDir = shippedDir ?? throw new ArgumentNullException(nameof(shippedDir));
        _userDir = userDir;
    }

    /// <summary>English first, then every other valid language by native name (a code found in both folders is listed once).</summary>
    public IReadOnlyList<LanguageInfo> DiscoverLanguages() => Discover([.. ReadAll(new List<string>())]);

    private static List<LanguageInfo> Discover(IReadOnlyList<(LanguageCatalog Catalog, bool IsUser)> catalogs)
    {
        var english = BuiltInCatalog.English;
        var found = new Dictionary<string, LanguageInfo>(StringComparer.OrdinalIgnoreCase)
        {
            [Localizer.EnglishCode] = new(Localizer.EnglishCode, english.Name, english.NativeName, false),
        };
        foreach (var (catalog, isUser) in catalogs)
        {
            if (!found.ContainsKey(catalog.Code)) found[catalog.Code] = new(catalog.Code, catalog.Name, catalog.NativeName, isUser);
        }
        return [.. found.Values.OrderBy(l => l.Code == Localizer.EnglishCode ? 0 : 1)
            .ThenBy(l => l.NativeName, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// Builds the localizer for <paramref name="code"/> (<see cref="AutoCode"/> resolves via
    /// <paramref name="uiCulture"/>). Unknown languages fall back to English.
    /// </summary>
    public Localizer Load(string? code, CultureInfo? uiCulture = null)
    {
        var warnings = new List<string>();
        var all = ReadAll(warnings).ToList(); // one pass over the folders
        var resolved = Resolve(Discover(all), code, uiCulture);
        var overlays = new List<LanguageCatalog>();
        if (!string.Equals(resolved, Localizer.EnglishCode, StringComparison.OrdinalIgnoreCase))
        {
            // Shipped first, user last: user entries override shipped ones key by key.
            overlays.AddRange(all
                .Where(x => string.Equals(x.Catalog.Code, resolved, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.IsUser)
                .Select(x => x.Catalog));
        }
        else
        {
            // A user file may also override English wording.
            overlays.AddRange(all
                .Where(x => x.IsUser && string.Equals(x.Catalog.Code, Localizer.EnglishCode, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Catalog));
        }
        return Localizer.Create(BuiltInCatalog.English, overlays, warnings);
    }

    /// <summary>Maps a setting value to a language code that has a catalog; English when nothing matches.</summary>
    public string Resolve(string? code, CultureInfo? uiCulture = null) => Resolve(DiscoverLanguages(), code, uiCulture);

    private static string Resolve(IReadOnlyList<LanguageInfo> languages, string? code, CultureInfo? uiCulture)
    {
        var available = languages.Select(l => l.Code).ToList();
        if (!string.IsNullOrWhiteSpace(code) && !string.Equals(code, AutoCode, StringComparison.OrdinalIgnoreCase))
        {
            return available.FirstOrDefault(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) ?? Localizer.EnglishCode;
        }
        var culture = uiCulture ?? CultureInfo.CurrentUICulture;
        return available.FirstOrDefault(c => string.Equals(c, culture.Name, StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault(c => string.Equals(c, culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
            ?? Localizer.EnglishCode;
    }

    private IEnumerable<(LanguageCatalog Catalog, bool IsUser)> ReadAll(List<string> warnings)
    {
        foreach (var catalog in ReadFolder(_shippedDir, warnings)) yield return (catalog, false);
        if (_userDir is not null)
        {
            foreach (var catalog in ReadFolder(_userDir, warnings)) yield return (catalog, true);
        }
    }

    private IEnumerable<LanguageCatalog> ReadFolder(string dir, List<string> warnings)
    {
        IEnumerable<string> files;
        try
        {
            if (!_fileSystem.DirectoryExists(dir)) yield break;
            files = [.. _fileSystem.EnumerateFiles(dir, "*.json")];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{dir}: cannot list files ({ex.Message})");
            yield break;
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            // Translator notes (en.notes.json) and other dotted names are not catalogs.
            if (Path.GetFileNameWithoutExtension(file).Contains('.', StringComparison.Ordinal)) continue;
            string json;
            try
            {
                var stat = _fileSystem.GetFileStat(file);
                if (stat is not null && stat.Length > LanguageCatalog.MaxFileBytes)
                {
                    warnings.Add($"{file}: larger than {LanguageCatalog.MaxFileBytes} bytes, skipped");
                    continue;
                }
                json = _fileSystem.ReadAllText(file);
                // The stat above can be missing or stale (the file grew after it): the text length is the last line of defence.
                if (json.Length > LanguageCatalog.MaxFileBytes)
                {
                    warnings.Add($"{file}: larger than {LanguageCatalog.MaxFileBytes} bytes, skipped");
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{file}: cannot read ({ex.Message})");
                continue;
            }
            if (LanguageCatalog.TryParse(json, file, out var catalog, warnings)) yield return catalog;
        }
    }
}
