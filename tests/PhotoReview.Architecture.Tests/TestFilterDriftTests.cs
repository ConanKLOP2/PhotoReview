using System.Text.RegularExpressions;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// R2-A-12: the default test filter is written in three places (CI env, verify-all.ps1, AGENTS.md > Tests) and the
/// Integration+Slow safety-net filter in two. They only agreed by comment; this pins them together.
/// </summary>
public sealed class TestFilterDriftTests
{
    private static string ReadRepoFile(string relative) => File.ReadAllText(Path.Combine(RepoScan.Root, relative));

    private static string CiFilter(string text) =>
        Regex.Match(text, @"^\s*TEST_FILTER:\s*""(?<f>[^""]+)""", RegexOptions.Multiline).Groups["f"].Value;

    /// <summary>Rebuilds the default gate filter from verify-all.ps1: the base plus each default-on exclusion.</summary>
    private static string VerifyAllDefaultFilter(string text)
    {
        var baseFilter = Regex.Match(text, @"\$filter\s*=\s*""(?<f>[^""]+)""").Groups["f"].Value;
        var additions = Regex.Matches(text, @"if \(-not \$(?:Native|Slow)\) \{ \$filter \+= ""(?<f>[^""]+)"" \}")
            .Select(m => m.Groups["f"].Value);
        return baseFilter + string.Concat(additions);
    }

    [Fact(DisplayName = "Rule R2-A-12: the default test filter is identical in CI, verify-all.ps1 and AGENTS.md")]
    public void DefaultTestFilter_IsIdenticalEverywhere()
    {
        var ci = CiFilter(ReadRepoFile(".github/workflows/ci.yml"));
        var gate = VerifyAllDefaultFilter(ReadRepoFile("tools/verify-all.ps1"));
        var agents = Regex.Match(ReadRepoFile("AGENTS.md"), @"Local filter: `(?<f>[^`]+)`").Groups["f"].Value;

        Assert.False(string.IsNullOrEmpty(ci), "TEST_FILTER not found in ci.yml");
        Assert.Equal(ci, gate);
        Assert.Equal(ci, agents);
    }

    [Fact(DisplayName = "Rule R2-A-12: the Integration+Slow safety-net filter is identical in CI and verify-all.ps1")]
    public void IntegrationSlowFilter_IsIdenticalInCiAndGate()
    {
        var pattern = new Regex(@"Category=Integration&Category=Slow[^""'\s]*");
        var ci = pattern.Match(ReadRepoFile(".github/workflows/ci.yml")).Value;
        var gate = pattern.Match(ReadRepoFile("tools/verify-all.ps1")).Value;

        Assert.NotEmpty(ci);
        Assert.Equal(ci, gate);
    }

    private static IEnumerable<string> TestProjects() =>
        Directory.GetDirectories(Path.Combine(RepoScan.Root, "tests"), "PhotoReview.*.Tests")
            .Select(dir => Path.GetFileName(dir)!)
            .Where(name => Directory.GetFiles(Path.Combine(RepoScan.Root, "tests", name), "*.csproj").Length > 0);

    private const string SharedFilterArgument = "--filter \"$env:TEST_FILTER\"";

    [Fact(DisplayName = "Rule R2-A-12: every CI dotnet test command passes the shared TEST_FILTER (or the Integration+Slow safety net)")]
    public void EveryCiTestCommand_PassesTheSharedFilter()
    {
        var commands = ReadRepoFile(".github/workflows/ci.yml").Split('\n')
            .Where(line => line.Contains("dotnet test ", StringComparison.Ordinal) && !line.TrimStart().StartsWith('#'))
            .ToArray();
        Assert.NotEmpty(commands);

        // A step that defines TEST_FILTER but forgets to pass it would silently run Native/Slow/Manual tests (or none).
        foreach (var command in commands)
        {
            var usesShared = command.Contains(SharedFilterArgument, StringComparison.Ordinal);
            var usesSafetyNet = command.Contains("--filter \"Category=Integration&Category=Slow&", StringComparison.Ordinal);
            Assert.True(usesShared || usesSafetyNet, $"CI test command without the shared filter: {command.Trim()}");
        }

        // ...and every test project has a main step with the shared filter.
        foreach (var project in TestProjects())
            Assert.Contains(commands, c => c.Contains($"tests/{project}/{project}.csproj", StringComparison.Ordinal)
                && c.Contains(SharedFilterArgument, StringComparison.Ordinal));

        // verify-all.ps1's per-project run passes the filter it builds (checked against CI above).
        Assert.Contains("'--filter', $filter,", ReadRepoFile("tools/verify-all.ps1"), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Rule R2-A-12: CI and verify-all.ps1 both run the translation and documentation gates")]
    public void CiAndLocalGate_RunTheSameScriptGates()
    {
        var ci = ReadRepoFile(".github/workflows/ci.yml");
        var gate = ReadRepoFile("tools/verify-all.ps1");

        foreach (var script in new[] { "i18n-check.ps1", "docs-budget.ps1", "check-doc-links.ps1" })
        {
            Assert.Matches(new Regex(@"^\s*run:\s*\./tools/" + Regex.Escape(script), RegexOptions.Multiline), ci);
            Assert.Matches(new Regex(@"^\s*&\s*\(Join-Path \$PSScriptRoot '" + Regex.Escape(script) + "'\\)", RegexOptions.Multiline), gate);
        }
    }

    [Fact(DisplayName = "Rule R2-A-12: the CI safety-net step covers every test project")]
    public void IntegrationSlowStep_CoversEveryTestProject()
    {
        var ci = ReadRepoFile(".github/workflows/ci.yml");
        var step = ci[ci.IndexOf("Integration-category tests that the main filter skips", StringComparison.Ordinal)..];

        foreach (var project in TestProjects())
            Assert.Contains($"'{project}'", step[..step.IndexOf("- name: Publish Release", StringComparison.Ordinal)], StringComparison.Ordinal);
    }
}
