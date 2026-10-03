using System.IO;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>Mutation-testing gap tests for <see cref="SourceRawHeaderSource"/> (Stryker round 2): argument validation and what Dispose releases.</summary>
public sealed class SourceRawHeaderSourceMutationGapTests
{
    private static SourceRawHeaderSource NewSource(int length = 1000) => new(new MemoryStream(new byte[length]));

    [Theory]
    [InlineData(-1L, 1)]
    [InlineData(0L, -1)]
    public void Read_NegativeOffsetOrCount_IsRejectedByTheArgumentCheckItself(long offset, int count)
    {
        using var source = NewSource();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => source.Read(offset, count));

        Assert.Equal("offset", ex.ParamName); // not an incidental failure further down (span construction)
    }

    [Theory]
    [InlineData(10L)]
    [InlineData(1000L)] // exactly the end of the stream
    public void Read_ZeroCountInsideTheStream_ReturnsAnEmptySpan(long offset)
    {
        using var source = NewSource();

        Assert.Equal(0, source.Read(offset, 0).Length);
    }

    [Fact]
    public void Read_AfterDispose_DoesNotServeBlocksThatWereCachedBefore()
    {
        var source = NewSource();
        Assert.Equal(16, source.Read(0, 16).Length); // block 0 is now cached
        source.Dispose();

        // The cache is released with the stream: a read is a fresh stream read, which fails like any use after dispose.
        Assert.Throws<InvalidDataException>(() => source.Read(0, 16));
    }
}
