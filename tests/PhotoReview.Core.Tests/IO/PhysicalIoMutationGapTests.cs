using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;

namespace PhotoReview.Core.Tests.IO;

/// <summary>
/// Mutation-testing gaps in <see cref="PhysicalFileSystem"/> and <see cref="PhysicalJournalCompactionFiles"/>. Real file system,
/// private temp directory only; the Recycle Bin is never involved.
/// </summary>
public sealed class PhysicalIoMutationGapTests : IDisposable
{
    private readonly TempRoot _root = new("physical-io-gap");
    private readonly PhysicalFileSystem _fs = new();

    public void Dispose() => _root.Dispose();

    // --- PhysicalJournalCompactionFiles -------------------------------------------------------------------------------

    [Fact]
    public void CreateStagedReplacement_FileAlreadyExists_ThrowsIOExceptionWithFileExistsAndKeepsTheFile()
    {
        var temp = _root.File("staged.tmp", 1, 2, 3);

        var ex = Assert.Throws<IOException>(() => new PhysicalJournalCompactionFiles().CreateStagedReplacement(temp));

        Assert.Equal(80, ex.HResult & 0xFFFF); // ERROR_FILE_EXISTS: CREATE_NEW never reuses or truncates a file
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(temp));
    }

    [Fact]
    public void StagedReplacementDispose_LeftoverTempThatCannotBeDeleted_DoesNotThrow()
    {
        var temp = _root.Combine("staged.tmp");
        var staged = new PhysicalJournalCompactionFiles().CreateStagedReplacement(temp);
        staged.Write("x"u8);
        File.SetAttributes(temp, FileAttributes.ReadOnly); // File.Delete now fails with UnauthorizedAccessException

        try
        {
            var ex = Record.Exception(staged.Dispose);

            Assert.Null(ex);
            Assert.True(File.Exists(temp)); // left behind for the next compaction to remove
        }
        finally
        {
            File.SetAttributes(temp, FileAttributes.Normal);
        }
    }

    // --- PhysicalFileSystem -------------------------------------------------------------------------------------------

    [Fact]
    public void TryCopyNew_SourceMissing_ThrowsInsteadOfReportingAnExistingDestination()
    {
        var destination = _root.Combine("copy.jpg");

        Assert.Throws<FileNotFoundException>(() => _fs.TryCopyNew(_root.Combine("missing.jpg"), destination));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void TryCopyNew_DestinationFolderMissing_Throws()
    {
        var source = _root.File("a.jpg", 1);

        Assert.Throws<DirectoryNotFoundException>(() => _fs.TryCopyNew(source, _root.Combine("nodir", "a.jpg")));
    }

    [Fact]
    public void TryCopyNew_DestinationIsADirectory_ThrowsInsteadOfReturningFalse()
    {
        var source = _root.File("a.jpg", 1);
        var directory = _root.Dir("taken");

        Assert.ThrowsAny<IOException>(() => RethrowUnauthorizedAsIo(() => _fs.TryCopyNew(source, directory)));
    }

    private static void RethrowUnauthorizedAsIo(Action action)
    {
        try { action(); }
        catch (UnauthorizedAccessException ex) { throw new IOException(ex.Message, ex); }
    }

    [Fact]
    public void TryCopyNew_DestinationFileExists_ReturnsFalseAndKeepsItsContent()
    {
        var source = _root.File("a.jpg", 1);
        var destination = _root.File("b.jpg", 9, 9);

        Assert.False(_fs.TryCopyNew(source, destination));
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(destination));
    }

    [Fact]
    public void TryProbeReadable_FileAlsoOpenForWriting_IsReadableBecauseTheProbeSharesReadWriteAndDelete()
    {
        var path = _root.File("busy.jpg", 1);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        Assert.True(_fs.TryProbeReadable(path, out var failure));
        Assert.Null(failure);
    }

    [Fact]
    public void TryProbeReadable_FileLockedAgainstReaders_ReportsTheOsMessage()
    {
        var path = _root.File("locked.jpg", 1);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(_fs.TryProbeReadable(path, out var failure));
        Assert.False(string.IsNullOrEmpty(failure));
    }

    [Fact]
    public void EnumerateReadableFilesWithStat_OneFileLocked_IsSkippedAndReportedWhileTheOthersAreListed()
    {
        var good = _root.File("good.jpg", 1, 2);
        var locked = _root.File("locked.jpg", 1);
        using var exclusive = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var skipped = new List<SkippedEntry>();

        var listed = _fs.EnumerateReadableFilesWithStat(_root.Path, _ => true, skipped.Add).ToList();

        var entry = Assert.Single(listed);
        Assert.Equal(good, entry.Path);
        Assert.Equal(2, entry.Stat!.Length);
        var skip = Assert.Single(skipped);
        Assert.Equal(locked, skip.Path);
        Assert.Equal(SkippedKind.FileUnreadable, skip.Kind);
        Assert.False(string.IsNullOrEmpty(skip.Reason));
    }

    [Fact]
    public void EnumerateFilesWithStat_FolderDoesNotExist_Propagates()
    {
        var skipped = new List<SkippedEntry>();

        Assert.Throws<DirectoryNotFoundException>(() => _fs.EnumerateFilesWithStat(_root.Combine("nope"), _ => true, skipped.Add).ToList());
        Assert.Empty(skipped); // an unreadable folder must fail the load loudly, not look like an empty catalog
    }

    // --- ResolveRealPath (SEC-01) -------------------------------------------------------------------------------------

    [Fact]
    public void ResolveRealPath_PlainPath_IsReturnedUnchanged()
    {
        var folder = _root.Dir("plain");
        var file = _root.File(Path.Combine("plain", "a.jpg"), 1);

        Assert.Equal(file, _fs.ResolveRealPath(file), ignoreCase: true);
        Assert.Equal(Path.Combine(folder, "future", "x.jpg"), _fs.ResolveRealPath(Path.Combine(folder, "future", "x.jpg")), ignoreCase: true);
    }
}