using System.Collections;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;

namespace PhotoReview.Core.Tests.IO;

/// <summary>
/// ADR 0007 section 3: an error in the middle of a directory listing keeps what was read and is reported as
/// <see cref="SkippedKind.ListingInterrupted"/>; an error before the first entry propagates. The enumerator is injected, so no real
/// directory trick is needed.
/// </summary>
public sealed class PhysicalFileSystemListingInterruptedTests : IDisposable
{
    private readonly TempRoot _root = new("listing-interrupted");

    public void Dispose() => _root.Dispose();

    private sealed class ThrowingListing(IEnumerable<FileInfo> entries, Exception thenThrow) : IEnumerator<FileInfo>
    {
        private readonly Queue<FileInfo> _entries = new(entries);
        private FileInfo? _current;

        public bool Disposed { get; private set; }
        public FileInfo Current => _current!;
        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_entries.Count == 0) throw thenThrow;
            _current = _entries.Dequeue();
            return true;
        }

        public void Reset() => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }

    private FileInfo[] Files(params string[] names) => names.Select(n => new FileInfo(_root.File(n, 1, 2, 3))).ToArray();

    private static List<(string Path, FileStat? Stat)> List(ThrowingListing listing, string directory, Func<string, bool> include, List<SkippedEntry> skipped) =>
        PhysicalFileSystem.EnumerateFilesWithStat(directory, include, skipped.Add, _ => listing).ToList();

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void EnumerateFilesWithStat_ListingBreaksAfterTwoEntries_KeepsThemAndReportsListingInterrupted(Type exceptionType)
    {
        var files = Files("a.jpg", "b.jpg");
        var listing = new ThrowingListing(files, (Exception)Activator.CreateInstance(exceptionType, "disk went away")!);
        var skipped = new List<SkippedEntry>();

        var listed = List(listing, _root.Path, _ => true, skipped);

        Assert.Equal(files.Select(f => f.FullName), listed.Select(e => e.Path));
        Assert.All(listed, e => Assert.Equal(3, e.Stat!.Length));
        var skip = Assert.Single(skipped);
        Assert.Equal(SkippedKind.ListingInterrupted, skip.Kind);
        Assert.Equal(_root.Path, skip.Path);
        Assert.Equal("disk went away", skip.Reason);
        Assert.True(listing.Disposed);
    }

    [Fact]
    public void EnumerateFilesWithStat_ListingBreaksBeforeTheFirstEntry_PropagatesAndReportsNothing()
    {
        var listing = new ThrowingListing([], new IOException("folder unreadable"));
        var skipped = new List<SkippedEntry>();

        var ex = Assert.Throws<IOException>(() => List(listing, _root.Path, _ => true, skipped));

        Assert.Equal("folder unreadable", ex.Message);
        Assert.Empty(skipped); // an unreadable folder must fail the load loudly, not look like an empty catalog
        Assert.True(listing.Disposed);
    }

    [Fact]
    public void EnumerateFilesWithStat_NonIoFailureMidListing_Propagates()
    {
        var listing = new ThrowingListing(Files("a.jpg"), new InvalidOperationException("not an I/O error"));
        var skipped = new List<SkippedEntry>();

        Assert.Throws<InvalidOperationException>(() => List(listing, _root.Path, _ => true, skipped));

        Assert.Empty(skipped);
    }

    [Fact]
    public void EnumerateFilesWithStat_IncludeFilterRejectsSome_ListsOnlyTheIncludedBeforeTheInterruption()
    {
        var files = Files("keep.jpg", "drop.txt", "keep2.jpg");
        var listing = new ThrowingListing(files, new IOException("cut"));
        var skipped = new List<SkippedEntry>();

        var listed = List(listing, _root.Path, p => p.EndsWith(".jpg", StringComparison.Ordinal), skipped);

        Assert.Equal([files[0].FullName, files[2].FullName], listed.Select(e => e.Path));
        Assert.Equal(SkippedKind.ListingInterrupted, Assert.Single(skipped).Kind);
    }
}