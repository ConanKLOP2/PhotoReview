using System.Diagnostics;
using System.IO;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// TOOL-02 / TOOL-03: destructive-path guards in tools/*.ps1. Every scenario uses fake roots under an owned
/// temp directory; nothing outside it is created or deleted.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PowerShellSafetyGuardTests : IDisposable
{
    private readonly TempRoot _root = new("ps-guard");

    public void Dispose() => _root.Dispose();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PhotoReview.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static readonly string[] BaseArgs = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass"];

    private static (int ExitCode, string Output) RunPowerShell(params string[] args)
    {
        var psi = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in BaseArgs.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout + stderr.Result);
    }

    private static (int ExitCode, string Output) RunGuard(string directory, string approvedRoot)
    {
        var guard = Path.Combine(RepoRoot(), "tools", "Publish-Guard.ps1");
        var script = $"$ErrorActionPreference='Stop'; . '{guard}'; Assert-ReleaseDirectoryOwned -Directory '{directory}' -ApprovedRoots @('{approvedRoot}'); 'GUARD-OK'";
        return RunPowerShell("-Command", script);
    }

    private string MakeDir(string relative)
    {
        var dir = _root.Dir(relative);
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "x");
        return dir;
    }

    [Fact(DisplayName = "TOOL-02: an external directory named 'publish' without a marker is rejected and left intact")]
    public void ExternalSameLeafDirectoryIsRejected()
    {
        var approved = _root.Dir("approved");
        var external = MakeDir(Path.Combine("elsewhere", "publish"));

        var (code, output) = RunGuard(external, approved);

        Assert.NotEqual(0, code);
        Assert.Contains("Refusing to wipe", output);
        Assert.True(File.Exists(Path.Combine(external, "keep.txt")));
    }

    [Fact(DisplayName = "TOOL-02: a 'publish' directory under an approved root is accepted")]
    public void DirectoryUnderApprovedRootIsAccepted()
    {
        var approved = _root.Dir("approved");
        var inside = MakeDir(Path.Combine("approved", "bin", "publish"));

        var (code, output) = RunGuard(inside, approved);

        Assert.Equal(0, code);
        Assert.Contains("GUARD-OK", output);
    }

    [Fact(DisplayName = "TOOL-02: an external directory carrying the publisher marker is accepted")]
    public void ExternalDirectoryWithPublisherMarkerIsAccepted()
    {
        var approved = _root.Dir("approved");
        var external = MakeDir(Path.Combine("elsewhere", "PhotoReview-self-contained"));
        File.WriteAllText(Path.Combine(external, ".photoreview-publish"), "marker");

        var (code, output) = RunGuard(external, approved);

        Assert.Equal(0, code);
        Assert.Contains("GUARD-OK", output);
    }

    [Fact(DisplayName = "TOOL-02: a wrong leaf name is rejected even under an approved root")]
    public void WrongLeafUnderApprovedRootIsRejected()
    {
        var approved = _root.Dir("approved");
        var inside = MakeDir(Path.Combine("approved", "bin", "notpublish"));

        var (code, output) = RunGuard(inside, approved);

        Assert.NotEqual(0, code);
        Assert.Contains("Refusing to wipe", output);
        Assert.True(File.Exists(Path.Combine(inside, "keep.txt")));
    }

    [Fact(DisplayName = "TOOL-02: a sibling whose name merely starts with the approved root is not treated as inside it")]
    public void PrefixSiblingOfApprovedRootIsRejected()
    {
        var approved = _root.Dir("approved");
        var sibling = MakeDir(Path.Combine("approved-evil", "publish"));

        var (code, output) = RunGuard(sibling, approved);

        Assert.NotEqual(0, code);
        Assert.Contains("Refusing to wipe", output);
    }

    [Fact(DisplayName = "TOOL-02: '..' segments are resolved before the approved-root check, so a path that only looks inside is rejected")]
    public void DotDotTraversalOutOfApprovedRootIsRejected()
    {
        var approved = _root.Dir("approved");
        MakeDir(Path.Combine("elsewhere", "publish"));
        var sneaky = Path.Combine(approved, "..", "elsewhere", "publish");

        var (code, output) = RunGuard(sneaky, approved);

        Assert.NotEqual(0, code);
        Assert.Contains("Refusing to wipe", output);
        Assert.True(File.Exists(Path.Combine(_root.Path, "elsewhere", "publish", "keep.txt")));
    }

    [Fact(DisplayName = "TOOL-02: wildcard characters, spaces, a trailing separator on the root and an upper-case leaf do not break acceptance")]
    public void BracketsTrailingSeparatorAndLeafCaseAreAccepted()
    {
        var approved = _root.Dir("[approved] root");
        var inside = MakeDir(Path.Combine("[approved] root", "bin [x]", "PUBLISH"));

        var (code, output) = RunGuard(inside, approved + Path.DirectorySeparatorChar);

        Assert.Equal(0, code);
        Assert.Contains("GUARD-OK", output);
    }

    [Fact(DisplayName = "TOOL-02: a directory that does not exist yet is accepted anywhere (nothing can be wiped)")]
    public void MissingDirectoryIsAccepted()
    {
        var approved = _root.Dir("approved");

        var (code, output) = RunGuard(_root.Combine("nowhere", "publish"), approved);

        Assert.Equal(0, code);
        Assert.Contains("GUARD-OK", output);
    }

    [Theory(DisplayName = "TOOL-02: the approved root itself and a drive-like leaf are never wiped")]
    [InlineData("approved")]
    [InlineData("approved-publish")]
    public void ApprovedRootItselfOrLookalikeLeafIsRejected(string relative)
    {
        var approved = _root.Dir("approved");
        var target = relative == "approved" ? approved : MakeDir(relative);

        var (code, output) = RunGuard(target, approved);

        Assert.NotEqual(0, code);
        Assert.Contains("Refusing to wipe", output);
        Assert.True(Directory.Exists(target));
    }

    [Theory(DisplayName = "TOOL-03: run-matrix rejects the cold-diskcache condition combined with -SharedAppCache before doing anything")]
    [InlineData("cold-diskcache")]
    [InlineData("warm,cold-diskcache")]
    public void ColdDiskCacheConditionWithSharedAppCacheIsRejected(string conditions)
    {
        var script = Path.Combine(RepoRoot(), "tools", "diag", "run-matrix.ps1");

        var (code, output) = RunPowerShell("-File", script, "-Conditions", conditions, "-SharedAppCache",
            "-OutRoot", _root.Combine("out"), "-FixturesFile", _root.Combine("missing-fixtures.json"), "-SkipBuild");

        Assert.NotEqual(0, code);
        Assert.Contains("cannot be combined with -SharedAppCache", output);
        Assert.False(Directory.Exists(_root.Combine("out")));
    }

    [Fact(DisplayName = "TOOL-03: run-matrix does not raise the cache-combination error without -SharedAppCache")]
    public void ColdDiskCacheConditionAloneIsNotRejectedForCacheReasons()
    {
        var script = Path.Combine(RepoRoot(), "tools", "diag", "run-matrix.ps1");

        var (code, output) = RunPowerShell("-File", script, "-Conditions", "cold-diskcache",
            "-OutRoot", _root.Combine("out"), "-FixturesFile", _root.Combine("missing-fixtures.json"), "-SkipBuild");

        Assert.NotEqual(0, code); // fails later on the missing fixtures file, harmlessly
        Assert.DoesNotContain("cannot be combined with -SharedAppCache", output);
        Assert.Contains("Fixture file not found", output);
    }
}
