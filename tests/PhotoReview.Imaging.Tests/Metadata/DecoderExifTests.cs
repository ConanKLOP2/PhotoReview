using System.Diagnostics;
using System.IO;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Metadata;
using Xunit.Abstractions;

namespace PhotoReview.Imaging.Tests.Metadata;

/// <summary>
/// Each backend extracts the photo-information EXIF during its normal decode, from a real encoder-written JPEG,
/// whether it decodes from the file or from pre-read bytes -- and all three agree.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class DecoderExifTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PhotoReview-DecoderExif-" + Guid.NewGuid().ToString("N"));

    public DecoderExifTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static TheoryData<string> Backends => new() { "Wpf", "WicDirect", "TurboJpeg" };

    private static IImageDecoder Create(string backend) => backend switch
    {
        "Wpf" => new WpfBitmapImageDecoder(),
        "WicDirect" => new WicDirectDecoder(),
        _ => new TurboJpegDecoder(),
    };

    private string Write(byte[] bytes, string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory(DisplayName = "A downscaled preview decode carries the EXIF of the source (file path)")]
    [MemberData(nameof(Backends))]
    public void PreviewDecodeFromFileCarriesExif(string backend)
    {
        var path = Write(ExifTestData.EncodeJpegWithExif(), backend + ".jpg");

        var decoded = Create(backend).Decode(new DecodeRequest(path, new DecodeBox(32, 32)));

        Assert.True(decoded.Downscaled);
        ExifTestData.AssertFullCamera(decoded.Exif);
    }

    [Theory(DisplayName = "A decode from pre-read bytes (source-bytes cache) carries the same EXIF")]
    [MemberData(nameof(Backends))]
    public void DecodeFromBytesCarriesExif(string backend)
    {
        var bytes = ExifTestData.EncodeJpegWithExif(orientation: 6);

        var decoded = Create(backend).Decode(new DecodeRequest(Path.Combine(_dir, "unused.jpg"), new DecodeBox(32, 32), bytes: bytes));

        Assert.Equal(6, decoded.Orientation); // orientation still read alongside
        ExifTestData.AssertFullCamera(decoded.Exif);
    }

    [Theory(DisplayName = "A full-resolution (Original mode) decode carries the EXIF too")]
    [MemberData(nameof(Backends))]
    public void FullDecodeCarriesExif(string backend)
    {
        var path = Write(ExifTestData.EncodeJpegWithExif(), "full-" + backend + ".jpg");

        ExifTestData.AssertFullCamera(Create(backend).Decode(new DecodeRequest(path, DecodeBox.Unbounded)).Exif);
    }

    [Theory(DisplayName = "A JPEG without EXIF decodes normally with no EXIF")]
    [MemberData(nameof(Backends))]
    public void NoExifGivesNull(string backend)
    {
        var path = Write(ExifTestData.EncodeJpegWithExif(withExif: false), "plain-" + backend + ".jpg");

        var decoded = Create(backend).Decode(new DecodeRequest(path, new DecodeBox(32, 32)));

        Assert.Equal(32, decoded.PixelWidth);
        Assert.Null(decoded.Exif);
    }

    [Fact(DisplayName = "The byte parser (TurboJpeg) and the WIC query readers agree on an encoder-written JPEG")]
    public void ParserMatchesWicQueries()
    {
        var bytes = ExifTestData.EncodeJpegWithExif();

        var parsed = ExifParser.TryParseJpeg(bytes);
        var wpf = new WpfBitmapImageDecoder().Decode(new DecodeRequest("x.jpg", new DecodeBox(16, 16), bytes: bytes)).Exif;
        var wic = new WicDirectDecoder().Decode(new DecodeRequest("x.jpg", new DecodeBox(16, 16), bytes: bytes)).Exif;

        ExifTestData.AssertFullCamera(parsed);
        Assert.Equal(parsed, wpf);
        Assert.Equal(parsed, wic);
    }

    [Fact(DisplayName = "A PNG decodes with no EXIF and no error on the query path")]
    public void PngHasNoExif()
    {
        var path = Path.Combine(_dir, "a.png");
        PhotoReview.Imaging.Tests.Fixtures.FixtureGenerator.GeneratePng(path, 40, 30);

        Assert.Null(new WpfBitmapImageDecoder().Decode(new DecodeRequest(path, new DecodeBox(20, 20))).Exif);
        Assert.Null(new WicDirectDecoder().Decode(new DecodeRequest(path, new DecodeBox(20, 20))).Exif);
    }

    /// <summary>
    /// Cost report only (no timing assert: wall-clock numbers are not deterministic). Compares the EXIF extraction
    /// alone with a whole preview decode of a 1500x1000 JPEG; the numbers are printed to the test output.
    /// </summary>
    [Fact(DisplayName = "EXIF extraction cost is reported next to the decode it rides on")]
    public void ReportsExtractionCost()
    {
        var bytes = ExifTestData.EncodeJpegWithExif(1500, 1000);
        const int rounds = 200;
        ExifSummary? last = null;

        var parse = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++) last = ExifParser.TryParseJpeg(bytes);
        parse.Stop();

        using var stream = new MemoryStream(bytes);
        var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None).Frames[0];
        var metadata = (System.Windows.Media.Imaging.BitmapMetadata)frame.Metadata;
        _ = ExifOrientation.Read(metadata); // the query the WPF decoder already made before this feature
        var queries = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++) last = WpfExifReader.Read(metadata);
        queries.Stop();
        ExifTestData.AssertFullCamera(last);

        var decoder = new WicDirectDecoder();
        var request = new DecodeRequest("x.jpg", new DecodeBox(960, 640), bytes: bytes);
        decoder.Decode(request); // warm-up
        var decode = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) decoder.Decode(request);
        decode.Stop();

        _output.WriteLine($"ExifParser.TryParseJpeg: {parse.Elapsed.TotalMilliseconds * 1000 / rounds:F1} us/call");
        _output.WriteLine($"WpfExifReader (BitmapMetadata queries): {queries.Elapsed.TotalMilliseconds * 1000 / rounds:F1} us/call");
        _output.WriteLine($"WicDirect 1500x1000 -> 960x640 decode incl. EXIF: {decode.Elapsed.TotalMilliseconds / 10:F2} ms/call");
        Assert.NotNull(last);
    }
}
