using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.IO;

[Trait("Category", "HotPath")]
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

    [Fact(DisplayName = "Every forwarded operation reaches the inner file system with the same arguments and none is counted")]
    public void ForwardedOperations_ReachTheInnerFileSystem_WithoutCounting()
    {
        var inner = new InMemoryFileSystem();
        var metrics = new ReviewMetrics();
        var fs = new CountingFileSystem(inner, metrics);
        var skipped = new List<SkippedEntry>();

        fs.CreateDirectory(@"C:\photos");
        fs.WriteAllTextAtomic(@"C:\photos\a.jpg", "12345");
        fs.Copy(@"C:\photos\a.jpg", @"C:\photos\b.jpg");
        Assert.True(fs.TryCopyNew(@"C:\photos\a.jpg", @"C:\photos\c.jpg"));
        Assert.False(fs.TryCopyNew(@"C:\photos\a.jpg", @"C:\photos\c.jpg")); // c.jpg exists now: the inner "create new" refuses
        fs.Move(@"C:\photos\b.jpg", @"C:\photos\d.jpg");
        fs.Delete(@"C:\photos\c.jpg");
        using (var append = fs.OpenAppend(@"C:\photos\log.txt", durable: false)) append.Write("hi"u8);

        Assert.Equal([@"C:\photos\a.jpg", @"C:\photos\d.jpg", @"C:\photos\log.txt"],
            fs.EnumerateFiles(@"C:\photos").Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Equal([@"C:\photos\a.jpg"], fs.EnumerateFiles(@"C:\photos", "a.jpg").ToArray());
        Assert.Equal("hi", fs.ReadAllText(@"C:\photos\log.txt"));
        using (var read = fs.OpenReadShared(@"C:\photos\a.jpg", 16))
        using (var reader = new StreamReader(read)) Assert.Equal("12345", reader.ReadToEnd());
        Assert.True(fs.TryProbeReadable(@"C:\photos\a.jpg", out var failure));
        Assert.Null(failure);
        Assert.False(fs.TryProbeReadable(@"C:\photos\gone.jpg", out failure));
        Assert.NotNull(failure);
        Assert.Equal([@"C:\photos\a.jpg"], fs.EnumerateFilesWithStat(@"C:\photos", p => p.EndsWith("a.jpg", StringComparison.Ordinal), skipped.Add)
            .Select(f => f.Path).ToArray());
        Assert.Equal([@"C:\photos\a.jpg"], fs.EnumerateReadableFilesWithStat(@"C:\photos", p => p.EndsWith("a.jpg", StringComparison.Ordinal), skipped.Add)
            .Select(f => f.Path).ToArray());
        Assert.Empty(skipped);
        Assert.Equal(@"C:\photos\a.jpg", fs.ResolveRealPath(@"C:\photos\a.jpg"), ignoreCase: true);
        Assert.Equal(0, metrics.Snapshot().StatCount);
    }

    [Fact(DisplayName = "EnumerateDirectories is forwarded to the inner file system")]
    public void EnumerateDirectories_IsForwarded()
    {
        var inner = new InMemoryFileSystem();
        inner.CreateDirectory(@"C:\photos\sub");
        var fs = new CountingFileSystem(inner, new ReviewMetrics());

        Assert.Equal(inner.EnumerateDirectories(@"C:\photos").ToArray(), fs.EnumerateDirectories(@"C:\photos").ToArray());
        Assert.NotEmpty(fs.EnumerateDirectories(@"C:\photos"));
    }

    [Fact(DisplayName = "The TryCopyNew-with-proof overload is forwarded to the inner overload (the proof is raised by the inner copy)")]
    public void TryCopyNewWithProof_IsForwardedToTheInnerOverload()
    {
        var inner = new InMemoryFileSystem();
        inner.WriteAllTextAtomic(@"C:\photos\a.jpg", "12345");
        var fs = new CountingFileSystem(inner, new ReviewMetrics());
        var proof = new CopyCreationProof();

        var created = fs.TryCopyNew(@"C:\photos\a.jpg", @"C:\photos\copy.jpg", proof);

        Assert.True(created);
        Assert.Equal("12345", inner.ReadAllText(@"C:\photos\copy.jpg"));
        Assert.True(proof.DestinationCreated);
    }
}
