using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Default-category (no libraw.dll, no corpus) pins of the decode admission inside <see cref="LibRawDecoder"/>: the memory guard runs BEFORE
/// the lease is marked worked (a refused decode must not age queued preloads), the guard is fed the decoder name and the raw structure read
/// from the handle, and a cancelled decode releases the process-wide slot. The native handle is replaced by a fake probe.
/// </summary>
[Collection(LibRawNativeDecodeGate.Name)] // the full-decode slot is process-wide
public sealed class LibRawAdmissionSeamTests
{
    private const long OneAndAHalfGiB = 1_600_000_000L;
    private static readonly DecodeMemoryGuard.RawStructure Mosaic = new(3, 0x94949494u);
    private static readonly DecodeMemoryGuard.RawStructure NoMosaic = new(3, 0u);
    private static readonly DecodeRequest Request = new(@"C:\nowhere\x.dng", new DecodeBox(100, 100));

    private static LibRawDecoder DecoderWithMemory(long total, long load = 0) =>
        new() { MemoryInfo = () => (total, load) };

    private static FullDecodeGate.Lease NewLease() =>
        new FullDecodeGate(LibRawDecoder.MaxQueuedPreloadDecodes).Enter(SourceReadPriority.Viewer, CancellationToken.None);

    [Fact]
    public void AdmitDecode_RefusedByTheMemoryGuard_DoesNotMarkTheLeaseWorked()
    {
        using var lease = NewLease();
        var probe = new FakeProbe("unpacked_load_raw()", Mosaic);

        Assert.Throws<InvalidOperationException>(() => DecoderWithMemory(total: 1_000).AdmitDecode(probe, Request, lease));

        Assert.False(lease.IsWorked); // an instant refusal must not age queued preloads
    }

    [Fact]
    public void AdmitDecode_PassingTheMemoryGuard_MarksTheLeaseWorked()
    {
        using var lease = NewLease();

        DecoderWithMemory(total: OneAndAHalfGiB).AdmitDecode(new FakeProbe("unpacked_load_raw()", Mosaic), Request, lease);

        Assert.True(lease.IsWorked);
    }

    [Fact]
    public void AdmitDecode_UnknownSize_MarksTheLeaseWorkedWithoutEstimating()
    {
        using var lease = NewLease();

        DecoderWithMemory(total: 1).AdmitDecode(new FakeProbe("x", Mosaic) { Width = 0 }, Request, lease);

        Assert.True(lease.IsWorked);
    }

    [Theory]
    [InlineData("deflate_dng_load_raw()", true, true)]   // DNG + real mosaic structure: Bayer, 13 B/px fits the 1.6 GB budget
    [InlineData("deflate_dng_load_raw()", false, false)] // structure not delivered (null): conservative Linear, refused
    [InlineData("lossless_dng_load_raw()", true, true)]
    public void AdmitDecode_ClassifiesByTheHandlesDecoderNameAndStructure(string decoderName, bool withMosaicStructure, bool admitted)
    {
        using var lease = NewLease();
        var probe = new FakeProbe(decoderName, withMosaicStructure ? Mosaic : null);

        var decoder = DecoderWithMemory(OneAndAHalfGiB);
        if (admitted) decoder.AdmitDecode(probe, Request, lease);
        else Assert.Throws<InvalidOperationException>(() => decoder.AdmitDecode(probe, Request, lease));

        Assert.Equal(admitted, lease.IsWorked);
        Assert.True(probe.DecoderNameRead && probe.StructureRead);
    }

    [Fact]
    public void AdmitDecode_DngWithANonMosaicStructure_IsRefusedButAnUnrelatedDecoderIsNot()
    {
        using var dng = NewLease();
        Assert.Throws<InvalidOperationException>(() =>
            DecoderWithMemory(OneAndAHalfGiB).AdmitDecode(new FakeProbe("deflate_dng_load_raw()", NoMosaic), Request, dng));

        using var other = NewLease();
        // The same structure with a non-DNG decoder name stays Bayer: proves the decoder NAME (not just the structure) reaches Classify.
        DecoderWithMemory(OneAndAHalfGiB).AdmitDecode(new FakeProbe("canon_load_raw()", NoMosaic), Request, other);
        Assert.True(other.IsWorked);
    }

    [Fact]
    public void Decode_ThrowingOperationCanceledAfterTheGateWasEntered_ReleasesTheSlot()
    {
        var entered = false;
        var decoder = new LibRawDecoder
        {
            CoreOverride = (_, _, _) =>
            {
                entered = true;
                Assert.Equal(0, LibRawDecoder.FullDecodeSlotsAvailable); // really inside the gate
                throw new OperationCanceledException();
            },
        };

        Assert.Throws<OperationCanceledException>(() => decoder.Decode(Request, CancellationToken.None));

        Assert.True(entered);
        Assert.Equal(1, LibRawDecoder.FullDecodeSlotsAvailable);
    }

    private sealed class FakeProbe(string? decoderName, DecodeMemoryGuard.RawStructure? structure) : LibRawDecoder.ILibRawProbe
    {
        public int Width { get; init; } = 10_000;
        public int Height { get; init; } = 10_000;
        public int RawWidth => Width;
        public int RawHeight => Height;
        public bool DecoderNameRead { get; private set; }
        public bool StructureRead { get; private set; }
        public string? DecoderName { get { DecoderNameRead = true; return decoderName; } }
        public DecodeMemoryGuard.RawStructure? Structure { get { StructureRead = true; return structure; } }
    }
}
