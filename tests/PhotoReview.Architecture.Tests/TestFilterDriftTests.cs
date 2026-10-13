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

    [Fact(DisplayName = "Rule WP-30: CI and release.yml publish BOTH executables through tools/publish-release.ps1 into the same folder and verify it with -Smoke")]
    public void CiAndRelease_PublishBothExecutablesTheSameWay()
    {
        const string folder = "src/PhotoReview.App/bin/Release/net10.0-windows/publish";
        foreach (var workflow in new[] { ".github/workflows/ci.yml", ".github/workflows/release.yml" })
        {
            var text = ReadRepoFile(workflow);
            Assert.Contains($"./tools/publish-release.ps1 -NoRestore -OutputDirectory '{folder}'", text, StringComparison.Ordinal);
            Assert.Contains($"./tools/verify-release.ps1 -Smoke -ReleaseDirectory '{folder}'", text, StringComparison.Ordinal);
            // No workflow may publish a single project directly again: that would ship one executable only.
            Assert.DoesNotContain("dotnet publish", text, StringComparison.Ordinal);
        }

        var script = ReadRepoFile("tools/publish-release.ps1");
        Assert.Contains(@"src\PhotoReview.App\PhotoReview.App.csproj", script, StringComparison.Ordinal);
        Assert.Contains(@"src\PhotoReview.Shell.Win32\PhotoReview.Shell.Win32.csproj", script, StringComparison.Ordinal);
        // The local gate publishes the same two projects.
        var gate = ReadRepoFile("tools/verify-all.ps1");
        Assert.Contains("PhotoReview.Shell.Win32.csproj", gate, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Rule WP-30: verify-release.ps1 requires both executables and every shell library of the package")]
    public void VerifyRelease_ListsTheShellFiles()
    {
        var script = ReadRepoFile("tools/verify-release.ps1");
        foreach (var file in new[]
        {
            "PhotoReview.App.exe", "PhotoReview.exe", "PhotoReview.dll", "PhotoReview.deps.json", "PhotoReview.runtimeconfig.json",
            "PhotoReview.Shell.Interop.dll", "PhotoReview.Shell.Rendering.dll", "PhotoReview.App.WpfWindows.dll",
        })
            Assert.Contains($"'{file}'", script, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Rule WP-36: the optional Native AOT variant is a separate non-blocking CI job and is never part of the normal release folder")]
    public void Ci_PublishesAotAsSeparateNonBlockingArtifact()
    {
        var ci = ReadRepoFile(".github/workflows/ci.yml");
        var start = ci.IndexOf("  publish-aot:", StringComparison.Ordinal);
        Assert.True(start >= 0, "publish-aot job not found in ci.yml");
        var next = ci.IndexOf("\n  tag-version:", start, StringComparison.Ordinal);
        var job = ci[start..next];

        Assert.Contains("continue-on-error: true", job, StringComparison.Ordinal);
        Assert.Contains("./tools/publish-release.ps1 -AotOnly -RequireAot", job, StringComparison.Ordinal);
        Assert.Contains("./tools/verify-release.ps1 -AotOnly -Smoke -AotDirectory", job, StringComparison.Ordinal);
        Assert.Contains("name: release-publish-aot", job, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet publish", job, StringComparison.Ordinal);
        // The release gate (tag-version) must not wait for, or depend on, the experimental variant.
        var tag = ci[next..];
        Assert.DoesNotContain("publish-aot", tag[..Math.Min(tag.Length, 400)], StringComparison.Ordinal);

        // The normal release script/workflow publish the same two projects as before: the AOT variant is opt-in (-Aot).
        var release = ReadRepoFile(".github/workflows/release.yml");
        Assert.DoesNotContain("ShellAot", release, StringComparison.Ordinal);
        Assert.DoesNotContain("-Aot", release, StringComparison.Ordinal);

        // The AOT build drops the WPF bridge and keeps EventSourceSupport (without it PHOTOREVIEW_PERF_TRACE and startup-probe are blind).
        var csproj = ReadRepoFile("src/PhotoReview.Shell.Win32/PhotoReview.Shell.Win32.csproj");
        Assert.Contains("<EventSourceSupport Condition=\"'$(ShellAot)' == 'true'\">true</EventSourceSupport>", csproj, StringComparison.Ordinal);
        Assert.Contains("<ItemGroup Condition=\"'$(ShellAot)' != 'true'\">", csproj, StringComparison.Ordinal);
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
