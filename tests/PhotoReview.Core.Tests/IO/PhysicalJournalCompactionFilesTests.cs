using System.Text;
using PhotoReview.Core.IO;

namespace PhotoReview.Core.Tests.IO;

/// <summary>Direct tests of <see cref="PhysicalJournalCompactionFiles"/>: the Windows rename/lock primitives, no <see cref="OperationJournal"/> involved.</summary>
public sealed class PhysicalJournalCompactionFilesTests : IDisposable
{
    private readonly TempRoot _root = new("journal-compaction-files");
    private readonly PhysicalJournalCompactionFiles _files = new();

    public void Dispose() => _root.Dispose();

    private string TargetPath => _root.Combine("operations.jsonl");
    private string NewTempPath() => _root.Combine("operations.jsonl." + Guid.NewGuid().ToString("N") + ".compact.tmp");

    private void WriteTarget(string content) => File.WriteAllText(TargetPath, content, new UTF8Encoding(false));

    [Fact(DisplayName = "ReplaceAtomically succeeds while a reader (ReadWrite|Delete) has the target open; the reader keeps seeing the old content")]
    public void ReplaceAtomically_TargetOpenByCompatibleReader_SucceedsAndReaderSeesOldContent()
    {
        WriteTarget("old-content");
        using var reader = new FileStream(TargetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        using (var replacement = _files.CreateStagedReplacement(NewTempPath()))
        {
            replacement.Write("new-content"u8);
            replacement.FlushToDisk();
            replacement.ReplaceAtomically(TargetPath);
            // The staged-replacement handle itself only shares Read (matching the real writer): it must be disposed
            // before anything else opens the (now renamed) target for reading, same as production's `using` scope.
        }

        var oldBytes = new byte[reader.Length];
        reader.ReadExactly(oldBytes);
        Assert.Equal("old-content", Encoding.UTF8.GetString(oldBytes));
        Assert.Equal("new-content", File.ReadAllText(TargetPath));
    }

    [Fact(DisplayName = "ReplaceAtomically succeeds while OpenReadDenyWriters holds the target locked")]
    public void ReplaceAtomically_TargetLockedByOpenReadDenyWriters_Succeeds()
    {
        WriteTarget("old-content");
        using var locked = _files.OpenReadDenyWriters(TargetPath);

        using (var replacement = _files.CreateStagedReplacement(NewTempPath()))
        {
            replacement.Write("new-content"u8);
            replacement.FlushToDisk();
            replacement.ReplaceAtomically(TargetPath);
        }

        var oldBytes = new byte[locked.Length];
        locked.ReadExactly(oldBytes);
        Assert.Equal("old-content", Encoding.UTF8.GetString(oldBytes));
        Assert.Equal("new-content", File.ReadAllText(TargetPath));
    }

    [Fact(DisplayName = "ReplaceAtomically throws while the target is held by an appender; the target is unchanged and Dispose deletes the temp file")]
    public void ReplaceAtomically_TargetHeldByAppender_ThrowsTargetUnchangedTempDeleted()
    {
        WriteTarget("old-content");
        var appender = new FileStream(TargetPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        var tempPath = NewTempPath();

        var replacement = _files.CreateStagedReplacement(tempPath);
        replacement.Write("new-content"u8);
        replacement.FlushToDisk();

        var ex = Assert.Throws<IOException>(() => replacement.ReplaceAtomically(TargetPath));
        Assert.Equal(unchecked((int)0x80070020), ex.HResult);
        replacement.Dispose();
        appender.Dispose(); // the appender eventually releases; a Write-sharing handle blocks even reads until then

        Assert.Equal("old-content", File.ReadAllText(TargetPath));
        Assert.False(File.Exists(tempPath));
    }

    [Fact(DisplayName = "Dispose without a successful ReplaceAtomically deletes the temp file")]
    public void Dispose_WithoutReplace_DeletesTempFile()
    {
        var tempPath = NewTempPath();
        var replacement = _files.CreateStagedReplacement(tempPath);
        replacement.Write("content"u8);
        replacement.FlushToDisk();
        Assert.True(File.Exists(tempPath));

        replacement.Dispose();

        Assert.False(File.Exists(tempPath));
    }

    [Fact(DisplayName = "OpenReadDenyWriters fails a FileMode.Append open with the sharing-violation HRESULT, but allows a compatible reader")]
    public void OpenReadDenyWriters_BlocksAppendOpens_AllowsCompatibleReaders()
    {
        WriteTarget("content");
        using var locked = _files.OpenReadDenyWriters(TargetPath);

        var ex = Assert.Throws<IOException>(() => new FileStream(TargetPath, FileMode.Append, FileAccess.Write, FileShare.Read));
        Assert.Equal(unchecked((int)0x80070020), ex.HResult);

        using var reader = new FileStream(TargetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[reader.Length];
        reader.ReadExactly(bytes);
        Assert.Equal("content", Encoding.UTF8.GetString(bytes));
    }
}
