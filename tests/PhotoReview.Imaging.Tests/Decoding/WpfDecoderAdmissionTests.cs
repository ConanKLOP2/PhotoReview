using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// D-08: the WPF decoder (the fallback backend) applies the same upfront memory admission as the other backends, so a huge
/// original that fails in the primary for another fallbackable reason is not allocated unguarded here.
/// </summary>
[Collection("GlobalState")] // the guard builds localized text through the ambient localizer
public sealed class WpfDecoderAdmissionTests : IDisposable
{
    private const int Side = 6000; // 6000 x 6000 x 4 = 144 MB output, above the 128 MB guard threshold; the 1-bit PNG itself is a few KB
    private const long Mb = 1024L * 1024;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PhotoReview-WpfAdmission-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public WpfDecoderAdmissionTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "huge.png");
        var stride = Side / 8;
        var source = BitmapSource.Create(Side, Side, 96, 96, PixelFormats.BlackWhite, null, new byte[stride * Side], stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var file = File.Create(_path);
        encoder.Save(file);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Decode_OutputLargerThanTheMemoryLeft_IsRefusedWithTheAdmissionException()
    {
        var decoder = new WpfBitmapImageDecoder { MemoryInfo = () => (100 * Mb, 0) };

        Assert.Throws<DecoderMemoryAdmissionException>(() => decoder.Decode(new DecodeRequest(_path, TargetWidth: 0, ApplyOrientation: true)));
    }

    [Fact]
    public void Decode_DownscaleToASmallOutput_IsAdmittedAlthoughTheFullSizeWouldNotFit()
    {
        var calls = 0;
        // The full-size 144 MB output would be refused here, but a request that downscales to 1000 px (4 MB) is admitted.
        var decoder = new WpfBitmapImageDecoder { MemoryInfo = () => { calls++; return (100 * Mb, 0); } };

        var image = decoder.Decode(new DecodeRequest(_path, TargetWidth: 1000, ApplyOrientation: true));

        Assert.True(image.PixelWidth <= 1000);
        Assert.Equal(0, calls); // 4 MB is below the guard threshold: the memory is not even consulted
    }

    [Fact(DisplayName = "R16: sides whose 4-byte pixel count wraps a long are refused by admission, not treated as a small buffer")]
    public void EnsureDecodeAdmitted_HostileSidesWhoseByteCountWrapsALong_AreRefused()
    {
        Assert.Throws<DecoderMemoryAdmissionException>(
            () => WpfBitmapImageDecoder.EnsureDecodeAdmitted(int.MaxValue, int.MaxValue, () => (64L * 1024 * Mb, 0)));
    }

    [Fact]
    public void Decode_FullSizeWithEnoughMemory_IsAdmitted()
    {
        var decoder = new WpfBitmapImageDecoder { MemoryInfo = () => (64L * 1024 * Mb, 0) };

        var image = decoder.Decode(new DecodeRequest(_path, TargetWidth: 0, ApplyOrientation: true));

        Assert.Equal(Side, image.PixelWidth);
    }
}
