using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

[Collection(LibRawNativeDecodeGate.Name)]
[Trait("Category", "Native")]
public sealed class LibRawWhiteBalanceAndErrorTests
{
    private static readonly string SamplePath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus/Canon - EOS 350D - RAW (3_2).CR2"));

    [Theory]
    [InlineData(-100007)] // LIBRAW_UNSUFFICIENT_MEMORY
    [InlineData(12)]      // ENOMEM returned as a positive errno
    public void CreateFailure_InsufficientMemory_IsInvalidOperationLikeManagedOutOfMemory(int code)
    {
        var ex = LibRawDecoder.CreateFailure(code, "unpack RAW data");

        Assert.IsType<InvalidOperationException>(ex);
        Assert.IsNotType<InvalidDataException>(ex);
    }

    [Theory]
    [InlineData(-100008)] // LIBRAW_DATA_ERROR
    [InlineData(-100009)] // LIBRAW_IO_ERROR
    [InlineData(-2)]      // LIBRAW_FILE_UNSUPPORTED
    public void CreateFailure_OtherCodes_StayInvalidData(int code)
    {
        Assert.IsType<InvalidDataException>(LibRawDecoder.CreateFailure(code, "unpack RAW data"));
    }

    [Fact]
    public void TrySetUseCameraWb_AfterOutputSetters_SetsTheFlagAndRefusesAMismatchedLayout()
    {
        if (!LibRawAvailability.Probe(out _)) return;
        using var handle = new SafeLibRawHandle(LibRawNativeMethods.LibRawInit(0));
        LibRawNativeMethods.LibRawSetOutputColor(handle, 1);
        LibRawNativeMethods.LibRawSetOutputBps(handle, 8);
        LibRawNativeMethods.LibRawSetNoAutoBright(handle, 1);
        Assert.Equal(0, LibRawNativeMethods.ReadUseCameraWb(handle));

        // Values that do not read back where expected (a different layout) must leave memory untouched.
        Assert.False(LibRawNativeMethods.TrySetUseCameraWb(handle, 1, 16, 1));
        Assert.Equal(0, LibRawNativeMethods.ReadUseCameraWb(handle));

        Assert.True(LibRawNativeMethods.TrySetUseCameraWb(handle, 1, 8, 1));
        Assert.Equal(1, LibRawNativeMethods.ReadUseCameraWb(handle));
    }

    [Fact]
    public void Decode_UsesCameraWhiteBalance_NotLibRawDaylightDefaults()
    {
        if (!LibRawAvailability.Probe(out _) || !File.Exists(SamplePath)) return;

        var daylight = RedToBlueRatio(NativeDecodeMeans(useCameraWb: false));
        var camera = RedToBlueRatio(NativeDecodeMeans(useCameraWb: true));
        // Guard the premise: the two white balances must be distinguishable on this sample.
        Assert.True(Math.Abs(camera / daylight - 1) > 0.05, $"daylight R/B {daylight:F3} vs camera R/B {camera:F3}");

        var image = new LibRawDecoder().Decode(new DecodeRequest(SamplePath, new DecodeBox(640, 480)));
        var decoded = RedToBlueRatio(BitmapMeans((BitmapSource)image.PlatformImage));

        Assert.True(Math.Abs(decoded / camera - 1) < 0.03, $"decoder R/B {decoded:F3}, camera-WB {camera:F3}, daylight {daylight:F3}");
    }

    private static double RedToBlueRatio((double R, double G, double B) means) => means.R / means.B;

    private static (double R, double G, double B) BitmapMeans(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        double r = 0, g = 0, b = 0;
        var count = pixels.Length / 4;
        for (var i = 0; i < pixels.Length; i += 4) { b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; }
        return (r / count, g / count, b / count);
    }

    private static (double R, double G, double B) NativeDecodeMeans(bool useCameraWb)
    {
        using var raw = new SafeLibRawHandle(LibRawNativeMethods.LibRawInit(0));
        Assert.Equal(0, LibRawNativeMethods.LibRawOpenWFile(raw, SamplePath));
        LibRawNativeMethods.LibRawSetOutputColor(raw, 1);
        LibRawNativeMethods.LibRawSetOutputBps(raw, 8);
        LibRawNativeMethods.LibRawSetNoAutoBright(raw, 1);
        if (useCameraWb) Assert.True(LibRawNativeMethods.TrySetUseCameraWb(raw, 1, 8, 1));
        Assert.Equal(0, LibRawNativeMethods.LibRawUnpack(raw));
        Assert.Equal(0, LibRawNativeMethods.LibRawDcrawProcess(raw));
        var pointer = LibRawNativeMethods.LibRawDcrawMakeMemImage(raw, out var error);
        Assert.NotEqual(IntPtr.Zero, pointer);
        Assert.Equal(0, error);
        using var image = new SafeLibRawImageHandle(pointer);
        // libraw_processed_image_t: int type; ushort height, width, colors, bits; uint data_size; uchar data[] at 16.
        var height = (ushort)Marshal.ReadInt16(pointer, 4);
        var width = (ushort)Marshal.ReadInt16(pointer, 6);
        double r = 0, g = 0, b = 0;
        var count = (long)width * height;
        for (long i = 0; i < count; i += 7) // sparse sample is plenty for a channel mean
        {
            var offset = 16 + (int)(i * 3);
            r += Marshal.ReadByte(pointer, offset);
            g += Marshal.ReadByte(pointer, offset + 1);
            b += Marshal.ReadByte(pointer, offset + 2);
        }
        var samples = (count + 6) / 7;
        return (r / samples, g / samples, b / samples);
    }
}
