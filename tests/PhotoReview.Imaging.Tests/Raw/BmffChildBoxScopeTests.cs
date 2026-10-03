using System.Text;
using PhotoReview.Imaging.Raw.Bmff;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// A size-0 box inside a parent extends to the end of that parent (not the file), and the CR3 CMT1 IFD is bounded by its own
/// payload: out-of-line values are payload-relative and ignored when they fall outside it.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class BmffChildBoxScopeTests
{
    private static readonly byte[] CanonUuid =
        [0x85, 0xC0, 0xB6, 0x87, 0x82, 0x0F, 0x11, 0xE0, 0x81, 0x11, 0xF4, 0xCE, 0x46, 0x2B, 0x6A, 0x48];

    private static byte[] U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static byte[] Box(string type, params byte[][] parts)
    {
        byte[] payload = [.. parts.SelectMany(x => x)];
        return [.. U32((uint)(8 + payload.Length)), .. Encoding.ASCII.GetBytes(type), .. payload];
    }

    [Fact]
    public void ReadChildBoxes_SizeZeroChild_ExtendsToTheEndOfItsParentNotTheFile()
    {
        byte[] first = Box("free", new byte[8]);
        byte[] sizeZero = [.. U32(0), .. Encoding.ASCII.GetBytes("last"), .. new byte[20]];
        byte[] parent = Box("moov", first, sizeZero);
        byte[] trailer = Box("free", new byte[100]); // more file after the parent: EOF is NOT the parent end
        var source = new InMemoryRawHeaderSource([.. parent, .. trailer]);

        Assert.True(BmffBoxNavigator.TryReadBox(source, 0, out var moov));
        var children = BmffBoxNavigator.ReadChildBoxes(source, moov);

        Assert.Equal(2, children.Count);
        Assert.Equal("last", children[1].Type);
        Assert.Equal(parent.Length, children[1].Offset + children[1].TotalSize);
    }

    [Fact]
    public void TryReadBox_TopLevelSizeZero_StillExtendsToEndOfFile()
    {
        byte[] data = [.. U32(0), .. Encoding.ASCII.GetBytes("mdat"), .. new byte[50]];

        Assert.True(BmffBoxNavigator.TryReadBox(new InMemoryRawHeaderSource(data), 0, out var box));

        Assert.Equal(data.Length, box.TotalSize);
    }

    /// <summary>
    /// CR3 = ftyp + moov[uuid[CMT1]] + free. CMT1 (little-endian TIFF, 64-byte payload) has Width as LONG x2 (out-of-line) with
    /// the given value-offset, and inline Orientation 6.
    /// </summary>
    private static byte[] BuildCr3(uint widthValueOffset, Func<int, byte[]> trailerForFreeOffset, bool widthInsidePayload)
    {
        byte[] Le32(uint v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
        byte[] Le16(int v) => [(byte)v, (byte)(v >> 8)];
        byte[] Entry(int tag, int type, uint count, uint value) => [.. Le16(tag), .. Le16(type), .. Le32(count), .. Le32(value)];

        var cmt1 = new byte[64];
        byte[] head = [0x49, 0x49, 0x2A, 0x00, .. Le32(8), .. Le16(2),
            .. Entry(0x0100, 4, 2, widthValueOffset), .. Entry(0x0112, 3, 1, 6), .. Le32(0)];
        head.CopyTo(cmt1, 0);
        if (widthInsidePayload) Le32(6000).CopyTo(cmt1, (int)widthValueOffset);

        byte[] ftyp = Box("ftyp", Encoding.ASCII.GetBytes("crx "), U32(1), Encoding.ASCII.GetBytes("crx "));
        byte[] moov = Box("moov", Box("uuid", CanonUuid, Box("CMT1", cmt1)));
        int freePayloadOffset = ftyp.Length + moov.Length + 8;
        return [.. ftyp, .. moov, .. Box("free", trailerForFreeOffset(freePayloadOffset))];
    }

    private static RawContainerInfo Read(byte[] data) =>
        new Cr3ContainerReader().Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    [Fact]
    public void Cmt1_OutOfLineWidthInsideThePayload_IsReadRelativeToThePayload()
    {
        var info = Read(BuildCr3(widthValueOffset: 40, _ => new byte[64], widthInsidePayload: true));

        Assert.Equal(6000, info.SensorWidth);
        Assert.Equal(6, info.Orientation);
    }

    [Fact]
    public void Cmt1_OutOfLineWidthPointingOutsideThePayload_IsIgnoredEvenIfThatFileOffsetHoldsAWidth()
    {
        // The value offset equals the absolute file offset of a planted 6000: the old code read it straight from the file.
        // Computed in two passes (the moov size does not depend on the offset value).
        byte[] probe = BuildCr3(0, _ => new byte[64], widthInsidePayload: false);
        int ftypAndMoov = probe.Length - (8 + 64);
        uint absoluteOfPlanted = (uint)(ftypAndMoov + 8);
        byte[] data = BuildCr3(absoluteOfPlanted, _ =>
        {
            var free = new byte[64];
            BitConverter.GetBytes(6000u).CopyTo(free, 0);
            return free;
        }, widthInsidePayload: false);

        var info = Read(data);

        Assert.Equal(0, info.SensorWidth);
        Assert.Equal(6, info.Orientation); // inline values are unaffected
    }
}
