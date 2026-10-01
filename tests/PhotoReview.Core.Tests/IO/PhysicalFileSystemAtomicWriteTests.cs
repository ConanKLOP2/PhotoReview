using System.IO;
using PhotoReview.Core.IO;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.IO;

/// <summary>RV-C05 step 3: the atomic write's temp file never outlives a failed rename (session/settings files).</summary>
public sealed class PhysicalFileSystemAtomicWriteTests : IDisposable
{
    private readonly TempRoot _root = new("atomic-write");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "RV-C05: WriteAllTextAtomic whose final rename fails leaves no temp file behind")]
    public void WriteAllTextAtomic_RenameFails_LeavesNoTempFile()
    {
        // The target name is taken by a directory: the temp file is written, then File.Move over it fails.
        var target = Path.Combine(_root.Path, "session.json");
        Directory.CreateDirectory(target);
        var fs = new PhysicalFileSystem();

        var ex = Record.Exception(() => fs.WriteAllTextAtomic(target, "{}", durable: false));

        Assert.True(ex is IOException or UnauthorizedAccessException, $"unexpected: {ex}");
        Assert.Empty(Directory.GetFiles(_root.Path, "*.tmp"));
        Assert.True(Directory.Exists(target));
    }
}
