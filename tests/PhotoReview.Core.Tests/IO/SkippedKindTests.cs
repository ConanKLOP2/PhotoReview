using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.TestSupport;

namespace PhotoReview.Core.Tests.IO;

public sealed class SkippedKindTests
{
    [Fact]
    public void LockedFile_IsReportedAsFileUnreadable_WithTheOsMessageKept()
    {
        using var temp = new TempRoot("skipped-kind");
        var locked = temp.File("locked.jpg", 1, 2, 3);
        var skipped = new List<SkippedEntry>();
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var readable = new PhysicalFileSystem()
            .EnumerateReadableFilesWithStat(temp.Path, _ => true, skipped.Add).ToList();

        Assert.Empty(readable);
        var entry = Assert.Single(skipped);
        Assert.Equal(locked, entry.Path);
        Assert.Equal(SkippedKind.FileUnreadable, entry.Kind);
        Assert.False(string.IsNullOrWhiteSpace(entry.Reason)); // OS message stays verbatim
    }

    [Fact]
    public void FastListing_DoesNotProbe_LockedFileIsListed_AndTheProbeReportsIt()
    {
        using var temp = new TempRoot("skipped-kind");
        var locked = temp.File("locked.jpg", 1, 2, 3);
        var skipped = new List<SkippedEntry>();
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var fs = new PhysicalFileSystem();

        var listed = fs.EnumerateFilesWithStat(temp.Path, _ => true, skipped.Add).ToList();

        Assert.Equal(locked, Assert.Single(listed).Path); // AR16: no per-file open on the listing path
        Assert.Empty(skipped);
        Assert.False(fs.TryProbeReadable(locked, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
