using System.IO;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// Mutation-testing gap closers (Stryker survivors in TurboJpegDecoder ~L129-217, WicDirectDecoder ~L173,
/// WpfBitmapImageDecoder ~L138-224, DecodeRequest.IsDownscaleRequested): the exact output size, the
/// <see cref="IDecodedImage.Downscaled"/> flag and the reported original size of every backend for requests equal to, smaller
/// than and larger than the stored size, on a single axis only, with and without EXIF orientation.
/// </summary>
public sealed class DecodeSizeDecisionMutationTests : IClassFixture<DecodeSizeDecisionMutationTests.Sources>
{
    private readonly Sources _src;

    public DecodeSizeDecisionMutationTests(Sources src) => _src = src;

    public sealed class Sources : IDisposable
    {
        private readonly TempRoot _root = new("size-decisions");

        public Sources()
        {
            Plain = FixtureGenerator.GenerateGradientJpeg(_root.Combine("plain-64x48.jpg"), 64, 48);
            FlatWide = FixtureGenerator.GenerateGradientJpeg(_root.Combine("flat-64x1.jpg"), 64, 1);
            FlatTall = FixtureGenerator.GenerateGradientJpeg(_root.Combine("flat-1x64.jpg"), 1, 64);
            Rotated6 = FixtureGenerator.GenerateJpegWithOrientation(_root.Combine("rot6-64x48.jpg"), 64, 48, 6);
            Rotated6Flat = FixtureGenerator.GenerateJpegWithOrientation(_root.Combine("rot6-64x1.jpg"), 64, 1, 6);
            ThreeRows = FixtureGenerator.GenerateGradientJpeg(_root.Combine("short-64x3.jpg"), 64, 3);
            Narrow = FixtureGenerator.GenerateGradientJpeg(_root.Combine("narrow-3x64.jpg"), 3, 64);
        }

        public string ThreeRows { get; }
        public string Narrow { get; }

        public string Plain { get; }
        public string FlatWide { get; }
        public string FlatTall { get; }
        public string Rotated6 { get; }
        public string Rotated6Flat { get; }

        public void Dispose() => _root.Dispose();
    }

    public static readonly string[] Backends = ["TurboJpeg", "WicDirect", "Wpf"];

