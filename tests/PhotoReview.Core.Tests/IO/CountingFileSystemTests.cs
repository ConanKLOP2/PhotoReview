using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.IO;

public sealed class CountingFileSystemTests
{
    [Fact(DisplayName = "Metadata queries are counted and results are forwarded")]
    public void StatQueriesAreCounted()
    {
        var inner = new InMemoryFileSystem();
        inner.WriteAllTextAtomic(@"C:\photos\a.jpg", "12345");
        var metrics = new ReviewMetrics();
        var fs = new CountingFileSystem(inner, metrics);

        Assert.True(fs.FileExists(@"C:\photos\a.jpg"));
        Assert.False(fs.DirectoryExists(@"C:\nope"));
        Assert.Equal(5, fs.GetFileStat(@"C:\photos\a.jpg")!.Length);
        Assert.Null(fs.GetFileStat(@"C:\photos\missing.jpg"));

        Assert.Equal(4, metrics.Snapshot().StatCount);
    }

    [Fact(DisplayName = "Non-metadata operations are forwarded without counting")]
    public void OtherOperationsAreNotCounted()
    {
        var inner = new InMemoryFileSystem();
        var metrics = new ReviewMetrics();
        var fs = new CountingFileSystem(inner, metrics);

        fs.WriteAllTextAtomic(@"C:\photos\b.txt", "hello");
        var text = fs.ReadAllText(@"C:\photos\b.txt");

        Assert.Equal("hello", text);
        Assert.Equal(0, metrics.Snapshot().StatCount);
    }
}
