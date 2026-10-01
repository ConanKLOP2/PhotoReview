using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
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
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public LibRawOpenOrderTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    public void Decode_MissingFile_ConfiguresOutputBeforeAttemptingToOpen()
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason)) return;
        var stages = new List<string>();
        var decoder = new LibRawDecoder(stages.Add);

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

        _ = new LibRawDecoder(stages.Add).Decode(new DecodeRequest(path, new DecodeBox(160, 120)));

        Assert.Equal(["configured", "opened", "unpacked", "processed", "handle-closed", "source-released", "bitmap-created"], stages);
    }

    [Fact]
    public void Decode_LegacyOrderSeam_ReportsOpenBeforeConfigure()
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason) ||
            RawCorpus.TryGetFile("Canon - EOS 350D - RAW (3_2).CR2") is not { } path) return;
        var stages = new List<string>();

        _ = new LibRawDecoder(stages.Add, configureAfterOpen: true).Decode(new DecodeRequest(path, new DecodeBox(160, 120)));

        Assert.Equal(["opened", "configured", "unpacked", "processed", "handle-closed", "source-released", "bitmap-created"], stages);
    }

    [Fact]
    public void Decode_OrfCameraMatrix_DiffersMeasurablyBetweenOpenTimeAndAfterOpenConfiguration()
    {
        // E-P3 is the smallest ORF of the corpus; measured open-time vs after-open R/G,B/G: E-P3 (0.953,1.042) vs (0.905,1.053),
        // E-M1 (0.920,0.957) vs (0.896,0.950), OM-1 (1.033,0.929) vs (1.029,0.908); CR2/NEF/ARW/RW2/Pentax DNG were byte-identical.
        const string fileName = "Olympus - E-P3 - 16bit (4_3).ORF";
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason) ||
            RawCorpus.TryGetFile(fileName) is not { } path) return;

        var before = Ratios(new LibRawDecoder(null).Decode(new DecodeRequest(path, new DecodeBox(240, 180))));
        var after = Ratios(new LibRawDecoder(null, configureAfterOpen: true).Decode(new DecodeRequest(path, new DecodeBox(240, 180))));
        _output.WriteLine($"{fileName}: open-time {before}, after-open {after}");

        Assert.True(Math.Abs(before.RG / after.RG - 1) > 0.01 || Math.Abs(before.BG / after.BG - 1) > 0.01,
            $"{fileName}: open-time {before} vs after-open {after}");
    }

    [Fact]
    public void Decode_NotEnoughHeadroom_IsRefusedBeforeUnpackAsInvalidOperation()
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason) ||
            RawCorpus.TryGetFile("Canon - EOS 350D - RAW (3_2).CR2") is not { } path) return;
        var stages = new List<string>();
        var decoder = new LibRawDecoder(stages.Add) { MemoryInfo = () => (TotalAvailable: 1_000_000, Load: 0) };

        var error = Assert.Throws<InvalidOperationException>(() => decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded)));

        Assert.Contains("memory", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["configured", "opened"], stages); // refused before unpack/process: nothing big was allocated
    }

    [Fact]
    public void Decode_HeadroomCoversOnlyTheWorkingImageAndBitmap_IsRefusedBecauseTheUnpackedRawBufferCounts()
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason) ||
            RawCorpus.TryGetFile("Canon - EOS 350D - RAW (3_2).CR2") is not { } path) return;
        var size = new LibRawDecoder(null).ReadInfo(path);
        // Exactly the old estimate (8 + 3 B/px + the full-size bitmap): it fits a total of that size only while the raw buffer is ignored.
        var withoutRawBuffer = (long)size.Width * size.Height * (8 + 3 + 4);
        var stages = new List<string>();
        var decoder = new LibRawDecoder(stages.Add) { MemoryInfo = () => (TotalAvailable: withoutRawBuffer, Load: 0) };

        var error = Assert.Throws<InvalidOperationException>(() => decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded)));

        Assert.Contains("memory", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["configured", "opened"], stages);
    }

    [Fact]
    public void Decode_HeadroomCoversTheRawBufferToo_Decodes()
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason) ||
            RawCorpus.TryGetFile("Canon - EOS 350D - RAW (3_2).CR2") is not { } path) return;
        var size = new LibRawDecoder(null).ReadInfo(path);
        // Generous: raw buffers are at most 2 B per sensor pixel for this Bayer file; the sensor is at most ~10% larger than the output.
        var total = (long)size.Width * size.Height * (8 + 3 + 4 + 3);
        var decoder = new LibRawDecoder(null) { MemoryInfo = () => (TotalAvailable: total, Load: 0) };

        var decoded = decoder.Decode(new DecodeRequest(path, new DecodeBox(240, 180)));

        Assert.Equal(DecoderBackend.LibRaw, decoded.ActualBackend);
    }

    private static (double RG, double BG) Ratios(IDecodedImage image)
    {
        var bitmap = (BitmapSource)image.PlatformImage;
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        double r = 0, g = 0, b = 0;
        for (var i = 0; i < pixels.Length; i += 4) { b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; }
        return (r / g, b / g);
    }
}
