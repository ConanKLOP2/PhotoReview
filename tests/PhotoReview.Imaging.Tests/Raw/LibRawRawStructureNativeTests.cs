using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// The pinned libraw_iparams_t offsets (colors/filters/cdesc) must read the real values LibRaw 0.22.2 parsed from the file: a wrong
/// layout would make the memory guard classify by garbage. Corpus-backed, so Native.
/// </summary>
[Collection(LibRawNativeDecodeGate.Name)]
[Trait("Category", "Native")]
public sealed class LibRawRawStructureNativeTests
{
    [Theory]
    [InlineData("Canon - EOS 350D - RAW (3_2).CR2")]
    [InlineData("Pentax - K-7 - 12bit (3_2).DNG")]
    [InlineData("Apple - iPhone 6s Plus - 16bit (4_3).DNG")]
    [InlineData("Fujifilm - X-T2 - 14bit 14bit uncompressed (3_2).RAF")] // X-Trans: filters = 9, still a mosaic
    public void TryGetRawStructure_MosaicCorpusFile_IsBayerWithThreeOrFourColors(string fileName)
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason) ||
            RawCorpus.TryGetFile(fileName) is not { } path) return;
        var pointer = LibRawNativeMethods.LibRawInit(0);
        Assert.NotEqual(IntPtr.Zero, pointer);
        using var handle = new SafeLibRawHandle(pointer);
        Assert.Equal(0, LibRawNativeMethods.LibRawOpenWFile(handle, path));

        var structure = LibRawNativeMethods.TryGetRawStructure(handle);

        Assert.NotNull(structure);
        Assert.True(structure!.Value.IsBayer, $"{fileName}: colors {structure.Value.Colors}, filters 0x{structure.Value.Filters:X}");
        Assert.InRange(structure.Value.Colors, 3, 4);
        Assert.Equal(DecodeMemoryGuard.RawBufferFamily.Bayer,
            DecodeMemoryGuard.Classify("deflate_dng_load_raw()", structure)); // the real mosaic structure keeps a Bayer DNG at 2 B/px
    }
}
