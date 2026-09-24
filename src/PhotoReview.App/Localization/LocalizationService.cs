using System.Globalization;
using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;

namespace PhotoReview.App.Localization;

/// <summary>
/// Owns the UI language (ADR 0006): loads catalogs from <c>&lt;app&gt;\Languages</c> and the user folder,
/// publishes the result as <see cref="Localizer.Current"/> and switches <see cref="CultureInfo.CurrentUICulture"/>.
/// <see cref="Load"/> only reads files, so it may run on a worker thread; <see cref="Apply"/> publishes.
/// </summary>
public sealed class LocalizationService
{
    private readonly LanguageLoader _loader;
    private readonly ILog _log;

    public LocalizationService(IAppPaths paths, IFileSystem fileSystem, ILog log)
        : this(new LanguageLoader(fileSystem, ShippedLanguagesDir, paths?.UserLanguagesDir), paths!, log)
    {
    }

    internal LocalizationService(LanguageLoader loader, IAppPaths paths, ILog log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        UserLanguagesDir = paths.UserLanguagesDir;
    }

    /// <summary><c>&lt;app&gt;\Languages</c>: catalogs shipped with the build.</summary>
    public static string ShippedLanguagesDir => Path.Combine(AppContext.BaseDirectory, "Languages");

    public string UserLanguagesDir { get; }

    /// <summary>The setting value last applied (<c>auto</c> or a code).</summary>
    public string RequestedLanguage { get; private set; } = LanguageLoader.AutoCode;

    public IReadOnlyList<LanguageInfo> DiscoverLanguages() => _loader.DiscoverLanguages();

    /// <summary>Reads and validates the catalogs for <paramref name="language"/>; no global state is touched.</summary>
    public Localizer Load(string? language) => _loader.Load(language, CultureInfo.InstalledUICulture);

    /// <summary>Publishes a localizer built by <see cref="Load"/>; call on the UI thread.</summary>
    public void Apply(string? language, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        RequestedLanguage = string.IsNullOrWhiteSpace(language) ? LanguageLoader.AutoCode : language;
        foreach (var warning in localizer.Warnings) _log.Warn("i18n: " + warning);
        try
        {
            var culture = CultureInfo.GetCultureInfo(localizer.Code);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // A community language code .NET does not know still works for text; only the UI culture stays.
        }
        Localizer.SetCurrent(localizer);
    }

    /// <summary>Loads and applies in one step (language switch or "Reload translations").</summary>
    public void Switch(string? language) => Apply(language, Load(language));

    /// <summary>Re-reads the files of the current language, e.g. after a translator edited them.</summary>
    public void Reload() => Switch(RequestedLanguage);
}