    private static IImageDecoder Create(string backend) => backend switch
    {
        "TurboJpeg" => new TurboJpegDecoder(),
        "WicDirect" => new WicDirectDecoder(WpfBitmapSourceCodec.Instance),
        "Wpf" => new WpfBitmapImageDecoder(),
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    /// <summary>(file key, box width, box height, expected width, expected height, expected Downscaled, expected original width/height).</summary>
    private static readonly (string File, int BoxW, int BoxH, int W, int H, bool Down, int OrigW, int OrigH)[] Cases =
    [
        // Equal to the stored size: nothing to do on either axis, however the box is expressed.
        ("plain", 64, 48, 64, 48, false, 64, 48),
        ("plain", 64, 0, 64, 48, false, 64, 48),
        ("plain", 0, 48, 64, 48, false, 64, 48),
        ("plain", 65, 49, 64, 48, false, 64, 48),
        ("plain", 640, 480, 64, 48, false, 64, 48),
        ("plain", 640, 0, 64, 48, false, 64, 48),
        ("plain", 0, 480, 64, 48, false, 64, 48),
        // Smaller on one axis of the box (both stored axes shrink: the aspect ratio is kept).
        ("plain", 63, 0, 63, 47, true, 64, 48),
        ("plain", 0, 47, 62, 47, true, 64, 48),
        ("plain", 640, 47, 62, 47, true, 64, 48),
        ("plain", 63, 480, 63, 47, true, 64, 48),
        ("plain", 32, 24, 32, 24, true, 64, 48),
        ("plain", 40, 0, 40, 30, true, 64, 48),
        ("plain", 10, 10, 10, 7, true, 64, 48),
        // A one-pixel strip: only ONE stored axis shrinks (the other stays 1).
        ("flatwide", 63, 0, 63, 1, true, 64, 1),
        ("flatwide", 63, 480, 63, 1, true, 64, 1),
        ("flatwide", 64, 0, 64, 1, false, 64, 1),
        ("flatwide", 640, 1, 64, 1, false, 64, 1),
        ("flattall", 0, 63, 1, 63, true, 1, 64),
        ("flattall", 480, 63, 1, 63, true, 1, 64),
        ("flattall", 0, 64, 1, 64, false, 1, 64),
        ("flattall", 1, 640, 1, 64, false, 1, 64),
        // The DCT scale (1/2) hits the target on one axis but still overshoots on the other: the fine scale must still run.
        ("short", 32, 0, 32, 1, true, 64, 3),
        ("short", 0, 1, 21, 1, true, 64, 3),
        ("short", 640, 1, 21, 1, true, 64, 3),
        ("narrow", 0, 32, 1, 32, true, 3, 64),
        ("narrow", 1, 0, 1, 21, true, 3, 64),
        ("narrow", 1, 640, 1, 21, true, 3, 64),
    ];

    public static TheoryData<string, string, int, int, int, int, bool, int, int> SizeCases()
    {
        var data = new TheoryData<string, string, int, int, int, int, bool, int, int>();
        foreach (var backend in Backends)
            foreach (var c in Cases)
                data.Add(backend, c.File, c.BoxW, c.BoxH, c.W, c.H, c.Down, c.OrigW, c.OrigH);
        return data;
    }

    private string PathOf(string file) => file switch
    {
        "plain" => _src.Plain,
        "flatwide" => _src.FlatWide,
        "flattall" => _src.FlatTall,
        "short" => _src.ThreeRows,
        "narrow" => _src.Narrow,
        "rot6" => _src.Rotated6,
        "rot6flat" => _src.Rotated6Flat,
        _ => throw new ArgumentOutOfRangeException(nameof(file)),
    };

    [Theory]
    [MemberData(nameof(SizeCases))]
    public void Decode_BoxAgainstStoredSize_ExactSizeDownscaledFlagAndOriginal(
        string backend, string file, int boxW, int boxH, int w, int h, bool down, int origW, int origH)
    {
        var decoded = Create(backend).Decode(new DecodeRequest(PathOf(file), new DecodeBox(boxW, boxH)));

        Assert.Equal((w, h), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal(down, decoded.Downscaled);
        Assert.Equal((origW, origH), (decoded.OriginalWidth, decoded.OriginalHeight));
        Assert.Equal(1, decoded.Orientation);
    }

    [Theory]
    [MemberData(nameof(SizeCases))]
    public void Decode_LegacyWidthHeightRequest_BehavesLikeTheBox(
        string backend, string file, int boxW, int boxH, int w, int h, bool down, int origW, int origH)
    {
        var decoded = Create(backend).Decode(new DecodeRequest(PathOf(file), boxW, ApplyOrientation: true, Bytes: null, TargetHeight: boxH));

        Assert.Equal((w, h, down), (decoded.PixelWidth, decoded.PixelHeight, decoded.Downscaled));
        Assert.Equal((origW, origH), (decoded.OriginalWidth, decoded.OriginalHeight));
    }

    [Theory]
    [MemberData(nameof(SizeCases))]
    public void Decode_FromPreReadBytes_SameDecisionsAsFromTheFile(
        string backend, string file, int boxW, int boxH, int w, int h, bool down, int origW, int origH)
    {
        var bytes = File.ReadAllBytes(PathOf(file));

        var decoded = Create(backend).Decode(new DecodeRequest(PathOf(file), new DecodeBox(boxW, boxH), bytes: bytes));

        Assert.Equal((w, h, down), (decoded.PixelWidth, decoded.PixelHeight, decoded.Downscaled));
        Assert.Equal((origW, origH), (decoded.OriginalWidth, decoded.OriginalHeight));
    }

    /// <summary>EXIF 6 (rotate 90): the box applies to the DISPLAYED size, the original size is reported swapped.</summary>
    private static readonly (string File, int BoxW, int BoxH, int W, int H, bool Down, int OrigW, int OrigH)[] RotatedCases =
    [
        // Stored 64x48 -> displayed 48x64.
        ("rot6", 48, 64, 48, 64, false, 48, 64),
        ("rot6", 480, 640, 48, 64, false, 48, 64),
        ("rot6", 0, 64, 48, 64, false, 48, 64),
        ("rot6", 48, 0, 48, 64, false, 48, 64),
        ("rot6", 47, 0, 47, 62, true, 48, 64),
        ("rot6", 0, 63, 47, 63, true, 48, 64),
        ("rot6", 480, 63, 47, 63, true, 48, 64),
        ("rot6", 24, 32, 24, 32, true, 48, 64),
        // Stored 64x1 -> displayed 1x64: only one stored axis shrinks.
        ("rot6flat", 0, 63, 1, 63, true, 1, 64),
        ("rot6flat", 480, 63, 1, 63, true, 1, 64),
        ("rot6flat", 0, 64, 1, 64, false, 1, 64),
        ("rot6flat", 1, 640, 1, 64, false, 1, 64),
    ];

    public static TheoryData<string, string, int, int, int, int, bool, int, int> RotatedSizeCases()
    {
        var data = new TheoryData<string, string, int, int, int, int, bool, int, int>();
        foreach (var backend in Backends)
            foreach (var c in RotatedCases)
                data.Add(backend, c.File, c.BoxW, c.BoxH, c.W, c.H, c.Down, c.OrigW, c.OrigH);
        return data;
    }

    [Theory]
    [MemberData(nameof(RotatedSizeCases))]
    public void Decode_RotatedSource_BoxAppliesToDisplayedSizeAndOriginalIsSwapped(
        string backend, string file, int boxW, int boxH, int w, int h, bool down, int origW, int origH)
    {
        var decoded = Create(backend).Decode(new DecodeRequest(PathOf(file), new DecodeBox(boxW, boxH)));

        Assert.Equal((w, h, down), (decoded.PixelWidth, decoded.PixelHeight, decoded.Downscaled));
        Assert.Equal((origW, origH), (decoded.OriginalWidth, decoded.OriginalHeight));
        Assert.Equal(6, decoded.Orientation);
    }

    [Theory]
    [InlineData("TurboJpeg")]
    [InlineData("WicDirect")]
    [InlineData("Wpf")]
    public void Decode_RotatedSourceWithoutApplyOrientation_KeepsStoredSizeAndReportsOrientationOne(string backend)
    {
        var decoded = Create(backend).Decode(new DecodeRequest(_src.Rotated6, 0, ApplyOrientation: false));

        Assert.Equal((64, 48), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal((64, 48), (decoded.OriginalWidth, decoded.OriginalHeight));
        Assert.Equal(1, decoded.Orientation);
        Assert.False(decoded.Downscaled);
    }

    [Theory]
    [InlineData("TurboJpeg")]
    [InlineData("WicDirect")]
    [InlineData("Wpf")]
    public void Decode_RotatedSourceWithBoxButWithoutApplyOrientation_BoxFitsTheStoredGrid(string backend)
    {
        // Stored 64x48: with orientation ignored the box 32x1000 constrains the STORED width (a transposed fit would give 24x32 -> 18 wide).
        var decoded = Create(backend).Decode(new DecodeRequest(_src.Rotated6, new DecodeBox(32, 1000), applyOrientation: false));

        Assert.Equal((32, 24), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal((64, 48), (decoded.OriginalWidth, decoded.OriginalHeight));
        Assert.Equal(1, decoded.Orientation);
        Assert.True(decoded.Downscaled);
    }

    [Theory]
    [InlineData("TurboJpeg")]
    [InlineData("WicDirect")]
    [InlineData("Wpf")]
    public void Decode_SourceOrientationWithoutApplyOrientation_IsIgnored(string backend)
    {
        // Unrotated file, caller-supplied orientation 6, but orientation is switched off: stored pixels, reported orientation 1.
        var decoded = Create(backend).Decode(
            new DecodeRequest(_src.Plain, 0, ApplyOrientation: false, SourceOrientation: 6));

        Assert.Equal((64, 48), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal(1, decoded.Orientation);
        Assert.Equal((64, 48), (decoded.OriginalWidth, decoded.OriginalHeight));
    }

    [Theory]
    [InlineData("TurboJpeg")]
    [InlineData("WicDirect")]
    [InlineData("Wpf")]
    public void Decode_SourceOrientationWithApplyOrientation_RotatesAndSwapsTheOriginal(string backend)
    {
        var decoded = Create(backend).Decode(new DecodeRequest(_src.Plain, 0, ApplyOrientation: true, SourceOrientation: 6));

        Assert.Equal((48, 64), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal(6, decoded.Orientation);
        Assert.Equal((48, 64), (decoded.OriginalWidth, decoded.OriginalHeight));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(5, 7, true)]
    [InlineData(-1, 0, false)]
    [InlineData(0, -1, false)]
    [InlineData(-1, -1, false)]
    [InlineData(-3, 5, true)]
    [InlineData(5, -3, true)]
    public void IsDownscaleRequested_EitherAxisPositive_IsTrue(int width, int height, bool expected)
    {
        var request = new DecodeRequest("x.jpg", width, TargetHeight: height);

        Assert.Equal(expected, request.IsDownscaleRequested);
    }
}