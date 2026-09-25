using System.Globalization;
using System.Text.RegularExpressions;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// Cross-cutting quality guards over tests/** (AGENTS.md > Tests; REVIEW-2026-09-25 TEST-02/TEST-03). Every rule works on the
/// blanked test bodies of <see cref="TestSourceScanner"/> and freezes today's exceptions in
/// <c>test-quality-allowlist.txt</c> (<c>rule|key|count</c>), which may only shrink: a new violation fails, and a
/// fixed one fails until its line is removed.
/// </summary>
public sealed class TestQualityRulesTests
{
    private const string AllowlistFile = "tests/PhotoReview.Architecture.Tests/test-quality-allowlist.txt";

    // Anything that decides a test: xUnit Assert.*, a helper named Assert*/Verify*, Throws*, FluentAssertions-style Should, Fail.
    private static readonly Regex AssertionToken = new(
        @"\bAssert\b|\bAssert\w*\s*\(|\bThrows\w*\s*[<(]|\bVerify\w*\s*\(|\.Should\w*\s*[(.]|\bFail\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DelayToken = new(
        @"\bThread\s*\.\s*Sleep\s*\(|\bTask\s*\.\s*Delay\s*\(|\bTask\s*\.\s*Yield\s*\(|\bawait\s+Task\s*\.\s*Yield\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Real OS resources a default-filter test must not touch: the user's Recycle Bin, spawned processes, shell, clipboard, registry.
    private static readonly Regex RealOsToken = new(
        @"(?<!typeof\([\w.]*)\bWindowsRecycleBin\b|\bProcess\s*\.\s*Start\s*\(|\bSHFileOperation\b|\bClipboard\s*\.|\bRegistry\s*\.|\bnew\s+ProcessStartInfo\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> OptOutCategories = new(StringComparer.Ordinal) { "Native", "Slow", "Manual", "Integration" };

    /// <summary>Assertion-free tests: a test that decides nothing is a silent placeholder (TEST-02).</summary>
    internal static List<ScannedTest> AssertionFreeTests(IEnumerable<ScannedTest> tests) =>
        tests.Where(t => !t.IsSkipped && !t.Categories.Contains("Manual") && !AssertionToken.IsMatch(t.Body)).ToList();

    [Fact(DisplayName = "Rule TEST-02: every [Fact]/[Theory] body asserts something (shrinking allowlist of no-throw tests)")]
    [Trait("Category", "Architecture")]
    public void EveryTestBodyContainsAnAssertion()
    {
        var all = TestSourceScanner.AllTests();
        Assert.True(all.Count > 1000, $"The scan found only {all.Count} tests; the scanner is broken.");

        var actual = AssertionFreeTests(all).GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.Count());
        AssertMatchesAllowlist("assertion-free", actual,
            "Each of these tests has no assertion (a no-throw test is allowed only via the allowlist, with a comment in the test)");
    }

    [Fact(DisplayName = "Rule TEST-02: an assertion-free [Fact] is detected, one with Assert/Throws/Skip/Manual is not")]
    [Trait("Category", "Architecture")]
    public void AssertionFreeDetection_FindsPlaceholders()
    {
        const string source = """
            public class C
            {
                [Fact] public void Empty() { var x = 1; }
                [Fact] public async Task CompletedOnly() { await Task.CompletedTask; }
                [Fact] public void Asserts() { Assert.Equal(2, Add()); }
                [Fact] public void Throws_() => Assert.Throws<Exception>(() => { });
                [Fact(Skip = "reason")] public void Skipped() { }
                [Fact, Trait("Category", "Manual")] public void Measured() { var s = "Assert."; }
                [Theory, InlineData(1)] public void Helper(int x) { AssertSomething(x); }
                [Fact] public void OnlyInStringOrComment() { var s = "Assert.True(x)"; /* Assert.True(y) */ }
            }
            """;

        var free = AssertionFreeTests(TestSourceScanner.Scan("t.cs", source)).Select(t => t.Name).ToArray();

        Assert.Equal(["Empty", "CompletedOnly", "OnlyInStringOrComment"], free);
    }

    [Fact(DisplayName = "Rule TEST-FLAKY: no Thread.Sleep / Task.Delay / Task.Yield in tests outside the shrinking allowlist (use Wait.Until or a seam)")]
    [Trait("Category", "Architecture")]
    public void TestsDoNotSleepOrDelay()
    {
        var actual = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in RepoScan.CsFiles("tests"))
        {
            var count = DelayToken.Count(TestIsolationRulesTests.Blank(RepoScan.Text(file)));
            if (count > 0) actual[RepoScan.Relative(file)] = count;
        }

        AssertMatchesAllowlist("delay", actual,
            "Fixed delays make tests slow and flaky; poll with Wait.Until (TestSupport), use a fake clock/delay seam or a signal");
    }

