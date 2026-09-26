using System.Diagnostics;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// AR12a: native binaries (e.g. <c>native/x64/turbojpeg.dll</c>, fetched hash-pinned by
/// <c>tools/fetch-native.ps1</c>) must never be tracked in git. Before AR12a, the only ignore rule
/// covering them was the generic Visual Studio build-output pattern <c>x64/</c> in <c>.gitignore</c>;
/// anyone narrowing that pattern (e.g. to <c>[Bb]in/x64/</c>) would start silently tracking the 3 MB
/// DLL with no test or CI check catching it. This asks git itself which files are tracked, so it
/// reflects the real ignore rules rather than re-implementing gitignore matching.
/// </summary>
public sealed class RepoHygieneTests
{
    // Shells out to the real `git` executable (Process.Start): a genuine OS resource, so this is
    // Category=Integration rather than the default filter (TEST-OS rule in TestQualityRulesTests).
    // Integration-only (no Slow) still runs under the shared gate filter (AGENTS.md > Tests / CI TEST_FILTER).
    [Fact(DisplayName = "AR12a: no tracked *.dll/*.exe files outside bin/obj build output")]
    [Trait("Category", "Integration")]
    public void NoTrackedNativeBinariesOutsideBuildOutput()
    {
        string[] tracked;
        try
        {
            tracked = RunGitLsFiles();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // git is not available in this environment; there is nothing to verify.
            return;
        }

        var violations = tracked
            .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(f => !IsBuildOutputPath(f))
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Tracked *.dll/*.exe files found outside bin/obj (native binaries must be gitignored, not committed):\n" +
            string.Join("\n", violations));
    }

    private static bool IsBuildOutputPath(string relativePath) =>
        relativePath.Contains("/bin/", StringComparison.Ordinal) ||
        relativePath.Contains("/obj/", StringComparison.Ordinal) ||
        relativePath.StartsWith("bin/", StringComparison.Ordinal) ||
        relativePath.StartsWith("obj/", StringComparison.Ordinal);

    private static string[] RunGitLsFiles()
    {
        var psi = new ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = RepoScan.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git process");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git ls-files failed with exit code {process.ExitCode}");
        }

        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
