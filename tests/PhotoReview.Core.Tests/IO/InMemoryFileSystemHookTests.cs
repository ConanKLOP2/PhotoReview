using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.IO;

[Trait("Category", "HotPath")]
public class InMemoryFileSystemHookTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly string _testFile = Path.Combine("C:", "Testing", "sample.txt");

    public InMemoryFileSystemHookTests()
    {
        _fs.CreateDirectory(Path.Combine("C:", "Testing"));
    }

    [Fact]
    public void OpenReadHookInterceptsReadOperations()
    {
        _fs.WriteAllTextAtomic(_testFile, "data");

        _fs.OpenReadHook = path => new IOException("Simulated disk read error");

        var ex = Assert.Throws<IOException>(() => _fs.OpenReadShared(_testFile));
        Assert.Equal("Simulated disk read error", ex.Message);
    }

    [Fact]
    public void WriteHookInterceptsWriteAllTextAtomic()
    {
        _fs.WriteHook = path => new UnauthorizedAccessException("Simulated permission denied");

        var ex = Assert.Throws<UnauthorizedAccessException>(() => _fs.WriteAllTextAtomic(_testFile, "content"));
        Assert.Equal("Simulated permission denied", ex.Message);
    }

    [Fact]
    public void DeleteHookInterceptsDeleteOperation()
    {
        _fs.WriteAllTextAtomic(_testFile, "data");
        _fs.DeleteHook = path => new IOException("Simulated file in use");

        var ex = Assert.Throws<IOException>(() => _fs.Delete(_testFile));
        Assert.Equal("Simulated file in use", ex.Message);
    }

    [Fact]
    public void MoveHookInterceptsMoveOperation()
    {
        var dst = Path.Combine("C:", "Testing", "dest.txt");
        _fs.WriteAllTextAtomic(_testFile, "data");
        _fs.MoveHook = (src, d) => new IOException("Simulated cross-device link");

        var ex = Assert.Throws<IOException>(() => _fs.Move(_testFile, dst));
        Assert.Equal("Simulated cross-device link", ex.Message);
    }

    [Fact]
    public void CopyHookInterceptsCopyOperation()
    {
        var dst = Path.Combine("C:", "Testing", "dest.txt");
        _fs.WriteAllTextAtomic(_testFile, "data");
        _fs.CopyHook = (src, d) => new IOException("Simulated out of disk space");

        var ex = Assert.Throws<IOException>(() => _fs.Copy(_testFile, dst));
        Assert.Equal("Simulated out of disk space", ex.Message);
    }
    [Fact]
    public void StampOnMoveRewritesTheDestinationWriteTime()
    {
        var dst = Path.Combine("C:", "Testing", "dest.txt");
        var stamp = new DateTime(2026, 9, 19, 9, 0, 1, 735, DateTimeKind.Utc);
        _fs.AddFile(_testFile, "data", stamp);
        _fs.StampOnMove = (destination, original) => destination == dst ? original.AddSeconds(-1) : original;

        _fs.Move(_testFile, dst);

        Assert.Equal(stamp.AddSeconds(-1), _fs.GetFileStat(dst)!.LastWriteUtc);
    }

    [Fact]
    public void CopyFailsAfterBytesLeavesAPartialDestinationAndThrows()
    {
        var dst = Path.Combine("C:", "Testing", "dest.txt");
        _fs.WriteAllTextAtomic(_testFile, "0123456789");
        _fs.CopyFailsAfterBytes = 3;

        Assert.Throws<IOException>(() => _fs.Copy(_testFile, dst));
        Assert.Throws<IOException>(() => _fs.TryCopyNew(_testFile, dst + ".2"));

        Assert.Equal("012", _fs.ReadAllText(dst));
        Assert.Equal("012", _fs.ReadAllText(dst + ".2"));
    }
}

