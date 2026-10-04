using System.Diagnostics;
using System.IO;
using PhotoReview.Benchmark.Cli;

namespace PhotoReview.Integration.Tests;

/// <summary>T-B-13: the output-directory guards resolve junctions/symlinks. Creates a real link in a temp folder (Native).</summary>
[Trait("Category", "Native")]
public sealed class BenchmarkCliJunctionTests : IDisposable
{
    private readonly TempRoot _root = new("cli-junction");
    private readonly List<string> _links = [];

    public void Dispose()
    {
        // Remove the links themselves first (never their targets), then the temp tree.
        foreach (var link in _links)
        {
            try { Directory.Delete(link); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        _root.Dispose();
    }

    private bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            _links.Add(linkPath);
            return true;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        // No symlink privilege: a junction resolves just the same.
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        if (process is null) return false;
        if (!process.WaitForExit(30_000))
        {
            process.Kill();
            return false;
        }

        if (process.ExitCode != 0) return false;
        _links.Add(linkPath);
        return true;
    }

    [Fact(DisplayName = "T-B-13 (Native): an outDir that is a junction into the photo folder is refused although its text does not overlap")]
    public void OutDirJunctionIntoThePhotoFolder_IsRefused()
    {
        var photos = _root.Dir("photos");
        File.WriteAllBytes(Path.Combine(photos, "a.jpg"), [1, 2, 3]);
        var link = _root.Combine("link-to-photos");
        Assert.True(TryCreateDirectoryLink(link, photos), "could not create a junction or symlink in the temp folder");

        Assert.Throws<InvalidOperationException>(() => PerfSession.ValidatePaths(photos, link, Path.Combine(link, "data"), Path.Combine(link, "copy")));
        Assert.Throws<InvalidOperationException>(() => ToolPathGuard.EnsureOutputDirectory(Path.Combine(link, "reports"), photos));
        Assert.Throws<InvalidOperationException>(() => ToolPathGuard.EnsureOutputFile(Path.Combine(link, "report.md"), photos));
    }
}
