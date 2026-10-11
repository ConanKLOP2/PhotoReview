using PhotoReview.Core.Localization;

namespace PhotoReview.App.Localization;

/// <summary>
/// Text of the message shown after "Reload translations": a translator must see at once that a file was skipped or an
/// entry fell back to English, not only find it in the log. The problem lines stay in English (file, key, reason).
/// </summary>
public static class TranslationProblems
{
    /// <summary>Problems listed in the message; the rest are counted (every one is in the log).</summary>
    public const int MaxListed = 15;

    public static bool HasProblems(IReadOnlyCollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        return warnings.Count > 0;
    }

    public static string Message(IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        if (warnings.Count == 0) return Tr.DialogReloadTranslationsOk;
        var details = string.Join("\n", warnings.Take(MaxListed).Select(w => "• " + w));
        if (warnings.Count > MaxListed) details += "\n" + Tr.DialogReloadTranslationsMore(warnings.Count - MaxListed);
        return Tr.DialogReloadTranslationsProblems(warnings.Count, details);
    }
}
