using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Quality;
using Xunit.Abstractions;

namespace PhotoReview.Imaging.Tests.Robustness;

/// <summary>
/// JPEG flavours WPF's encoder cannot write (every chroma subsampling, progressive, arithmetic coding, restart intervals,
/// CMYK/YCCK, degenerate sizes) synthesized with libjpeg-turbo's own compressor, then decoded by TurboJpeg, WPF and WicDirect.
/// The production chain must always produce an image of the right size, and where TurboJpeg and WPF both decode, the pixels
/// must agree (a colour-conversion bug in one backend shows as a large mean difference).
/// Native: drives turbojpeg.dll directly.
/// </summary>
[Trait("Category", "Native")]
public sealed class TurboJpegSyntheticFormatTests(ITestOutputHelper output)
{
    private const int TjInitCompress = 0;
    private const int ParamQuality = 3, ParamSubsamp = 4, ParamColorspace = 8, ParamProgressive = 12, ParamArithmetic = 14, ParamRestartRows = 19;
    private const int PixelFormatBgra = 8, PixelFormatCmyk = 11;
    private const int SampGray = 3;

    [DllImport("turbojpeg.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr tj3Init(int initType);
    [DllImport("turbojpeg.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void tj3Destroy(IntPtr handle);
    [DllImport("turbojpeg.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int tj3Set(IntPtr handle, int param, int value);
    [DllImport("turbojpeg.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int tj3Compress8(IntPtr handle, byte[] src, int width, int pitch, int height, int pixelFormat, ref IntPtr jpeg, ref UIntPtr size);
    [DllImport("turbojpeg.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void tj3Free(IntPtr buffer);

    private sealed record Flavour(string Name, int Width, int Height, int Subsamp = 0, bool Progressive = false, bool Arithmetic = false, int RestartRows = 0, bool Cmyk = false, int Colorspace = -1);

    public static TheoryData<string> Flavours() =>
    [
        "444", "422", "420", "440", "411", "gray", "progressive-420", "arithmetic-444", "restart-rows", "cmyk", "ycck",
        "1x1", "1x40", "40x1", "7x9-420", "17x11-422", "9x9-411", "16x16-420-progressive",
    ];

    private static Flavour Resolve(string name) => name switch
    {
        "444" => new(name, 33, 21, 0),
        "422" => new(name, 33, 21, 1),
        "420" => new(name, 33, 21, 2),
        "440" => new(name, 33, 21, 4),
        "411" => new(name, 33, 21, 5),
        "gray" => new(name, 33, 21, SampGray),
        "progressive-420" => new(name, 33, 21, 2, Progressive: true),
        "arithmetic-444" => new(name, 33, 21, 0, Arithmetic: true),
        "restart-rows" => new(name, 33, 21, 2, RestartRows: 1),
        "cmyk" => new(name, 33, 21, 0, Cmyk: true, Colorspace: 3),
        "ycck" => new(name, 33, 21, 0, Cmyk: true, Colorspace: 4),
        "1x1" => new(name, 1, 1),
        "1x40" => new(name, 1, 40, 2),
        "40x1" => new(name, 40, 1, 2),
        "7x9-420" => new(name, 7, 9, 2),
        "17x11-422" => new(name, 17, 11, 1),
        "9x9-411" => new(name, 9, 9, 5),
        "16x16-420-progressive" => new(name, 16, 16, 2, Progressive: true),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static byte[] Compress(Flavour flavour)
    {
        var handle = tj3Init(TjInitCompress);
        Assert.NotEqual(IntPtr.Zero, handle);
        try
        {
            Assert.Equal(0, tj3Set(handle, ParamQuality, 92));
            Assert.Equal(0, tj3Set(handle, ParamSubsamp, flavour.Subsamp));
            if (flavour.Progressive) Assert.Equal(0, tj3Set(handle, ParamProgressive, 1));
            if (flavour.Arithmetic) Assert.Equal(0, tj3Set(handle, ParamArithmetic, 1));
            if (flavour.RestartRows > 0) Assert.Equal(0, tj3Set(handle, ParamRestartRows, flavour.RestartRows));
            if (flavour.Colorspace >= 0) Assert.Equal(0, tj3Set(handle, ParamColorspace, flavour.Colorspace));

            var bytesPerPixel = 4;
            var pixels = new byte[flavour.Width * flavour.Height * bytesPerPixel];
            for (var y = 0; y < flavour.Height; y++)
            {
                for (var x = 0; x < flavour.Width; x++)
                {
                    var o = (y * flavour.Width + x) * bytesPerPixel;
                    // Smooth gradients (JPEG-friendly), distinct per channel so a swapped or inverted channel is visible.
                    pixels[o] = (byte)(40 + 180 * x / Math.Max(1, flavour.Width - 1));
                    pixels[o + 1] = (byte)(40 + 180 * y / Math.Max(1, flavour.Height - 1));
                    pixels[o + 2] = (byte)(200 - 120 * (x + y) / Math.Max(1, flavour.Width + flavour.Height - 2));
                    pixels[o + 3] = flavour.Cmyk ? (byte)(30 + 60 * x / Math.Max(1, flavour.Width - 1)) : (byte)255;
                }
            }

            var jpeg = IntPtr.Zero;
            var size = UIntPtr.Zero;
            var rc = tj3Compress8(handle, pixels, flavour.Width, flavour.Width * bytesPerPixel, flavour.Height,
                flavour.Cmyk ? PixelFormatCmyk : PixelFormatBgra, ref jpeg, ref size);
            Assert.True(rc == 0, "tj3Compress8 failed for " + flavour.Name);

            try
            {
                var bytes = new byte[(int)size];
                Marshal.Copy(jpeg, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { tj3Free(jpeg); }
        }
        finally { tj3Destroy(handle); }
    }

    [Theory(DisplayName = "Synthesized JPEG flavours decode in the production chain at the right size, and TurboJpeg agrees with WPF where both decode")]
    [MemberData(nameof(Flavours))]
    public void Flavour_DecodesConsistently(string name)
    {
        var flavour = Resolve(name);
        var jpeg = Compress(flavour);
        var turbo = new TurboJpeg.TurboJpegDecoder();
        var wpf = new WpfBitmapImageDecoder();
        var chains = new (string Name, IImageDecoder Decoder)[]
        {
            ("Turbo->Wpf", new FallbackImageDecoder(turbo, DecoderBackend.TurboJpeg, wpf)),
            ("WicDirect->Wpf", new FallbackImageDecoder(new WicDirectDecoder(), DecoderBackend.WicDirect, wpf)),
        };

        foreach (var (chainName, chain) in chains)
        {
            var image = chain.Decode(new DecodeRequest("synthetic.jpg", DecodeBox.Unbounded, bytes: jpeg));
            Assert.True(image.PixelWidth == flavour.Width && image.PixelHeight == flavour.Height,
                $"{chainName} decoded {image.PixelWidth}x{image.PixelHeight}, expected {flavour.Width}x{flavour.Height}");
        }

        IDecodedImage? viaTurbo = null;
        try { viaTurbo = turbo.Decode(new DecodeRequest("synthetic.jpg", DecodeBox.Unbounded, bytes: jpeg)); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { output.WriteLine($"{name}: TurboJpeg declines ({ex.GetType().Name}: {ex.Message})"); }

        IDecodedImage? viaWpf = null;
        try { viaWpf = wpf.Decode(new DecodeRequest("synthetic.jpg", DecodeBox.Unbounded, bytes: jpeg)); }
        catch (Exception ex) when (ex is System.IO.FileFormatException or NotSupportedException) { output.WriteLine($"{name}: WPF declines ({ex.GetType().Name}: {ex.Message})"); }

        if (viaTurbo is null || viaWpf is null) return;
        var diff = ImageCompare.Compare(viaTurbo, viaWpf);
        output.WriteLine($"{name}: TurboJpeg vs WPF psnr={diff.Psnr:F1} dE={diff.MeanDeltaE:F2} maxChannelDiff={diff.MaxChannelDiff}");
        Assert.True(diff.MeanDeltaE < 3.0, $"{name}: TurboJpeg and WPF disagree on colours (mean dE {diff.MeanDeltaE:F2}, PSNR {diff.Psnr:F1} dB)");
    }

    public static TheoryData<int, int> Sizes()
    {
        var data = new TheoryData<int, int>();
        foreach (var (w, h) in new[] { (1, 1), (1, 7), (7, 1), (33, 21), (64, 48), (100, 3), (3, 100), (129, 257), (255, 255) }) data.Add(w, h);
        return data;
    }

    [Theory(DisplayName = "Every backend decodes a synthesized JPEG into exactly DecodeBox.Fit's size for degenerate and odd source sizes and tiny/one-sided boxes (DCT scaling rounding)")]
    [MemberData(nameof(Sizes))]
    public void BoxDecode_ProducesExactFitSize(int width, int height)
    {
        var jpeg = Compress(new Flavour("box", width, height, Subsamp: 2));
        var decoders = new (string Name, IImageDecoder Decoder)[]
        {
            ("TurboJpeg", new TurboJpeg.TurboJpegDecoder()),
            ("Wpf", new WpfBitmapImageDecoder()),
            ("WicDirect", new WicDirectDecoder()),
        };
        foreach (var box in new[] { new DecodeBox(1, 1), new DecodeBox(2, 2), new DecodeBox(5, 0), new DecodeBox(0, 5), new DecodeBox(16, 16), new DecodeBox(50, 50), new DecodeBox(1000, 1000), new DecodeBox(7, 3) })
        {
            var expected = box.Fit(width, height);
            foreach (var (name, decoder) in decoders)
            {
                var image = decoder.Decode(new DecodeRequest("box.jpg", box, applyOrientation: false, bytes: jpeg));
                Assert.True((image.PixelWidth, image.PixelHeight) == expected,
                    $"{name}: {width}x{height} into box {box.Width}x{box.Height} gave {image.PixelWidth}x{image.PixelHeight}, expected {expected.Width}x{expected.Height}");
                Assert.Equal((width, height), (image.OriginalWidth, image.OriginalHeight));
            }
        }
    }
}
