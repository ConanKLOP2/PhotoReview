namespace PhotoReview.Architecture.Tests;

public sealed class EnvironmentVariableScopeTests
{
    private const string TargetVariable = "PHOTOREVIEW_DATA_ROOT";

    [Fact(DisplayName = "Rule 5: Only AppPaths.cs contains the literal string PHOTOREVIEW_DATA_ROOT across src/")]
    public void Only_AppPaths_Contains_String_PHOTOREVIEW_DATA_ROOT_In_Source()
    {
        var csFiles = RepoScan.CsFiles("src");
        Assert.NotEmpty(csFiles);

        var violations = new List<string>();

        foreach (var file in csFiles)
        {
            var relativePath = RepoScan.Relative(file);

            // AppPaths.cs is the ONLY file allowed to contain this environment variable name
            if (relativePath.Equals("src/PhotoReview.Core/AppPaths.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (RepoScan.Text(file).Contains(TargetVariable, StringComparison.Ordinal))
            {
                violations.Add(relativePath);
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Found forbidden occurrences of '{TargetVariable}' outside AppPaths.cs:\n{string.Join("\n", violations)}");
    }
}
