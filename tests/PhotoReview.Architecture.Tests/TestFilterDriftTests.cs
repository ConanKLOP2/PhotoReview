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

    [Fact(DisplayName = "Rule R2-A-12: the CI safety-net step covers every test project")]
    public void IntegrationSlowStep_CoversEveryTestProject()
    {
        var ci = ReadRepoFile(".github/workflows/ci.yml");
        var step = ci[ci.IndexOf("Integration-category tests that the main filter skips", StringComparison.Ordinal)..];
        var projects = Directory.GetDirectories(Path.Combine(RepoScan.Root, "tests"), "PhotoReview.*.Tests")
            .Select(Path.GetFileName)
            .Where(name => Directory.GetFiles(Path.Combine(RepoScan.Root, "tests", name!), "*.csproj").Length > 0);

        foreach (var project in projects)
            Assert.Contains($"'{project}'", step[..step.IndexOf("- name: Publish Release", StringComparison.Ordinal)], StringComparison.Ordinal);
    }
}
