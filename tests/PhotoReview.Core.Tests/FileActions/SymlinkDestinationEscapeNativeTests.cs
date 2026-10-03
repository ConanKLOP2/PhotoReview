using System.Diagnostics;
using System.Threading.Tasks;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// SEC-01: end-to-end proof with a REAL NTFS junction (created via <c>mklink /J</c> — <see cref="Directory.CreateSymbolicLink"/>
/// needs SeCreateSymbolicLinkPrivilege, which is not available to a normal, non-elevated, non-Developer-Mode process;
/// a junction needs no special privilege and is exactly the reparse-point kind the vulnerability report used as its
/// example) that a relative Move/Copy destination which traverses an existing junction cannot escape the photo
/// folder, using the real <see cref="PhysicalFileSystem"/> (not a fake). If junction creation still fails on some
/// machine (e.g. a filesystem that doesn't support reparse points), the test skips itself gracefully (early return,
/// same pattern as <c>RealPhotosManualTests</c> — xUnit 2.9.3 has no <c>Assert.Skip</c>); this is a real-OS check the
/// local hang-guard/CI gate never runs by default (Category=Native is excluded from the standard filter).
/// </summary>
[Trait("Category", "Native")]
public sealed class SymlinkDestinationEscapeNativeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SEC01_Symlink_" + Guid.NewGuid().ToString("N"));
    private readonly PhysicalFileSystem _fs = new();

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; } = new(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class FakeAppPaths(string journalPath) : IAppPaths
    {
        public string ConfigFile => Path.Combine(Path.GetDirectoryName(journalPath)!, "config.json");
        public string JournalFile => journalPath;
        public string SessionsDir => Path.Combine(Path.GetDirectoryName(journalPath)!, "Sessions");
        public string LogFile => Path.Combine(Path.GetDirectoryName(journalPath)!, "logs", "app.log");
        public string PreviewCacheDir => Path.Combine(Path.GetDirectoryName(journalPath)!, "cache");
        public string ThumbnailCacheDir => Path.Combine(Path.GetDirectoryName(journalPath)!, "thumbnails");
        public string WindowPlacementFile => Path.Combine(Path.GetDirectoryName(journalPath)!, "window-placement.json");
    }

    private sealed class FakeRecycleBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) { }
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => true;
    }

    public SymlinkDestinationEscapeNativeTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    /// <summary>Creates a real NTFS junction at <paramref name="linkPath"/> pointing at <paramref name="targetPath"/>
    /// via <c>cmd /c mklink /J</c> (no special privilege needed, unlike a symlink). Returns false instead of
    /// throwing if it could not be created, so the test can skip itself gracefully.</summary>
    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        process!.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(linkPath) && CanCreateDirectoryThroughJunction(linkPath, targetPath);
    }

    /// <summary>Junction creation succeeding does not prove the junction is usable for WRITES: under some
    /// filesystem-virtualizing setups (observed for %LOCALAPPDATA%\Temp when the process is launched from an
    /// AppData-redirected host such as the Claude desktop app) <c>mklink /J</c> exits 0 and the link lists fine, but
    /// creating a directory through it fails and never reaches the target. That is a machine precondition, not a
    /// defect in <see cref="FileActionService"/>, so probe it and let the test skip instead of failing spuriously.</summary>
    private static bool CanCreateDirectoryThroughJunction(string linkPath, string targetPath)
    {
        var probeName = ".junction-probe-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.Combine(linkPath, probeName));
            return Directory.Exists(Path.Combine(targetPath, probeName));
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(Path.Combine(targetPath, probeName)); } catch { /* best-effort probe cleanup */ }
        }
    }

    private FileActionService CreateService(out OperationJournal journal)
    {
        var paths = new FakeAppPaths(Path.Combine(_root, "operations.jsonl"));
        journal = new OperationJournal(paths, _fs, new FakeClock());
        return new FileActionService(journal, _fs, new FakeClock(), new FakeRecycleBin());
    }

    [Fact(DisplayName = "SEC-01 (Native): a relative destination through a REAL junction escaping the photo folder is rejected")]
    public async Task ExecuteAsync_RealSymlinkEscapesFolder_Rejected()
    {
        var photoFolder = Path.Combine(_root, "photos");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(photoFolder);
        Directory.CreateDirectory(outside);

        var linkPath = Path.Combine(photoFolder, "link");
        if (!TryCreateJunction(linkPath, outside))
        {
            // Junction creation failed on this machine (e.g. filesystem without reparse-point support) — a real-OS
            // precondition, not a test failure; Category=Native already excludes this from the default gate.
            return;
        }

        var source = Path.Combine(photoFolder, "a.jpg");
        File.WriteAllText(source, "hello photo");

        var service = CreateService(out _);
        var result = await service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"link\processed"));

        Assert.False(result.Succeeded);
        Assert.True(File.Exists(source), "the source must stay put when the destination is rejected");
        Assert.False(File.Exists(Path.Combine(outside, "processed", "a.jpg")), "nothing must land outside the photo folder");
    }

    [Fact(DisplayName = "SEC-01 (Native): a REAL junction that resolves back inside the photo folder stays allowed")]
    public async Task ExecuteAsync_RealSymlinkStaysInsideFolder_Succeeds()
    {
        var photoFolder = Path.Combine(_root, "photos");
        var real = Path.Combine(photoFolder, "real");
        Directory.CreateDirectory(photoFolder);
        Directory.CreateDirectory(real);

        var linkPath = Path.Combine(photoFolder, "link");
        if (!TryCreateJunction(linkPath, real))
        {
            return; // see comment above
        }

        var source = Path.Combine(photoFolder, "a.jpg");
        File.WriteAllText(source, "hello photo");

        var service = CreateService(out _);
        var result = await service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"link\processed"));

        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(Path.Combine(real, "processed", "a.jpg")));
    }

    [Fact(DisplayName = "SEC-01 (Native): an ordinary relative destination with no symlinks involved is unaffected")]
    public async Task ExecuteAsync_OrdinaryDestination_Unaffected()
    {
        var photoFolder = Path.Combine(_root, "photos");
        Directory.CreateDirectory(photoFolder);

        var source = Path.Combine(photoFolder, "a.jpg");
        File.WriteAllText(source, "hello photo");

        var service = CreateService(out _);
        var result = await service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"sub\deeper"));

        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(Path.Combine(photoFolder, "sub", "deeper", "a.jpg")));
    }
}
