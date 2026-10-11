using PhotoReview.Core.Localization;

namespace PhotoReview.App.Localization;

/// <summary>One entry of the language picker in Settings: the value saved to <c>AppSettings.UiLanguage</c> and its label.</summary>
public sealed record LanguageOption(string Code, string DisplayName)
{
    public bool IsAuto => string.Equals(Code, LanguageLoader.AutoCode, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Language picker entries (I18N L08): "Auto (Windows)" first, then every discovered language by its native
/// name, user-provided ones marked. UI-free so the mapping between setting values and entries is unit tested.
/// </summary>
public static class LanguageOptions
{
    /// <summary>Picker entries; the "Auto" and "user file" labels come from <paramref name="localizer"/> (default: the current language).</summary>
    public static IReadOnlyList<LanguageOption> Build(IEnumerable<LanguageInfo> languages, Localizer? localizer = null)
    {
        ArgumentNullException.ThrowIfNull(languages);
        var text = localizer ?? Localizer.Current;
        var options = new List<LanguageOption> { new(LanguageLoader.AutoCode, text.Get(TrKeys.SettingsLanguageAuto)) };
        foreach (var language in languages)
        {
            var name = string.IsNullOrWhiteSpace(language.NativeName) ? language.Code : language.NativeName;
            options.Add(new LanguageOption(language.Code,
                language.IsUserProvided ? text.Format(TrKeys.SettingsLanguageUserProvided, new LocArg("name", name)) : name));
        }
        return options;
    }

    /// <summary>Index of the entry for a saved setting value; an empty value means auto, an unknown code gives -1.</summary>
    public static int IndexOf(IReadOnlyList<LanguageOption> options, string? uiLanguage)
    {
        ArgumentNullException.ThrowIfNull(options);
        var code = string.IsNullOrWhiteSpace(uiLanguage) ? LanguageLoader.AutoCode : uiLanguage.Trim();
        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].Code, code, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>
    /// The setting value to save: the selected entry's code, or <paramref name="current"/> unchanged when nothing is
    /// selected (e.g. the saved code names a language whose file was removed; keep it rather than silently switch).
    /// </summary>
    public static string ToSetting(LanguageOption? selected, string? current)
    {
        if (selected is not null) return selected.IsAuto ? LanguageLoader.AutoCode : selected.Code;
        return string.IsNullOrWhiteSpace(current) ? LanguageLoader.AutoCode : current;
    }
}
