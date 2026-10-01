using System.Diagnostics;
using System.IO;
using PhotoReview.Core.IO;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// RV-S06: a reparse-point loop along a relative action destination (a -&gt; b -&gt; a) cannot be resolved, so the SEC-01
/// containment check cannot prove the destination stays inside the photo folder. It must fail CLOSED (report
/// <see cref="ActionDestinationCheck.EscapesSourceFolder"/>) instead of throwing the resolver's IOException at the caller.
/// Real OS links: a directory symlink loop when the process may create symlinks, otherwise an NTFS junction loop
/// (<c>mklink /J</c> needs no privilege); when neither can be created the test returns early (xUnit 2.9 has no Assert.Skip).
/// </summary>
[Trait("Category", "Native")]
public sealed class PhysicalFileSystemReparseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RVS06_Reparse_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = [];

    public PhysicalFileSystemReparseTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Remove the links themselves first (never their targets), then the temp tree.
        foreach (var link in _links)
        {
            try { Directory.Delete(link); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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

        // No SeCreateSymbolicLinkPrivilege (not elevated, no Developer Mode): a junction loops just the same.
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

    [Fact(DisplayName = "RV-S06 (Native): a reparse-point loop in a relative destination is rejected (fail closed), not thrown")]
    public void ValidateNoEscapeViaReparsePoint_ReparseLoop_FailsClosed()
    {
        var photoFolder = Path.Combine(_root, "photos");
        Directory.CreateDirectory(photoFolder);
        var a = Path.Combine(photoFolder, "a");
        var b = Path.Combine(photoFolder, "b");
        if (!TryCreateDirectoryLink(a, b) || !TryCreateDirectoryLink(b, a))
        {
            Console.WriteLine("RV-S06: skipped, this machine cannot create a symlink or junction in the temp folder.");
            return;
        }

        var destination = Path.Combine(a, "processed", "photo.jpg");
        var check = ActionDestinationCheck.Ok;
        var ex = Record.Exception(() =>
            check = ActionDestinationPolicy.ValidateNoEscapeViaReparsePoint(photoFolder, destination, new PhysicalFileSystem()));

        Assert.Null(ex);
        Assert.Equal(ActionDestinationCheck.EscapesSourceFolder, check);
    }

    [Fact(DisplayName = "RV-S06 (Native): an ordinary relative destination next to the loop is still allowed")]
    public void ValidateNoEscapeViaReparsePoint_OrdinaryDestinationBesideLoop_StaysOk()
    {
        var photoFolder = Path.Combine(_root, "photos");
        Directory.CreateDirectory(photoFolder);
        var a = Path.Combine(photoFolder, "a");
        var b = Path.Combine(photoFolder, "b");
        if (!TryCreateDirectoryLink(a, b) || !TryCreateDirectoryLink(b, a))
        {
            Console.WriteLine("RV-S06: skipped, this machine cannot create a symlink or junction in the temp folder.");
            return;
        }

        var destination = Path.Combine(photoFolder, "sel", "photo.jpg");

        Assert.Equal(ActionDestinationCheck.Ok,
            ActionDestinationPolicy.ValidateNoEscapeViaReparsePoint(photoFolder, destination, new PhysicalFileSystem()));
    }
}
