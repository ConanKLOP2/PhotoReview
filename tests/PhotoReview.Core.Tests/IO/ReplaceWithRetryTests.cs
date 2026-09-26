using System.IO;
using PhotoReview.Core.IO;

namespace PhotoReview.Core.Tests.IO;

public sealed class ReplaceWithRetryTests
{
    private static IOException Sharing() => new("in use", unchecked((int)0x80070020));

    [Fact(DisplayName = "A sharing violation is retried with growing backoff until the move succeeds")]
    public void SharingViolation_RetriedThenSucceeds()
    {
        var calls = 0;
        var sleeps = new List<int>();

        PhysicalFileSystem.ReplaceWithRetry(() => { if (++calls < 3) throw Sharing(); }, sleeps.Add);

        Assert.Equal(3, calls);
        Assert.Equal([3, 6], sleeps);
    }

    [Fact(DisplayName = "Access denied is retried, then rethrown after the attempt budget")]
    public void AccessDenied_RetriedThenRethrown()
    {
        var calls = 0;
        var sleeps = 0;

        Assert.Throws<UnauthorizedAccessException>(() =>
            PhysicalFileSystem.ReplaceWithRetry(() => { calls++; throw new UnauthorizedAccessException(); }, _ => sleeps++));

        Assert.Equal(8, calls);
        Assert.Equal(7, sleeps);
    }

    [Theory(DisplayName = "A non-transient failure (missing directory, path too long, other IO error) is not retried and does not sleep")]
    [InlineData(typeof(DirectoryNotFoundException))]
    [InlineData(typeof(PathTooLongException))]
    [InlineData(typeof(IOException))]
    public void NonTransient_SurfacesImmediately(Type exceptionType)
    {
        var calls = 0;
        var sleeps = 0;

        Assert.Throws(exceptionType, () =>
            PhysicalFileSystem.ReplaceWithRetry(() => { calls++; throw (Exception)Activator.CreateInstance(exceptionType)!; }, _ => sleeps++));

        Assert.Equal(1, calls);
        Assert.Equal(0, sleeps);
    }
}
