using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Metadata;

namespace PhotoReview.Imaging.Tests.Robustness;

/// <summary>
/// Files that are not decodable images at all (missing, a directory, zero bytes, one byte, text, a bare SOI, an exclusively
/// locked file) must fail every decoder, chain, Decode and ReadInfo alike, each time with an exception the application
/// understands (I/O, access, unsupported, invalid data, file format, COM) -- never a managed-bug exception, never a hang and
/// never a "successful" garbage image.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class DecoderFileEdgeCaseTests : IDisposable
{
    private readonly TempRoot _root = new("DecoderFileEdge");

    public void Dispose() => _root.Dispose();

    private static IEnumerable<(string Name, IImageDecoder Decoder)> Decoders()
    {
        yield return ("Wpf", new WpfBitmapImageDecoder());
        yield return ("WicDirect", new WicDirectDecoder());
        yield return ("TurboJpeg", new TurboJpegDecoder());
        yield return ("Turbo->Wpf", new FallbackImageDecoder(new TurboJpegDecoder(), DecoderBackend.TurboJpeg, new WpfBitmapImageDecoder()));
        yield return ("WicDirect->Wpf", new FallbackImageDecoder(new WicDirectDecoder(), DecoderBackend.WicDirect, new WpfBitmapImageDecoder()));
    }

    private static bool IsUnderstood(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException
            or FileFormatException or COMException;

    public static TheoryData<string> Cases() =>
        ["missing", "directory", "zero-bytes", "one-byte", "text", "bare-soi", "png-signature-only", "locked"];

    [Theory(DisplayName = "A file that is not a decodable image fails Decode and ReadInfo of every decoder and chain with an understood exception")]
    [MemberData(nameof(Cases))]
    public void NotAnImage_FailsCleanly(string kind)
    {
        FileStream? held = null;
        string path;
        switch (kind)
        {
            case "missing": path = _root.Combine("nope.jpg"); break;
            case "directory": path = _root.Dir("folder.jpg"); break;
            case "zero-bytes": path = _root.File("zero.jpg"); break;
            case "one-byte": path = _root.File("one.jpg", 0xFF); break;
            case "text": path = _root.File("text.jpg", "just some text, not an image"u8.ToArray()); break;
            case "bare-soi": path = _root.File("soi.jpg", 0xFF, 0xD8, 0xFF); break;
            case "png-signature-only": path = _root.File("sig.png", 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A); break;
            case "locked":
                path = _root.File("locked.jpg", ExifTestData.EncodeJpegWithExif(16, 16));
                held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }

        using (held)
        {
            foreach (var (name, decoder) in Decoders())
            {
                var decode = Record(() => decoder.Decode(new DecodeRequest(path, new DecodeBox(64, 64))));
                Assert.True(decode is not null, $"{name}.Decode of '{kind}' returned an image");
                Assert.True(IsUnderstood(decode!), $"{name}.Decode of '{kind}' threw {decode!.GetType().FullName}: {decode.Message}");

                var info = Record(() => decoder.ReadInfo(path));
                Assert.True(info is not null, $"{name}.ReadInfo of '{kind}' returned info");
                Assert.True(IsUnderstood(info!), $"{name}.ReadInfo of '{kind}' threw {info!.GetType().FullName}: {info.Message}");
            }
        }
    }

    private static Exception? Record(Action action)
    {
        try { action(); return null; }
        catch (Exception ex) { return ex; }
    }

    [Fact(DisplayName = "A missing file is a FileNotFoundException/DirectoryNotFoundException from every decoder (stale-file handling depends on that type)")]
    public void MissingFile_KeepsItsExceptionType()
    {
        var path = _root.Combine("gone.jpg");
        foreach (var (name, decoder) in Decoders())
        {
            var ex = Record(() => decoder.Decode(new DecodeRequest(path, new DecodeBox(64, 64))));
            Assert.True(ex is FileNotFoundException or DirectoryNotFoundException, $"{name}: {ex?.GetType().FullName}");
        }
    }

    [Fact(DisplayName = "ReadInfo on mutated JPEG files never throws a managed-bug exception, and reports sane dimensions when it succeeds")]
    public void ReadInfo_OnMutatedFiles_IsWellBehaved()
    {
        var seed = ExifTestData.EncodeJpegWithExif(24, 16, orientation: 6);
        var rng = new Random(606);
        var path = _root.Combine("mutant.jpg");
        var defects = new List<string>();
        var succeeded = 0;
        for (var i = 0; i < 250; i++)
        {
            File.WriteAllBytes(path, JpegBytes.Mutate(rng, seed));
            foreach (var (name, decoder) in Decoders())
            {
                try
                {
                    var info = decoder.ReadInfo(path);
                    succeeded++;
                    Assert.True(info.PixelWidth > 0 && info.PixelHeight > 0 && info.Width > 0 && info.Height > 0, $"{name}: non-positive size");
                    Assert.InRange(info.Orientation, 1, 8);
                }
                catch (Exception ex) when (IsUnderstood(ex))
                {
                    // A corrupt file may legitimately fail.
                }
                catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
                {
                    if (defects.Count < 10) defects.Add($"mutant {i} {name}: {ex.GetType().Name}: {ex.Message.Split((char)10)[0]}");
                }
            }
        }

        Assert.True(defects.Count == 0, "ReadInfo threw non-understood exceptions:" + Environment.NewLine + string.Join(Environment.NewLine, defects));
        Assert.True(succeeded > 250, $"only {succeeded} ReadInfo calls succeeded");
    }
}
