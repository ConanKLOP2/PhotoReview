; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PRLOC001 | Localization | Error | Invalid JSON in the English catalog
PRLOC002 | Localization | Error | Invalid localization key
PRLOC003 | Localization | Error | Two keys map to the same generated identifier
PRLOC004 | Localization | Error | Unbalanced braces or invalid placeholder in English text
PRLOC005 | Localization | Error | Plural group without a .other entry
PRLOC006 | Localization | Error | Duplicate key in the English catalog
