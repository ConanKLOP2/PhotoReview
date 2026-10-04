using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

// Needs libraw.dll (and the corpus). The pure managed checks (error mapping, version gates, the layout proof over a fake
// memory block) live in LibRawManagedLogicTests, which runs in the default category.
[Collection(LibRawNativeDecodeGate.Name)]
[Trait("Category", "Native")]
public sealed class LibRawWhiteBalanceAndErrorTests
{
    [Fact]
    public void TrySetUseCameraWb_PinnedLayout_SetsFlagAndAppliesOutputSettings()
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason)) return;
        using var handle = new SafeLibRawHandle(LibRawNativeMethods.LibRawInit(0));
        var layout = LibRawNativeMethods.PinnedWhiteBalanceLayout;
        Assert.Equal(0, ReadUseCameraWb(handle));

        Assert.True(LibRawNativeMethods.TrySetUseCameraWb(handle, 1, 8, 1));

        Assert.Equal(1, ReadUseCameraWb(handle));
        // The sentinels used for the layout proof must be gone: the real settings are what LibRaw will use.
        Assert.Equal(1, ReadInt32(handle, layout.OutputColor));
        Assert.Equal(8, ReadInt32(handle, layout.OutputBps));
        Assert.Equal(1, ReadInt32(handle, layout.NoAutoBright));
    }

    [Theory]
    [InlineData(4, 0, 0)]    // output_color read from the wrong place
    [InlineData(0, -4, 0)]   // output_bps read from the wrong place
    [InlineData(0, 0, 8)]    // no_auto_bright read from the wrong place
    [InlineData(-8, -8, -8)] // a whole shifted block
    public void TrySetUseCameraWb_WrongOffset_FailsClosedWithoutWritingTheFlag(int colorShift, int bpsShift, int brightShift)
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason)) return;
        using var handle = new SafeLibRawHandle(LibRawNativeMethods.LibRawInit(0));
        var real = LibRawNativeMethods.PinnedWhiteBalanceLayout;
        var wrong = new LibRawNativeMethods.WhiteBalanceLayout(real.OutputColor + colorShift, real.OutputBps + bpsShift,
            real.NoAutoBright + brightShift, real.UseCameraWb);

        var applied = LibRawNativeMethods.TrySetUseCameraWb(handle, 1, 8, 1, () => true, wrong);

        Assert.False(applied);
        Assert.Equal(0, ReadUseCameraWb(handle));
        // The output settings still reach LibRaw through the supported setters (daylight WB, sRGB, 8 bit).
        Assert.Equal(1, ReadInt32(handle, real.OutputColor));
        Assert.Equal(8, ReadInt32(handle, real.OutputBps));
        Assert.Equal(1, ReadInt32(handle, real.NoAutoBright));
    }

    [Theory]
    [InlineData("Canon - EOS 350D - RAW (3_2).CR2")]
    [InlineData("Nikon - D40X - 12bit 12bit compressed (Lossy (type 1)) (3_2).NEF")]
    [InlineData("Olympus - E-P3 - 16bit (4_3).ORF")]
    public void Decode_CameraWhiteBalance_ChangesOutputAndMovesTowardTheEmbeddedPreview(string fileName)
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason) || RawCorpus.TryGetFile(fileName) is not { } path) return;

        var daylight = Ratios(NativeDecodeMeans(path, useCameraWb: false));
        var camera = Ratios(NativeDecodeMeans(path, useCameraWb: true));
        var preview = Ratios(PreviewMeans(path));

        // The flag really changed LibRaw's output (R/G or B/G moves by more than 5 %).
        Assert.True(Math.Abs(camera.RG / daylight.RG - 1) > 0.05 || Math.Abs(camera.BG / daylight.BG - 1) > 0.05,
            $"{fileName}: daylight {daylight} vs camera {camera}");
        // The camera-WB decode has the cast of the camera's own JPEG preview, the daylight decode does not.
        Assert.True(Distance(camera, preview) < Distance(daylight, preview) / 2,
            $"{fileName}: preview {preview}, camera-WB {camera}, daylight {daylight}");

        var image = new LibRawDecoder().Decode(new DecodeRequest(path, new DecodeBox(320, 240)));
        var decoded = Ratios(BitmapMeans((BitmapSource)image.PlatformImage));
        Assert.True(Math.Abs(decoded.RG / camera.RG - 1) < 0.04 && Math.Abs(decoded.BG / camera.BG - 1) < 0.04,
            $"{fileName}: decoder {decoded}, camera-WB {camera}, daylight {daylight}");
    }

    private static (double RG, double BG) Ratios((double R, double G, double B) means) => (means.R / means.G, means.B / means.G);

    private static double Distance((double RG, double BG) a, (double RG, double BG) b) =>
        Math.Abs(Math.Log(a.RG / b.RG)) + Math.Abs(Math.Log(a.BG / b.BG));

    private static (double R, double G, double B) PreviewMeans(string path)
    {
        var jpeg = LibRawDecoder.ReadJpegThumbnail(path);
        using var stream = new MemoryStream(jpeg);
        var frame = new JpegBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        return BitmapMeans(new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgr32, null, 0));
    }

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

    private static (double R, double G, double B) NativeDecodeMeans(string path, bool useCameraWb)
    {
        using var raw = new SafeLibRawHandle(LibRawNativeMethods.LibRawInit(0));
        // Same order as LibRawDecoder: parameters first, then open (ORF/PEF only adopt the camera matrix if the flag is set at open).
        LibRawNativeMethods.LibRawSetOutputColor(raw, 1);
        LibRawNativeMethods.LibRawSetOutputBps(raw, 8);
        LibRawNativeMethods.LibRawSetNoAutoBright(raw, 1);
        if (useCameraWb) Assert.True(LibRawNativeMethods.TrySetUseCameraWb(raw, 1, 8, 1));
        Assert.Equal(0, LibRawNativeMethods.LibRawOpenWFile(raw, path));
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

    /// <summary>Reads an int at a byte offset of the libraw_data_t.</summary>
    private static int ReadInt32(SafeLibRawHandle handle, int offset) => Marshal.ReadInt32(handle.DangerousGetHandle(), offset);

    /// <summary>Reads use_camera_wb back; only meaningful for the pinned layout.</summary>
    private static int ReadUseCameraWb(SafeLibRawHandle handle) => ReadInt32(handle, LibRawNativeMethods.PinnedWhiteBalanceLayout.UseCameraWb);
}
