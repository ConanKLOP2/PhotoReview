using System.IO;
using System.Xml.Linq;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// The LibRaw project runs tools/fetch-libraw.ps1 on every build. A failed run (offline, source package missing from the
/// checkout) must only warn: the DLL may still be usable, and LibRawAvailability / verify-release.ps1 fail closed on a
/// missing or wrong one. Reads the project file's structure (an MSBuild run would need the network or the real DLL).
/// </summary>
public sealed class LibRawBuildTargetTests
{
    private static XElement FetchExec()
    {
        var path = Path.Combine(PowerShellRunner.RepoRoot(), "src", "PhotoReview.Imaging.LibRaw", "PhotoReview.Imaging.LibRaw.csproj");
        var project = XDocument.Load(path).Root!;
        var target = project.Elements("Target").Single(t => (string?)t.Attribute("Name") == "EnsureLibRawBinary");
        return target.Elements("Exec").Single(e => ((string?)e.Attribute("Command") ?? string.Empty).Contains("FetchLibRawScriptPath"));
    }

    [Fact]
    public void FetchLibRawExec_FailureIsAWarningNotABuildError()
    {
        Assert.Equal("WarnAndContinue", (string?)FetchExec().Attribute("ContinueOnError"));
    }

    [Fact]
    public void FetchLibRawExec_RunsEvenWhenTheDllExists_SoAStaleDllIsStillCaught()
    {
        var condition = (string?)FetchExec().Attribute("Condition") ?? string.Empty;

        // Only the explicit opt-out property may suppress it; an existing DLL alone must not.
        Assert.Contains("LibRawFetchOnEveryBuild", condition);
        Assert.Contains("!Exists('$(LibRawDllPath)') or", condition);
    }
}
