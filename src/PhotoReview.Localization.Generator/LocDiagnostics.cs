using Microsoft.CodeAnalysis;

namespace PhotoReview.Localization.Generator;

/// <summary>Build errors reported for a broken English catalog (all are errors: the Tr API would be wrong).</summary>
internal static class LocDiagnostics
{
    private const string Category = "Localization";

    public static readonly DiagnosticDescriptor InvalidJson = new(
        "PRLOC001", "Invalid English catalog", "en.json is not a valid flat catalog: {0}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidKey = new(
        "PRLOC002", "Invalid localization key",
        "Key '{0}' is invalid: use ASCII letters, digits and '.', start with a letter, no empty segment",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor IdentifierCollision = new(
        "PRLOC003", "Generated identifier collision", "Key '{0}' generates identifier '{1}', which is already used by {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidPlaceholder = new(
        "PRLOC004", "Invalid placeholder",
        "Text of '{0}' has unbalanced braces or an invalid placeholder (use {{name}}, and {{{{ }}}} for literal braces)",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PluralWithoutOther = new(
        "PRLOC005", "Plural group without .other", "Plural group '{0}' has '{0}.one' but no '{0}.other'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateKey = new(
        "PRLOC006", "Duplicate key", "Key '{0}' appears more than once",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);
}