    [Fact(DisplayName = "Rule TEST-OS: tests that touch the real Recycle Bin, spawn processes or use the shell/clipboard/registry are Native/Slow/Manual/Integration")]
    [Trait("Category", "Architecture")]
    public void RealOsResourceTestsAreCategorised()
    {
        // The token may sit in a helper of the file (e.g. a RunPowerShell method) rather than in the test body, so it is
        // judged per file: a file that uses a real OS resource makes every uncategorised test in it a suspect.
        var osFiles = RepoScan.CsFiles("tests")
            .Where(f => RealOsToken.IsMatch(TestIsolationRulesTests.Blank(RepoScan.Text(f))))
            .Select(RepoScan.Relative).ToHashSet(StringComparer.Ordinal);
        var actual = TestSourceScanner.AllTests()
            .Where(t => !t.IsSkipped && osFiles.Contains(t.File) && !t.Categories.Any(OptOutCategories.Contains))
            .GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.Count());

        AssertMatchesAllowlist("real-os-uncategorised", actual,
            "These default-filter tests use a real OS resource; add [Trait(\"Category\", \"Native\"|\"Slow\")] (self-cleaning) or use a fake");
    }

    [Fact(DisplayName = "Rule TEST-03: Skip reasons are real and not stale placeholders (shrinking allowlist of tracked skips)")]
    [Trait("Category", "Architecture")]
    public void SkipReasonsAreNotStale()
    {
        var stale = new Regex(@"not\s+(yet\s+)?implemented|\bTODO\b|\bTBD\b|\bWIP\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var tests = TestSourceScanner.AllTests().Where(t => t.IsSkipped).ToList();

        var empty = tests.Where(t => t.SkipReason.Trim().Length < 10).Select(t => t.Key).ToList();
        Assert.True(empty.Count == 0, "A Skip needs a real reason (10+ characters):\n" + string.Join("\n", empty));

        var actual = tests.Where(t => stale.IsMatch(t.SkipReason)).GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.Count());
        AssertMatchesAllowlist("stale-skip", actual,
            "A skip that says 'not implemented' must be re-checked against master (TEST-03); keep it only as a tracked, allowlisted decision");
    }

    [Fact(DisplayName = "Rule TEST-TAUTOLOGY: no Assert.True(true) / Assert.Equal(sameLiteral, sameLiteral)")]
    [Trait("Category", "Architecture")]
    public void NoLiteralTautologies()
    {
        var literal = new Regex(
            @"Assert\.True\(\s*true\b|Assert\.False\(\s*false\b|Assert\.Equal\(\s*(?<a>""[^""]*""|\d+)\s*,\s*\k<a>\s*[,)]",
            RegexOptions.CultureInvariant);

        var violations = TestSourceScanner.AllTests().Where(t => literal.IsMatch(t.RawBody)).Select(t => t.Key).ToList();

        Assert.True(violations.Count == 0, "These tests assert a constant against itself and can never fail:\n" + string.Join("\n", violations));
    }

    [Fact(DisplayName = "Rule TEST-09b: tests that assign CultureInfo.DefaultThreadCurrent*Culture are in the GlobalState collection")]
    [Trait("Category", "Architecture")]
    public void ProcessWideCultureMutationIsInGlobalState()
    {
        var assignment = new Regex(@"DefaultThreadCurrent(?:UI)?Culture\s*=(?!=)", RegexOptions.CultureInvariant);
        var violations = new List<string>();
        var mutating = 0;
        foreach (var file in RepoScan.CsFiles("tests"))
        {
            var text = RepoScan.Text(file);
            if (!assignment.IsMatch(TestIsolationRulesTests.Blank(text))) continue;
            mutating++;
            if (!text.Contains("[Collection(\"GlobalState\")]", StringComparison.Ordinal)) violations.Add(RepoScan.Relative(file));
        }

        Assert.True(mutating > 0, "The scan found no culture-mutating tests; the rule is not checking anything.");
        Assert.True(violations.Count == 0, "Process-wide culture mutation outside [Collection(\"GlobalState\")]:\n" + string.Join("\n", violations));
    }

    [Fact(DisplayName = "Rule TEST-09c: test files that switch the ambient localizer declare a non-parallel [Collection]")]
    [Trait("Category", "Architecture")]
    public void AmbientLocalizerSwitchingTestsAreInACollection()
    {
        var switching = new Regex(@"\bTestLocalization\s*\.\s*Use(?:Vietnamese)?\s*\(|\bLocalizer\s*\.\s*SetCurrent\s*\(", RegexOptions.CultureInvariant);
        var files = TestSourceScanner.AllTests().Select(t => t.File).ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();
        var switchingFiles = 0;
        foreach (var file in files)
        {
            var text = RepoScan.Text(Path.Combine(RepoScan.Root, file));
            if (!switching.IsMatch(TestIsolationRulesTests.Blank(text))) continue;
            switchingFiles++;
            if (!text.Contains("[Collection(\"", StringComparison.Ordinal)) violations.Add(file);
        }

        Assert.True(switchingFiles > 3, "The scan found almost no localizer-switching tests; the rule is not checking anything.");
        Assert.True(violations.Count == 0,
            "Localizer.Current is process-wide; these tests switch it without a [Collection(...)] and can race Vietnamese assertions:\n" + string.Join("\n", violations));
    }

    [Fact(DisplayName = "Rule TEST-QUALITY: the allowlist only names known rules")]
    [Trait("Category", "Architecture")]
    public void AllowlistOnlyNamesKnownRules()
    {
        var known = new HashSet<string>(StringComparer.Ordinal) { "assertion-free", "delay", "real-os-uncategorised", "stale-skip" };
        var unknown = ReadAllowlist().Keys.Where(k => !known.Contains(k.Rule)).Select(k => k.Rule + "|" + k.Key).ToList();

        Assert.True(unknown.Count == 0, "Unknown rule in " + AllowlistFile + ":\n" + string.Join("\n", unknown));
    }

    private static Dictionary<(string Rule, string Key), int> ReadAllowlist()
    {
        var result = new Dictionary<(string, string), int>();
        foreach (var raw in File.ReadAllLines(Path.Combine(RepoScan.Root, AllowlistFile)))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split('|');
            Assert.True(parts.Length == 3 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _),
                $"{AllowlistFile}: malformed line '{line}' (expected 'rule|key|count')");
            result[(parts[0].Trim(), parts[1].Trim())] = int.Parse(parts[2], CultureInfo.InvariantCulture);
        }
        return result;
    }

    private static void AssertMatchesAllowlist(string rule, Dictionary<string, int> actual, string why)
    {
        var allowed = ReadAllowlist().Where(e => e.Key.Rule == rule).ToDictionary(e => e.Key.Key, e => e.Value, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (key, count) in actual.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(key, out var limit)) problems.Add($"NEW   {rule}|{key}|{count}");
            else if (count > limit) problems.Add($"MORE  {rule}|{key}: {count}, allowlist says {limit}");
            else if (count < limit) problems.Add($"FEWER {rule}|{key}: {count}, allowlist says {limit} (lower it)");
        }
        foreach (var key in allowed.Keys.Where(k => !actual.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
            problems.Add($"STALE {rule}|{key}: fixed or gone, remove it from {AllowlistFile}");

        Assert.True(problems.Count == 0, why + ":\n" + string.Join("\n", problems));
    }
}
