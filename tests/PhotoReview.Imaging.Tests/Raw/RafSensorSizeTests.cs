using System.Buffers.Binary;
using System.IO;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Raw.Raf;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// The RAF CFA header (offset 92/96) declares the raw size: record 0x0100 = full readout (height, width) and 0x0111 =
/// cropped size. Without it the size fell back to the embedded JPEG (1920x1280 for an X-T2), breaking the status bar,
/// zoom gating and RAM estimates.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class RafSensorSizeTests
{
    private static readonly string CorpusDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

    private static RawContainerInfo Read(byte[] data) =>
        new RafContainerReader().Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    /// <summary>A synthetic RAF whose CFA header (appended after the JPEG) holds the given (tag, height, width) records.</summary>
    private static byte[] BuildRaf(params (ushort Tag, ushort Height, ushort Width)[] records)
    {
        var raf = SyntheticRawBuilder.BuildRaf().ToList();
        uint cfaOffset = (uint)raf.Count;
        var cfa = new List<byte>();
        AddU32(cfa, (uint)records.Length);
        foreach (var (tag, height, width) in records)
        {
            AddU16(cfa, tag);
            AddU16(cfa, 4);
            AddU16(cfa, height);
            AddU16(cfa, width);
        }

        raf.AddRange(cfa);
        var bytes = raf.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(92), cfaOffset);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(96), (uint)cfa.Count);
        return bytes;
    }

    private static void AddU16(List<byte> list, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        list.AddRange(b.ToArray());
    }

    private static void AddU32(List<byte> list, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        list.AddRange(b.ToArray());
    }

    [Fact]
    public void Read_CroppedAndFullRecords_PrefersCroppedSize()
    {
        var info = Read(BuildRaf((0x0100, 4032, 6160), (0x0111, 4000, 6000)));

        Assert.Equal((6000, 4000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_OnlyFullRecord_UsesFullSize()
    {
        var info = Read(BuildRaf((0x0100, 3296, 4992)));

        Assert.Equal((4992, 3296), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_CroppedLargerThanFull_IsIgnored()
    {
        var info = Read(BuildRaf((0x0100, 3296, 4992), (0x0111, 9000, 9000)));

        Assert.Equal((4992, 3296), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_NoCfaHeader_KeepsSizeUnknown()
    {
        var info = Read(SyntheticRawBuilder.BuildRaf());

        Assert.Equal((0, 0), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_CfaRangePastEndOfFile_IsIgnoredWithoutThrowing()
    {
        var bytes = SyntheticRawBuilder.BuildRaf();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(92), uint.MaxValue - 8);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(96), 64);

        var info = Read(bytes);

        Assert.Equal((0, 0), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_HugeRecordCount_StopsAtTheEndOfTheHeader()
    {
        var bytes = BuildRaf((0x0100, 3296, 4992));
        int cfaOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(92));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(cfaOffset), uint.MaxValue);

        var info = Read(bytes);

        Assert.Equal((4992, 3296), (info.SensorWidth, info.SensorHeight));
    }

    [Theory]
    // Cropped record (height x width) per camera; LibRaw reports 6032x4032 / 4934x3296 / 6246x4170 (own margins).
    [InlineData("X-T2", 6000, 4000)]
    [InlineData("X-E2S", 4896, 3264)]
    [InlineData("X100V", 6240, 4160)]
    public void Corpus_RafReportsRawSizeNotEmbeddedJpegSize(string camera, int width, int height)
    {
        var file = RawCorpus.TryGetFirst("*.RAF", camera);
        if (file is null) return;

        using var fs = File.OpenRead(file);
        using var source = new SourceRawHeaderSource(fs);
        var info = new RafContainerReader().Read(source, CancellationToken.None);

        Assert.Equal((width, height), (info.SensorWidth, info.SensorHeight));
    }
}
