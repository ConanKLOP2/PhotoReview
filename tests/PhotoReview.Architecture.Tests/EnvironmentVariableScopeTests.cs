using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace PhotoReview.Architecture.Tests;

public sealed class EnvironmentVariableScopeTests
{
    private const string TargetVariable = "PHOTOREVIEW_DATA_ROOT";

    [Fact(DisplayName = "Rule 5: Only AppPaths.cs contains the literal string PHOTOREVIEW_DATA_ROOT across src/")]
    public void Only_AppPaths_Contains_String_PHOTOREVIEW_DATA_ROOT_In_Source()
    {
        var repoRoot = FindRepoRoot();
        var srcDir = Path.Combine(repoRoot, "src");
        Assert.True(Directory.Exists(srcDir), $"Expected src directory at {srcDir}");

        var csFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(csFiles);

        var violations = new List<string>();

        foreach (var file in csFiles)
        {
            var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

            // AppPaths.cs is the ONLY file allowed to contain this environment variable name
            if (relativePath.Equals("src/PhotoReview.Core/AppPaths.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (text.Contains(TargetVariable, StringComparison.Ordinal))
            {
                violations.Add(relativePath);
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Found forbidden occurrences of '{TargetVariable}' outside AppPaths.cs:\n{string.Join("\n", violations)}");
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PhotoReview.slnx")) ||
                Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root (looking for PhotoReview.slnx or .git)");
    }
}
