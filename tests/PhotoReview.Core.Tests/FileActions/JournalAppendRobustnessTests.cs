using System.IO;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>Review r7: journal append survives a failed first append (tail check) and a competing writer (retry).</summary>
public sealed class JournalAppendRobustnessTests : IDisposable
{
    private readonly TempRoot _root = new("journal-append");

    public void Dispose() => _root.Dispose();

    private static JournalEntry Entry(string id) => new(
        id, FileOperationType.Move, JournalState.Prepared, $@"C:\photos\{id}.jpg", $@"C:\photos\sel\{id}.jpg", 1,
        new DateTime(2026, 9, 25, 1, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 25, 1, 0, 0, DateTimeKind.Utc));

    [Fact(DisplayName = "A failed first append keeps the partial-tail repair for the next append")]
    public void FailedFirstAppend_NextAppendStillRepairsPartialTail()
    {
        var fs = new InMemoryFileSystem();
        var paths = new AppPaths(@"C:\Users\test\AppData\Local");
        // A crash left a partial last line without a newline.
        fs.AddFile(paths.JournalFile, "{\"Id\":\"torn\",\"Ty");
        var journal = new OperationJournal(paths, fs, new SystemClock());

        fs.OpenAppendHook = _ => new IOException("disk full");
        Assert.Throws<IOException>(() => journal.Append(Entry("first")));
        fs.OpenAppendHook = null;

        journal.Append(Entry("second"));

        var pending = Assert.Single(journal.ReadPendingOperations());
        Assert.Equal("second", pending.Id);
    }

    [Fact(DisplayName = "Append retries a sharing violation from another writer and succeeds once it is released")]
    public void Append_SharingViolation_RetriesUntilOtherWriterReleases()
    {
        var paths = new AppPaths(_root.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.JournalFile)!);
        // Another PhotoReview process mid-append: same open mode as PhysicalFileSystem.OpenAppend (FileShare.Read).
        var other = new FileStream(paths.JournalFile, FileMode.Append, FileAccess.Write, FileShare.Read);
        var delays = new List<TimeSpan>();
        var journal = new OperationJournal(paths, new PhysicalFileSystem(), new SystemClock(), appendRetryDelay: delay =>
        {
            delays.Add(delay);
            if (delays.Count == 2) other.Dispose(); // the other process finishes its write
        });

        try
        {
            journal.Append(Entry("a"));
        }
        finally
        {
            other.Dispose();
        }

        Assert.Equal(2, delays.Count);
        Assert.True(delays[1] > delays[0]);
        Assert.Equal("a", Assert.Single(journal.ReadPendingOperations()).Id);
    }

    [Fact(DisplayName = "Append gives up after a bounded number of sharing-violation retries")]
    public void Append_SharingViolationNeverReleased_ThrowsAfterBoundedAttempts()
    {
        var paths = new AppPaths(_root.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.JournalFile)!);
        using var other = new FileStream(paths.JournalFile, FileMode.Append, FileAccess.Write, FileShare.Read);
        var delays = 0;
        var journal = new OperationJournal(paths, new PhysicalFileSystem(), new SystemClock(), appendRetryDelay: _ => delays++);

        Assert.Throws<IOException>(() => journal.Append(Entry("a")));
        Assert.Equal(OperationJournal.AppendAttempts - 1, delays);
    }

    [Fact(DisplayName = "Append does not retry IO errors other than sharing/lock violations")]
    public void Append_OtherIoError_IsNotRetried()
    {
        var fs = new InMemoryFileSystem();
        var delays = 0;
        var journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), fs, new SystemClock(), appendRetryDelay: _ => delays++);
        fs.OpenAppendHook = _ => new IOException("disk full");

        Assert.Throws<IOException>(() => journal.Append(Entry("a")));
        Assert.Equal(0, delays);
    }
}
