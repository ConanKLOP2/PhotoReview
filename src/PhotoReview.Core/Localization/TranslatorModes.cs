using System.Text;

namespace PhotoReview.Core.Localization;

/// <summary>
/// Translator aids (I18N L09): a pseudo-locale that makes untranslated (hard-coded) text and clipped layouts
/// obvious, and a mode that shows each text's catalog key.
/// </summary>
public static class TranslatorModes
{
    public const string PseudoCode = "qps-ploc";

    private const string Plain = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    // Same index as Plain. Built from code points so the source has no Vietnamese letters (localization guard test).
    private static readonly string Accented = new([.. new[]
    {
        0x00E1, 0x0180, 0x0107, 0x0111, 0x00E9, 0x0192, 0x011D, 0x0125, 0x00ED, 0x0135, 0x0137, 0x013A, 0x1E3F,
        0x0144, 0x00F3, 0x1E55, 0x02A0, 0x0155, 0x015B, 0x0165, 0x00FA, 0x1E7D, 0x0175, 0x1E8B, 0x00FD, 0x017A,
        0x00C1, 0x0181, 0x0106, 0x0110, 0x00C9, 0x0191, 0x011C, 0x0124, 0x00CD, 0x0134, 0x0136, 0x0139, 0x1E3E,
        0x0143, 0x00D3, 0x1E54, 0x024A, 0x0154, 0x015A, 0x0164, 0x00DA, 0x1E7C, 0x0174, 0x1E8A, 0x00DD, 0x0179,
    }.Select(c => (char)c)]);

    /// <summary>
    /// Every text becomes <c>[Ŝéţţíñĝś ~~~]</c>: accented letters, about 35 % longer, bracketed so truncation shows.
    /// Placeholders are kept, so formatting still works.
    /// </summary>
    public static Localizer CreatePseudo(Localizer source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Transform(PseudoCode, "Pseudo", PluralRule.OneOther, (_, t) =>
        {
            var first = true;
            return t.TransformLiterals(literal =>
            {
                var accented = Accent(literal);
                if (!first) return accented;
                first = false;
                return "[" + accented;
            }).AppendLiteral(Padding(t.Text.Length) + "]");
        });
    }

    /// <summary>Every text is replaced by its key in brackets, e.g. <c>[status.scanningFolder]</c>.</summary>
    public static Localizer CreateShowKeys(Localizer source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Transform(source.Code, source.NativeName, source.Plural, (key, _) => LocTemplate.Literal("[" + key + "]"));
    }

    /// <summary>Maps ASCII letters to accented look-alikes; everything else is unchanged.</summary>
    public static string Accent(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var i = Plain.IndexOf(c, StringComparison.Ordinal);
            sb.Append(i >= 0 ? Accented[i] : c);
        }
        return sb.ToString();
    }

    private static string Padding(int length) => length == 0 ? string.Empty : " " + new string('~', Math.Max(1, (int)Math.Ceiling(length * 0.35)));
}
