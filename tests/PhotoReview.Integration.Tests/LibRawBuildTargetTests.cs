using System.Diagnostics;
using System.IO;
using System.Text;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// The LibRaw project runs tools/fetch-libraw.ps1 on every build. Behavioural: the real EnsureLibRawBinary target of the real
/// project is executed by MSBuild with a stub fetch script (and a fake DLL path) injected through global properties, and the tests
/// observe whether the stub ran. A failed fetch (offline, source package missing from the checkout) must only warn: the DLL may
/// still be usable, and LibRawAvailability / verify-release.ps1 fail closed on a missing or wrong one.
/// </summary>
public sealed class LibRawBuildTargetTests : IDisposable
{
    private readonly TempRoot _root = new("libraw-target");
    public void Dispose() => _root.Dispose();

    private static string ProjectPath() =>
        Path.Combine(PowerShellRunner.RepoRoot(), "src", "PhotoReview.Imaging.LibRaw", "PhotoReview.Imaging.LibRaw.csproj");

    /// <summary>Runs the target; returns the MSBuild exit code, its output and whether the stub fetch script was invoked.</summary>
    private (int ExitCode, string Output, bool StubRan) RunTarget(string name, bool dllExists, string? fetchOnEveryBuild, int stubExitCode = 0)
    {
        var dir = _root.Dir(name);
        var marker = Path.Combine(dir, "stub-ran.txt");
        var stub = Path.Combine(dir, "stub-fetch.ps1");
        File.WriteAllText(stub, $"[IO.File]::WriteAllText('{marker}', 'ran')\r\nexit {stubExitCode}\r\n", new UTF8Encoding(false));
        var dll = Path.Combine(dir, "libraw.dll");
        if (dllExists) File.WriteAllBytes(dll, [1, 2, 3]);

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "msbuild", ProjectPath(), "-nologo", "-nodeReuse:false", "-v:minimal", "-t:EnsureLibRawBinary",
                     "-p:LibRawDllPath=" + dll, "-p:FetchLibRawScriptPath=" + stub })
            psi.ArgumentList.Add(arg);
        if (fetchOnEveryBuild is not null) psi.ArgumentList.Add("-p:LibRawFetchOnEveryBuild=" + fetchOnEveryBuild);
        PowerShellRunner.ForWindowsPowerShell(psi);
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("dotnet msbuild did not finish within 3 minutes");
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result, File.Exists(marker));
    }

    [Fact(DisplayName = "EnsureLibRawBinary: with the DLL present and the opt-out set, the fetch script is not invoked")]
    public void EnsureLibRawBinary_DllPresentAndOptOut_DoesNotRunTheFetchScript()
    {
        var (code, output, ran) = RunTarget("present-optout", dllExists: true, fetchOnEveryBuild: "false");

        Assert.True(code == 0, output);
        Assert.False(ran, output);
    }

    [Fact(DisplayName = "EnsureLibRawBinary: with the DLL missing the fetch script is invoked even when the opt-out is set")]
    public void EnsureLibRawBinary_DllMissing_RunsTheFetchScript()
    {
        var (code, output, ran) = RunTarget("missing", dllExists: false, fetchOnEveryBuild: "false");

        Assert.True(code == 0, output);
        Assert.True(ran, output);
    }

    [Fact(DisplayName = "EnsureLibRawBinary: by default the fetch script runs even though the DLL exists, so a stale DLL is caught")]
    public void EnsureLibRawBinary_DefaultRunsEvenWhenTheDllExists()
    {
        var (code, output, ran) = RunTarget("present-default", dllExists: true, fetchOnEveryBuild: null);

        Assert.True(code == 0, output);
        Assert.True(ran, output);
    }

    [Fact(DisplayName = "EnsureLibRawBinary: a failing fetch script is a warning, not a build error")]
    public void EnsureLibRawBinary_FailingFetch_WarnsButDoesNotFailTheBuild()
    {
        var (code, output, ran) = RunTarget("failing", dllExists: false, fetchOnEveryBuild: null, stubExitCode: 1);

        Assert.True(ran, output);
        Assert.True(code == 0, output);
        Assert.Contains("warning", output, StringComparison.OrdinalIgnoreCase);
    }
}
