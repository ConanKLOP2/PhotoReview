using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Tests.Fakes;
using Xunit;

namespace PhotoReview.Core.Tests.FileActions;

[Trait("Category", "HotPath")]
public sealed class DuplicateFinderTests
{
    private readonly InMemoryFileSystem _fs = new();

    [Fact]
    public async Task FindAsync_WhenFilesEmpty_ReturnsEmpty()
    {
        var result = await DuplicateFinder.FindAsync(
            files: [],
            removeNumbered: true,
            hash: (path, ct) => Task.FromResult("dummy"),
            fileSystem: _fs);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FindAsync_RemoveNumbered_SelectsOnlyNumberedCopies()
    {
        var f1 = @"C:\photos\img.jpg";
        var f2 = @"C:\photos\img (1).jpg";
        var f3 = @"C:\photos\img (2).jpg";

        _fs.WriteAllTextAtomic(f1, "identical-content");
        _fs.WriteAllTextAtomic(f2, "identical-content");
        _fs.WriteAllTextAtomic(f3, "identical-content");

        var candidates = new[] { f1, f2, f3 };
        var result = await DuplicateFinder.FindAsync(
            candidates,
            removeNumbered: true,
            hash: (p, ct) => Task.FromResult("hash-abc"),
            fileSystem: _fs);

        Assert.Equal(2, result.Count);
        Assert.Contains(f2, result);
        Assert.Contains(f3, result);
        Assert.DoesNotContain(f1, result);
    }

    [Fact]
    public async Task FindAsync_RemoveOriginal_SelectsOnlyNonNumberedOriginals()
    {
        var f1 = @"C:\photos\img.jpg";
        var f2 = @"C:\photos\img (1).jpg";

        _fs.WriteAllTextAtomic(f1, "identical-content");
        _fs.WriteAllTextAtomic(f2, "identical-content");

        var candidates = new[] { f1, f2 };
        var result = await DuplicateFinder.FindAsync(
            candidates,
            removeNumbered: false,
            hash: (p, ct) => Task.FromResult("hash-abc"),
            fileSystem: _fs);

        Assert.Single(result);
        Assert.Equal(f1, result[0]);
    }

    [Fact]
    public async Task FindAsync_RemoveNumbered_WhenGroupContainsOnlyNumberedCopies_KeepsOneSurvivor()
    {
        var f1 = @"C:\photos\img (1).jpg";
        var f2 = @"C:\photos\img (2).jpg";
        var f3 = @"C:\photos\img (3).jpg";

        _fs.WriteAllTextAtomic(f1, "identical-content");
        _fs.WriteAllTextAtomic(f2, "identical-content");
        _fs.WriteAllTextAtomic(f3, "identical-content");

        var result = await DuplicateFinder.FindAsync(
            new[] { f1, f2, f3 },
            removeNumbered: true,
            hash: (p, ct) => Task.FromResult("hash-abc"),
            fileSystem: _fs);

        Assert.Equal(new[] { f2, f3 }, result);
    }

    [Fact]
    public async Task FindAsync_RemoveOriginal_WhenGroupContainsOnlyOriginals_KeepsOneSurvivor()
    {
        var f1 = @"C:\photos\img-a.jpg";
        var f2 = @"C:\photos\img-b.jpg";
        var f3 = @"C:\photos\img-c.jpg";

        _fs.WriteAllTextAtomic(f1, "identical-content");
        _fs.WriteAllTextAtomic(f2, "identical-content");
        _fs.WriteAllTextAtomic(f3, "identical-content");

        var result = await DuplicateFinder.FindAsync(
            new[] { f1, f2, f3 },
            removeNumbered: false,
            hash: (p, ct) => Task.FromResult("hash-abc"),
            fileSystem: _fs);

        Assert.Equal(new[] { f2, f3 }, result);
    }

    [Fact]
    public async Task FindAsync_DifferentSizes_SkipsHashComputation()
    {
        var f1 = @"C:\photos\small.jpg";
        var f2 = @"C:\photos\large.jpg";

        _fs.WriteAllTextAtomic(f1, "small");
        _fs.WriteAllTextAtomic(f2, "large large large");

        var hashCalled = false;
        var result = await DuplicateFinder.FindAsync(
            new[] { f1, f2 },
            removeNumbered: true,
            hash: (p, ct) =>
            {
                hashCalled = true;
                return Task.FromResult("hash");
            },
            fileSystem: _fs);

        Assert.False(hashCalled);
        Assert.Empty(result);
    }

    [Fact]
    public async Task FindAsync_SameSizeDifferentHash_NotTreatedAsDuplicate()
    {
        var f1 = @"C:\photos\a.jpg";
        var f2 = @"C:\photos\a (1).jpg";

        _fs.WriteAllTextAtomic(f1, "12345");
        _fs.WriteAllTextAtomic(f2, "abcde");

        var result = await DuplicateFinder.FindAsync(
            new[] { f1, f2 },
            removeNumbered: true,
            hash: (p, ct) => Task.FromResult(p.Contains("(1)") ? "hash-b" : "hash-a"),
            fileSystem: _fs);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FindAsync_FileErrorDuringHash_IgnoresAndContinues()
    {
        var f1 = @"C:\photos\a.jpg";
        var f2 = @"C:\photos\a (1).jpg";
        var f3 = @"C:\photos\a (2).jpg";

        _fs.WriteAllTextAtomic(f1, "content");
        _fs.WriteAllTextAtomic(f2, "content");
        _fs.WriteAllTextAtomic(f3, "content");

        var result = await DuplicateFinder.FindAsync(
            new[] { f1, f2, f3 },
            removeNumbered: true,
            hash: (p, ct) =>
            {
                if (p.EndsWith("a (1).jpg")) throw new IOException("File disappeared");
                return Task.FromResult("hash-same");
            },
            fileSystem: _fs);

        // f1 and f3 have the same hash; f2 threw IOException and was skipped
        Assert.Single(result);
        Assert.Equal(f3, result[0]);
    }

    [Fact]
    public async Task FindAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var f1 = @"C:\photos\a.jpg";
        var f2 = @"C:\photos\a (1).jpg";

        _fs.WriteAllTextAtomic(f1, "content");
        _fs.WriteAllTextAtomic(f2, "content");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await DuplicateFinder.FindAsync(
                new[] { f1, f2 },
                removeNumbered: true,
                hash: (p, ct) => Task.FromResult("hash"),
                cancellationToken: cts.Token,
                fileSystem: _fs);
        });
    }
}

