using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.Imaging.Tests.Metadata;
using Xunit.Abstractions;

namespace PhotoReview.Imaging.Tests.Robustness;

/// <summary>
/// Byte-level mutation of small valid images (JPEG with EXIF, grayscale JPEG, PNG) through every decoder and through the
/// production TurboJpeg -> WPF fallback chain. A corrupt file may fail, but only with the exception types the
/// application's error paths understand: a managed-bug exception (NullReference, IndexOutOfRange, InvalidCast, Overflow,
/// ArgumentOutOfRange, DivideByZero), an out-of-memory from a tiny mutated header, or a stall is a decoder defect.
/// The decodes run from in-memory bytes (no file I/O), so the corpus is pure CPU.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class DecoderMutationFuzzTests(ITestOutputHelper output)
{
    private static bool IsDefect(Exception ex) => ex is NullReferenceException or IndexOutOfRangeException or InvalidCastException
        or OverflowException or ArgumentOutOfRangeException or DivideByZeroException or OutOfMemoryException or AccessViolationException
        or InsufficientMemoryException or ArrayTypeMismatchException;

    private static byte[] GrayJpeg()
    {
        var gray = new FormatConvertedBitmap(FixtureGenerator.CreateGradientCheckerboard(24, 16), PixelFormats.Gray8, null, 0);
        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(gray));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] TinyPng()
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(FixtureGenerator.CreateGradientCheckerboard(16, 12)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static readonly (string Name, Func<byte[]> Build)[] Seeds =
    [
        ("jpeg-exif-o6", () => ExifTestData.EncodeJpegWithExif(24, 16, orientation: 6)),
        ("jpeg-exif-o8", () => ExifTestData.EncodeJpegWithExif(17, 23, orientation: 8)),
        ("jpeg-gray", GrayJpeg),
        ("png", TinyPng),
    ];

    private static IEnumerable<(string Name, IImageDecoder Decoder)> Decoders()
    {
        yield return ("Wpf", new WpfBitmapImageDecoder());
        yield return ("WicDirect", new WicDirectDecoder());
        yield return ("TurboJpeg", new TurboJpegDecoder());
        yield return ("Turbo->Wpf chain", new FallbackImageDecoder(new TurboJpegDecoder(), DecoderBackend.TurboJpeg, new WpfBitmapImageDecoder()));
        yield return ("WicDirect->Wpf chain", new FallbackImageDecoder(new WicDirectDecoder(), DecoderBackend.WicDirect, new WpfBitmapImageDecoder()));
    }

    private void Run(int iterationsPerSeed, int rngSeed)
    {
        var rng = new Random(rngSeed);
        var box = new DecodeBox(12, 12);
        var defects = new List<string>();
        var histogram = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var worst = TimeSpan.Zero;
        var decoded = 0;

        foreach (var (seedName, build) in Seeds)
        {
            var seed = build();
            for (var i = 0; i < iterationsPerSeed; i++)
            {
                var mutant = JpegBytes.Mutate(rng, seed);
                foreach (var (decoderName, decoder) in Decoders())
                {
                    foreach (var request in new[]
                    {
                        new DecodeRequest("mutant.bin", box, bytes: mutant),
                        new DecodeRequest("mutant.bin", DecodeBox.Unbounded, bytes: mutant),
                    })
                    {
                        var started = Stopwatch.GetTimestamp();
                        try
                        {
                            var image = decoder.Decode(request);
                            decoded++;
                            Assert.True(image.PixelWidth > 0 && image.PixelHeight > 0);
                            Assert.InRange(image.Orientation, 1, 8);
                            Assert.True(image.OriginalWidth > 0 && image.OriginalHeight > 0);
                        }
                        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
                        {
                            var hk = $"{decoderName}: {ex.GetType().Name}" + (ex is ArgumentException ? $" [{ex.Message.Split((char)10)[0]}] at {ex.StackTrace?.Split((char)10).FirstOrDefault()?.Trim()}" : "");
                            histogram[hk] = histogram.GetValueOrDefault(hk) + 1;
                            if (IsDefect(ex) && defects.Count < 12)
                                defects.Add($"{decoderName} on {seedName} mutant {i} ({mutant.Length} B, box={request.Box}): {ex.GetType().Name}: {ex.Message} :: {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
                        }

                        var elapsed = Stopwatch.GetElapsedTime(started);
                        if (elapsed > worst) worst = elapsed;
                    }
                }
            }
        }

        foreach (var (key, count) in histogram) output.WriteLine($"{count,6}  {key}");
        output.WriteLine($"decoded ok: {decoded}, slowest decode {worst.TotalMilliseconds:F0} ms");
        Assert.True(defects.Count == 0, "Defect-class exceptions from corrupt input:\n" + string.Join("\n", defects));
        Assert.True(worst < TimeSpan.FromSeconds(5), $"slowest decode {worst.TotalSeconds:F1} s");
        Assert.True(decoded > 100, $"only {decoded} mutants decoded: the corpus is too destructive to reach the pixel paths");
    }

    [Fact(DisplayName = "Mutated JPEG/PNG bytes fail only with application-understood exception types in every decoder and in the fallback chains")]
    public void MutatedImages_FailOnlyWithUnderstoodExceptions() => Run(iterationsPerSeed: 60, rngSeed: 11);

    /// <summary>Byte range of the first APP1 segment (marker through payload) of an encoder-written JPEG.</summary>
    private static (int Start, int End) FindApp1(byte[] jpeg)
    {
        for (var i = 2; i + 4 <= jpeg.Length;)
        {
            if (jpeg[i] != 0xFF) break;
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (jpeg[i + 1] == 0xE1) return (i, i + 2 + length);
            i += 2 + length;
        }

        throw new InvalidOperationException("no APP1 in the seed");
    }

    [Fact(DisplayName = "Corrupt EXIF metadata (mutation confined to the APP1 segment of an otherwise valid JPEG) never makes any decoder throw a defect-class exception or misreport orientation")]
    public void CorruptExifMetadata_NeverBreaksTheDecoders()
    {
        var rng = new Random(20260926);
        var seed = ExifTestData.EncodeJpegWithExif(24, 16, orientation: 6);
        var (start, end) = FindApp1(seed);
        var defects = new List<string>();
        var decoded = 0;
        for (var i = 0; i < 1500; i++)
        {
            var mutant = (byte[])seed.Clone();
            var flips = rng.Next(1, 4);
            for (var f = 0; f < flips; f++) mutant[rng.Next(start + 4, end)] = (byte)rng.Next(256);
            foreach (var (name, decoder) in Decoders())
            {
                try
                {
                    var image = decoder.Decode(new DecodeRequest("mutant.jpg", new DecodeBox(12, 12), bytes: mutant));
                    decoded++;
                    Assert.InRange(image.Orientation, 1, 8);
                }
                catch (Exception ex) when (ex is not Xunit.Sdk.XunitException && IsDefect(ex))
                {
                    if (defects.Count < 10) defects.Add($"{name} mutant {i}: {ex.GetType().Name}: {ex.Message} :: {ex.StackTrace?.Split((char)10).FirstOrDefault()?.Trim()}");
                }
                catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
                {
                    // A corrupt-but-not-metadata-only mutant (e.g. a broken segment length) may legitimately fail to decode.
                }
            }
        }

        Assert.True(defects.Count == 0, "Defect-class exceptions from corrupt EXIF:" + Environment.NewLine + string.Join(Environment.NewLine, defects));
        Assert.True(decoded > 1500, $"only {decoded} decodes succeeded: the corpus is too destructive");
    }

    [Fact(DisplayName = "An orientation tag that holds two SHORT values (count 2) is read the same way by all three decoders (first value), not thrown on")]
    public void OrientationTagWithTwoValues_IsReadConsistently()
    {
        var jpeg = ExifTestData.EncodeJpegWithExif(24, 16, orientation: 6);
        var (start, end) = FindApp1(jpeg);
        // Find the IFD0 entry with tag 0x0112 (byte order per the TIFF header) and set its count to 2: two SHORTs still fit
        // the 4 inline value bytes, and WIC then returns ushort[] for the query.
        var tiff = start + 4 + 6;
        var little = jpeg[tiff] == (byte)'I';
        int U16(int at) => little ? jpeg[at] | (jpeg[at + 1] << 8) : (jpeg[at] << 8) | jpeg[at + 1];
        int U32(int at) => little ? U16(at) | (U16(at + 2) << 16) : (U16(at) << 16) | U16(at + 2);
        var ifd0 = tiff + U32(tiff + 4);
        var entries = U16(ifd0);
        var patched = false;
        for (var e = 0; e < entries; e++)
        {
            var at = ifd0 + 2 + e * 12;
            if (U16(at) != 0x0112) continue;
            if (little) jpeg[at + 4] = 2; else jpeg[at + 7] = 2;
            patched = true;
        }
        Assert.True(patched && end > start);

        foreach (var (name, decoder) in Decoders())
        {
            var image = decoder.Decode(new DecodeRequest("two-values.jpg", DecodeBox.Unbounded, bytes: jpeg));
            Assert.True(image.Orientation == 6, $"{name} reported orientation {image.Orientation}");
            Assert.Equal((16, 24), (image.PixelWidth, image.PixelHeight));
        }
    }

    [Theory(DisplayName = "A JPEG whose EXIF TIFF header is damaged (bad byte order mark, magic or IFD offset) still decodes in every decoder and chain, with orientation 1")]
    [InlineData(0)] // byte order mark
    [InlineData(2)] // magic 42
    [InlineData(3)]
    [InlineData(4)] // IFD0 offset (high byte)
    public void DamagedExifTiffHeader_StillDecodes(int tiffOffset)
    {
        var jpeg = ExifTestData.EncodeJpegWithExif(24, 16, orientation: 6);
        var (start, _) = FindApp1(jpeg);
        var at = start + 4 + 6 + tiffOffset;
        jpeg[at] = (byte)(jpeg[at] ^ 0x5A);

        // The bare WicDirect decoder may answer COMException for damaged colour metadata: that is its designed signal to
        // hand the file to the WPF fallback, which is what the chain (the production configuration) must then do.
        foreach (var (name, decoder) in Decoders().Where(d => d.Name != "WicDirect"))
        {
            foreach (var box in new[] { new DecodeBox(12, 12), DecodeBox.Unbounded })
            {
                var image = decoder.Decode(new DecodeRequest("damaged-exif.jpg", box, bytes: jpeg));
                Assert.True(image.Orientation == 1, $"{name} reported orientation {image.Orientation}");
                Assert.Equal((24, 16), (image.OriginalWidth, image.OriginalHeight));
            }
        }
    }

    [Fact(DisplayName = "Deep run: 50x more mutated images through every decoder (about a minute)")]
    [Trait("Category", "Slow")]
    public void MutatedImages_DeepRun() => Run(iterationsPerSeed: 3000, rngSeed: DeepSeed(12));

    /// <summary>PHOTOREVIEW_FUZZ_SEED overrides the deep-run seed, so an unattended job can sweep seeds.</summary>
    internal static int DeepSeed(int fallback) => int.TryParse(Environment.GetEnvironmentVariable("PHOTOREVIEW_FUZZ_SEED"), out var seed) ? seed : fallback;
}
