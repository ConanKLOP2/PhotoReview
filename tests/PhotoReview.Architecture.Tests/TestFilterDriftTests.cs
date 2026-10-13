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

    // A project's main CI step: the shared TEST_FILTER, optionally narrowed to one namespace shard by
    // `&FullyQualifiedName~<ns>` (this shard) or `&FullyQualifiedName!~<ns>` (the complement shard). Anything else is drift.
    private static readonly Regex MainStepCommand = new(
        @"dotnet test tests/(?<p>PhotoReview\.[A-Za-z.]+\.Tests)/\k<p>\.csproj -c Release --no-build --filter ""\$env:TEST_FILTER(?:&FullyQualifiedName(?<neg>!?)~(?<ns>[A-Za-z0-9_.]+))?""",
        RegexOptions.CultureInvariant);

    private static Dictionary<string, string> CiJobs(string ci)
    {
        var jobStarts = Regex.Matches(ci, @"^  (?<id>[a-z][a-z0-9-]*):\s*$", RegexOptions.Multiline)
            .Where(m => m.Index > ci.IndexOf("\njobs:", StringComparison.Ordinal)).ToArray();
        var jobs = new Dictionary<string, string>();
        for (var i = 0; i < jobStarts.Length; i++)
        {
            var end = i + 1 < jobStarts.Length ? jobStarts[i + 1].Index : ci.Length;
            jobs[jobStarts[i].Groups["id"].Value] = ci[jobStarts[i].Index..end];
        }

        return jobs;
    }

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
            var usesShared = MainStepCommand.IsMatch(command);
            var usesSafetyNet = command.Contains("--filter \"Category=Integration&Category=Slow&", StringComparison.Ordinal);
            Assert.True(usesShared || usesSafetyNet, $"CI test command without the shared filter: {command.Trim()}");
        }

        // ...and every test project has a main step with the shared filter.
        foreach (var project in TestProjects())
            Assert.Contains(commands, c => MainStepCommand.Match(c) is { Success: true } m && m.Groups["p"].Value == project);

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

        // The AOT build keeps EventSourceSupport (without it PHOTOREVIEW_PERF_TRACE and startup-probe are blind); since WP-35 G5 the
        // shell has no WPF bridge reference in ANY variant, so no ShellAot-conditional bridge group may come back.
        var csproj = ReadRepoFile("src/PhotoReview.Shell.Win32/PhotoReview.Shell.Win32.csproj");
        Assert.Contains("<EventSourceSupport Condition=\"'$(ShellAot)' == 'true'\">true</EventSourceSupport>", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("PhotoReview.Shell.WpfBridge.csproj", csproj, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Rule R2-A-12: the CI safety-net steps (one per test job) together cover every test project")]
    public void IntegrationSlowStep_CoversEveryTestProject()
    {
        var ci = ReadRepoFile(".github/workflows/ci.yml");
        const string marker = "- name: Run Integration-category tests that the main filter skips";
        var steps = new List<string>();
        for (var at = ci.IndexOf(marker, StringComparison.Ordinal); at >= 0; at = ci.IndexOf(marker, at + marker.Length, StringComparison.Ordinal))
        {
            var end = ci.IndexOf("\n      - name:", at + marker.Length, StringComparison.Ordinal);
            steps.Add(end < 0 ? ci[at..] : ci[at..end]);
        }

        Assert.NotEmpty(steps);
        foreach (var project in TestProjects())
            Assert.Contains(steps, step => step.Contains($"'{project}'", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Rule CI-PARALLEL: each test project is tested by exactly one job (or by two complementary namespace shards), its safety net runs in one job that built it, and the aggregate build-test-publish job needs every build/test job")]
    public void EachProjectIsTestedInOneJob_AndTheAggregateGateNeedsAllJobs()
    {
        var ci = ReadRepoFile(".github/workflows/ci.yml");
        var jobs = CiJobs(ci);
        const string safetyMarker = "- name: Run Integration-category tests that the main filter skips";

        foreach (var project in TestProjects())
        {
            var mains = jobs.SelectMany(j => j.Value.Split('\n')
                    .Select(line => MainStepCommand.Match(line))
                    .Where(m => m.Success && m.Groups["p"].Value == project)
                    .Select(m => (Job: j.Key, Neg: m.Groups["neg"].Value == "!", Ns: m.Groups["ns"].Value, Sharded: m.Groups["ns"].Success)))
                .ToArray();

            if (mains.Length == 1)
            {
                Assert.False(mains[0].Sharded, $"{project}: a single main step must not be a shard (the other shard would be missing)");
            }
            else
            {
                // Sharded: exactly two steps, the same namespace, one positive (~) and one complement (!~), in different jobs.
                Assert.True(mains.Length == 2, $"{project}: expected one main step or two complementary shards, found {mains.Length}");
                Assert.All(mains, m => Assert.True(m.Sharded, $"{project}: a second main step must be a namespace shard"));
                Assert.Equal(mains[0].Ns, mains[1].Ns);
                Assert.NotEqual(mains[0].Neg, mains[1].Neg);
                Assert.NotEqual(mains[0].Job, mains[1].Job);
            }

            // The safety net runs exactly once per project, in a job that builds it (checked against the slnf below).
            var safetyJobs = jobs.Where(j =>
            {
                var at = j.Value.IndexOf(safetyMarker, StringComparison.Ordinal);
                if (at < 0) return false;
                var end = j.Value.IndexOf("\n      - name:", at + safetyMarker.Length, StringComparison.Ordinal);
                var step = end < 0 ? j.Value[at..] : j.Value[at..end];
                return step.Contains($"'{project}'", StringComparison.Ordinal) && step.Contains("Category=Integration&Category=Slow&", StringComparison.Ordinal);
            }).Select(j => j.Key).ToArray();
            Assert.Single(safetyJobs);
        }

        // Each job builds, through its solution filter (tools/ci/<job>.slnf), every project it tests.
        foreach (var (id, text) in jobs.Where(j => j.Value.Contains("dotnet test ", StringComparison.Ordinal)))
        {
            var build = Regex.Match(text, @"dotnet build (?<f>tools/ci/[A-Za-z0-9-]+\.slnf) -c Release --no-restore");
            Assert.True(build.Success, $"{id}: no `dotnet build tools/ci/<job>.slnf -c Release --no-restore` step before its tests");
            var filter = ReadRepoFile(build.Groups["f"].Value);
            // Projects tested by a main step (dotnet test tests/<p>/...) or listed in the safety-net foreach ('<p>', '<q>').
            var tested = Regex.Matches(text, @"dotnet test tests/(?<p>PhotoReview\.[A-Za-z.]+\.Tests)/").Select(m => m.Groups["p"].Value)
                .Concat(Regex.Matches(text, @"foreach \(\$project in (?<l>[^)]+)\)")
                    .SelectMany(m => Regex.Matches(m.Groups["l"].Value, @"'(?<p>PhotoReview\.[A-Za-z.]+\.Tests)'"))
                    .Select(m => m.Groups["p"].Value));
            foreach (var project in tested.Distinct())
                Assert.True(filter.Contains($"tests/{project}/{project}.csproj", StringComparison.Ordinal),
                    $"{id}: {project} is tested but not built by {build.Groups["f"].Value}");
        }

        // The aggregate job keeps the id the required status check and release.yml look up, and waits for every job that builds or tests.
        var aggregate = jobs["build-test-publish"];
        foreach (var job in jobs.Keys.Where(k => k.StartsWith("test-", StringComparison.Ordinal) || k == "build-publish" || k == "changes"))
            Assert.Contains(job, aggregate, StringComparison.Ordinal);
        Assert.Contains("if: always()", aggregate, StringComparison.Ordinal);
        Assert.DoesNotContain("publish-aot", aggregate, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Rule CI-PARALLEL: every project of PhotoReview.slnx is compiled by some CI build (a tools/ci/*.slnf or its ProjectReference closure)")]
    public void EveryProjectOfTheSolution_IsBuiltBySomeCiBuild()
    {
        // build-publish compiles only the published executables' closure and each test job only its own test projects, so a new
        // project referenced by none of them would silently stop being compiled (and analysed) in CI.
        var slnx = ReadRepoFile("PhotoReview.slnx");
        var solutionProjects = Regex.Matches(slnx, @"<Project Path=""(?<p>[^""]+)""")
            .Select(m => m.Groups["p"].Value.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(solutionProjects);

        var roots = Directory.GetFiles(Path.Combine(RepoScan.Root, "tools", "ci"), "*.slnf")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"""(?<p>[^""]+\.csproj)""").Select(m => m.Groups["p"].Value.Replace('\\', '/')))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(roots);

        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(roots);
        while (queue.Count > 0)
        {
            var project = queue.Dequeue();
            if (!reached.Add(project)) continue;
            var dir = Path.GetDirectoryName(project)!.Replace('\\', '/');
            foreach (Match reference in Regex.Matches(ReadRepoFile(project), @"<ProjectReference\s+Include=""(?<p>[^""]+)"""))
            {
                var target = Path.GetFullPath(Path.Combine(RepoScan.Root, dir, reference.Groups["p"].Value.Replace('\\', '/')));
                queue.Enqueue(Path.GetRelativePath(RepoScan.Root, target).Replace('\\', '/'));
            }
        }

        var unbuilt = solutionProjects.Where(p => !reached.Contains(p)).ToArray();
        Assert.True(unbuilt.Length == 0, "Solution projects built by no CI job: " + string.Join(", ", unbuilt));
    }
}
