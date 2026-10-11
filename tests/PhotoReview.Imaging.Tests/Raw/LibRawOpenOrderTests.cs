using PhotoReview.Core.Model;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// The LibRaw parameters (output colour, bit depth, use_camera_wb) must be in place before libraw_open: identify() adopts
/// the embedded camera matrix only when use_camera_wb is already set at that point (non-DNG makers such as Olympus ORF).
/// </summary>
[Collection(LibRawNativeDecodeGate.Name)]
[Trait("Category", "Native")]
public sealed class LibRawOpenOrderTests
{
    [Fact]
    public void Decode_MissingFile_ConfiguresOutputBeforeAttemptingToOpen()
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason)) return;
        var stages = new List<string>();
        var decoder = new LibRawDecoder(WpfBitmapSourceCodec.Instance, stages.Add);

        Assert.ThrowsAny<Exception>(() => decoder.Decode(new DecodeRequest(@"Z:\definitely-missing\file.orf", DecodeBox.Unbounded)));

        // "opened" is never reported (open failed), and the settings were applied before that attempt.
        Assert.Equal(["configured"], stages);
    }

    [Fact]
    public void Decode_CorpusFile_ConfiguresOutputBeforeOpenAndNotAfter()
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason) ||
            RawCorpus.TryGetFile("Canon - EOS 350D - RAW (3_2).CR2") is not { } path) return;
        var stages = new List<string>();

        _ = new LibRawDecoder(WpfBitmapSourceCodec.Instance, stages.Add).Decode(new DecodeRequest(path, new DecodeBox(160, 120)));

        Assert.Equal(["configured", "opened", "unpacked", "processed", "handle-closed", "source-released", "bitmap-created"], stages);
    }

    [Fact]
    public void Decode_NotEnoughHeadroom_IsRefusedBeforeUnpackAsInvalidOperation()
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason) ||
            RawCorpus.TryGetFile("Canon - EOS 350D - RAW (3_2).CR2") is not { } path) return;
        var stages = new List<string>();
        var decoder = new LibRawDecoder(WpfBitmapSourceCodec.Instance, stages.Add) { MemoryInfo = () => (TotalAvailable: 1_000_000, Load: 0) };

        var error = Assert.Throws<InvalidOperationException>(() => decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded)));

        Assert.Contains("memory", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["configured", "opened"], stages); // refused before unpack/process: nothing big was allocated
    }

    [Fact]
    public void Decode_HeadroomCoversOnlyTheWorkingImageAndBitmap_IsRefusedBecauseTheUnpackedRawBufferCounts()
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason) ||
            RawCorpus.TryGetFile("Canon - EOS 350D - RAW (3_2).CR2") is not { } path) return;
        var size = new LibRawDecoder(WpfBitmapSourceCodec.Instance).ReadInfo(path);
        // Exactly the old estimate (8 + 3 B/px + the full-size bitmap): it fits a total of that size only while the raw buffer is ignored.
        var withoutRawBuffer = (long)size.Width * size.Height * (8 + 3 + 4);
        var stages = new List<string>();
        var decoder = new LibRawDecoder(WpfBitmapSourceCodec.Instance, stages.Add) { MemoryInfo = () => (TotalAvailable: withoutRawBuffer, Load: 0) };

        var error = Assert.Throws<InvalidOperationException>(() => decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded)));

        Assert.Contains("memory", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["configured", "opened"], stages);
    }

    [Fact]
    public void Decode_HeadroomCoversTheRawBufferToo_Decodes()
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason) ||
            RawCorpus.TryGetFile("Canon - EOS 350D - RAW (3_2).CR2") is not { } path) return;
        var size = new LibRawDecoder(WpfBitmapSourceCodec.Instance).ReadInfo(path);
        // Generous: raw buffers are at most 2 B per sensor pixel for this Bayer file; the sensor is at most ~10% larger than the output.
        var total = (long)size.Width * size.Height * (8 + 3 + 4 + 3);
        var decoder = new LibRawDecoder(WpfBitmapSourceCodec.Instance) { MemoryInfo = () => (TotalAvailable: total, Load: 0) };

        var decoded = decoder.Decode(new DecodeRequest(path, new DecodeBox(240, 180)));

        Assert.Equal(DecoderBackend.LibRaw, decoded.ActualBackend);
    }
}
