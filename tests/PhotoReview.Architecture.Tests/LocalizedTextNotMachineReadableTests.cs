using System.Text.RegularExpressions;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// I18N + AGENTS.md rule 4: <c>Tr.*</c> text (plural forms format the count with CurrentCulture) is user-facing. Text
/// that goes to a log, journal or CSV is machine-read and must use invariant formatting, so it must not embed
/// <c>Tr.*</c>. Single-line scan: a sink call and a <c>Tr.</c> member on the same line.
/// </summary>
public sealed class LocalizedTextNotMachineReadableTests
{
    private static readonly Regex Sink = new(
        @"(\.(Info|Warn|Error|Debug)\(|\bJournal\w*\.\w+\(|\bCsv\w*\.\w+\(|\bAppendLine\(.*Csv)",
        RegexOptions.CultureInvariant);

    private static readonly Regex TrMember = new(@"(?<![\w.])Tr\.[A-Z]\w*", RegexOptions.CultureInvariant);

    internal static bool IsViolation(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal)) return false;
        return Sink.IsMatch(line) && TrMember.IsMatch(line);
    }

    [Fact(DisplayName = "Localized Tr.* text is never written to a log, journal or CSV line")]
    public void TrText_NotUsedInMachineReadableSinks()
    {
        var violations = RepoScan.FindLineViolations(
            IsViolation,
            skipFile: f => f.Contains("Localization.Generator", StringComparison.Ordinal),
            "src");

        Assert.True(violations.Count == 0,
            "Tr.* is culture-dependent user text; log/journal/CSV lines must use invariant formatting:\n" + string.Join("\n", violations));
    }

    [Theory(DisplayName = "The scan flags Tr.* inside a sink call and ignores unrelated lines")]
    [InlineData("_log.Info(Tr.Files.Count(n));", true)]
    [InlineData("log.Warn($\"skipped {Tr.Skipped(3)}\");", true)]
    [InlineData("StatusText = Tr.Files.Count(n);", false)]
    [InlineData("_log.Info(\"scan done\");", false)]
    [InlineData("// _log.Info(Tr.X())", false)]
    public void Predicate_Behaves(string line, bool expected) => Assert.Equal(expected, IsViolation(line));
}
