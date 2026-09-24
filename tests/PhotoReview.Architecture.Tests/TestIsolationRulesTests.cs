namespace PhotoReview.Architecture.Tests;

/// <summary>
/// TEST-09: a test that changes process-wide environment variables (directly or through
/// <c>DataRootFixture</c>) must run in the non-parallel "GlobalState" collection, otherwise it can
/// leak PHOTOREVIEW_DATA_ROOT into a concurrently running test.
/// </summary>
public sealed class TestIsolationRulesTests
{
    // Built by concatenation so the literals stay greppable-clean.
    private const string SetEnvCall = "Environment." + "SetEnvironmentVariable(";
    private const string DataRootFixtureName = "DataRoot" + "Fixture";
    private const string CollectionAttribute = "[Collection(\"GlobalState\")]";

    [Fact(DisplayName = "Rule TEST-09: test files that mutate environment variables are in the GlobalState collection")]
    public void EnvironmentMutatingTests_AreInGlobalStateCollection()
    {
        var violations = new List<string>();
        var scanned = 0;

        foreach (var file in RepoScan.CsFiles("tests"))
        {
            var relative = RepoScan.Relative(file);
            // The fixture itself, shared helpers and this rule file (its docs name the fixture) are not tests.
            if (relative.StartsWith("tests/PhotoReview.TestSupport", StringComparison.Ordinal) || relative.EndsWith("/TestIsolationRulesTests.cs", StringComparison.Ordinal)) continue;

            var text = RepoScan.Text(file);
            if (!text.Contains(SetEnvCall, StringComparison.Ordinal) &&
                !text.Contains(DataRootFixtureName, StringComparison.Ordinal))
                continue;

            scanned++;
            if (!text.Contains(CollectionAttribute, StringComparison.Ordinal))
                violations.Add(relative);
        }

        Assert.True(scanned > 0, "The scan found no environment-mutating test files; the rule is not checking anything.");
        Assert.True(
            violations.Count == 0,
            "These test files mutate environment variables (or use DataRootFixture) but lack " +
            $"{CollectionAttribute}:\n{string.Join("\n", violations)}");
    }
}
