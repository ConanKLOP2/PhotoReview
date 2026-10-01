using System.IO;
using PhotoReview.Imaging.Raw;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

public sealed class RawContractsAndHeaderSourceTests
{
    [Fact]
    public void SourceRawHeaderSource_ReadsOnlyRequestedBlocks_AndCaches()
    {
        var dummyData = new byte[256 * 1024];
        for (int i = 0; i < dummyData.Length; i++) dummyData[i] = (byte)(i % 251);

        using var ms = new MemoryStream(dummyData);
        using var source = new SourceRawHeaderSource(ms);

        Assert.Equal(dummyData.Length, source.Length);
        Assert.Equal(0, source.TotalBytesRead);

        // Read 10 bytes in block 0
        var span1 = source.Read(0, 10);
        Assert.Equal(10, span1.Length);
        Assert.Equal(SourceRawHeaderSource.BlockSize, source.TotalBytesRead);

        // Read 10 bytes within block 0 again (should be served from cache, no new read)
        var span2 = source.Read(100, 10);
        Assert.Equal(10, span2.Length);
        Assert.Equal(SourceRawHeaderSource.BlockSize, source.TotalBytesRead);

        // Read crossing boundary: block 0 to block 1
        var span3 = source.Read(SourceRawHeaderSource.BlockSize - 5, 20);
        Assert.Equal(20, span3.Length);
        Assert.Equal(SourceRawHeaderSource.BlockSize * 2, source.TotalBytesRead);
    }

    [Fact]
    public void SourceRawHeaderSource_ThrowsOnPastEof()
    {
        var dummy = new byte[100];
        using var ms = new MemoryStream(dummy);
        using var source = new SourceRawHeaderSource(ms);

        Assert.Throws<InvalidDataException>(() => source.Read(95, 10));
    }

    [Fact]
    public void InMemoryRawHeaderSource_ReadsExactSlices()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var source = new InMemoryRawHeaderSource(bytes);

        Assert.Equal(8, source.Length);
        var span = source.Read(2, 4);
        Assert.Equal(4, span.Length);
        Assert.Equal(3, span[0]);
        Assert.Equal(6, span[3]);
    }

    [Fact]
    public void RawFileTypes_ContainsExpectedExtensions()
    {
        Assert.True(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2"
        }.SetEquals(RawFileTypes.Extensions));
        Assert.True(RawFileTypes.IsRawExtension("test.CR3"));
        Assert.False(RawFileTypes.IsRawExtension("test.jpg"));
        Assert.False(RawFileTypes.IsRawExtension("test.nrw"));
        Assert.False(RawFileTypes.IsRawExtension("test.pef"));
    }

    [Fact]
    public void RawContainerReaderRegistry_DoesNotAcceptReservedFormats()
    {
        var registry = new RawContainerReaderRegistry();
        var tiffHeader = new byte[] { 0x49, 0x49, 0x2A, 0x00 };

        Assert.Null(registry.FindReader(tiffHeader, ".nrw"));
        Assert.Null(registry.FindReader(tiffHeader, ".pef"));
    }

    [Fact]
    public void InitialProbeLength_StaysBoundedForLargeSources()
    {
        Assert.Equal(64, RawContainerLimits.InitialProbeLength((long)int.MaxValue + 1));
        Assert.Equal(64, RawContainerLimits.InitialProbeLength(long.MaxValue));
        Assert.Equal(12, RawContainerLimits.InitialProbeLength(12));
        Assert.Equal(0, RawContainerLimits.InitialProbeLength(0));
    }

    [Fact]
    public void RawContainerLimits_HasSpecifiedConstants()
    {
        Assert.Equal(64, RawContainerLimits.MaxIfdCount);
        Assert.Equal(4096, RawContainerLimits.MaxEntriesPerIfd);
        Assert.Equal(128 << 20, RawContainerLimits.MaxPreviewBytes);
        Assert.Equal(8 << 20, RawContainerLimits.MaxHeaderBytes);
    }

    [Fact]
    public void RawContainerReaderRegistry_RoutesCorrectly()
    {
        var mockReader = new TestRawReader(RawFormat.Cr2, ".cr2");
        var registry = new RawContainerReaderRegistry([mockReader]);

        var found = registry.FindReader(new byte[64], ".cr2");
        Assert.Same(mockReader, found);

        var notFound = registry.FindReader(new byte[64], ".unknown");
        Assert.Null(notFound);
    }

    [Fact]
    public void SyntheticRawBuilder_RoundTripsMinimalContainers()
    {
        var tiffLe = SyntheticRawBuilder.BuildTiff(littleEndian: true);
        Assert.Equal(0x49, tiffLe[0]);
        Assert.Equal(0x49, tiffLe[1]);

        var tiffBe = SyntheticRawBuilder.BuildTiff(littleEndian: false);
        Assert.Equal(0x4D, tiffBe[0]);
        Assert.Equal(0x4D, tiffBe[1]);

        var bmff = SyntheticRawBuilder.BuildIsoBmff();
        Assert.Equal((byte)'f', bmff[4]);
        Assert.Equal((byte)'t', bmff[5]);
        Assert.Equal((byte)'y', bmff[6]);
        Assert.Equal((byte)'p', bmff[7]);

        var raf = SyntheticRawBuilder.BuildRaf();
        Assert.StartsWith("FUJIFILMCCD-RAW", System.Text.Encoding.ASCII.GetString(raf, 0, 15));
    }

    private sealed class TestRawReader(RawFormat format, string ext) : IRawContainerReader
    {
        public RawFormat Format => format;
        public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension) =>
            string.Equals(extension, ext, StringComparison.OrdinalIgnoreCase);

        public RawContainerInfo Read(IRawHeaderSource source, CancellationToken ct) =>
            new(format, 4000, 3000, 1, [], []);
    }
}
