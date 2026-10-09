using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Localization;
using PhotoReview.Imaging.Decoding.Wic;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>Q-FMT-WEBP-HEIC routing: WebP/HEIC to the WIC chain, refusals before any read, one log line per missing codec.</summary>
[Collection("UiLanguage")] // the Localizer is process-wide (see DecoderErrorLocalizationTests)
[Trait("Category", "HotPath")]
public sealed class WebpHeicRoutingDecoderTests
{
    private static string InEnglish(Exception ex)
    {
        using var _ = TestLocalization.Use(TestLocalization.English);
        return UserFacingError.Describe(ex);
    }

    private static string InVietnamese(Exception ex)
    {
        using var _ = TestLocalization.Use(TestLocalization.Vietnamese);
        return UserFacingError.Describe(ex);
    }

    private static readonly WicCodecSupport AllCodecs = new(WebP: true, HeifContainer: true, HevcDecoder: true, "test");

    private readonly RecordingDecoder _inner = new();
    private readonly RecordingDecoder _wic = new();
    private readonly RecordingLog _log = new();
    private bool _enabled = true;
    private WicCodecSupport _codecs = AllCodecs;
    private int _probeCalls;

    private WebpHeicRoutingDecoder CreateRouter() =>
        new(_inner, _wic, () => _enabled, () => { _probeCalls++; return _codecs; }, _log);

    [Theory]
    [InlineData(@"C:\p\a.webp")]
    [InlineData(@"C:\p\a.WEBP")]
    [InlineData(@"C:\p\IMG_0001.HEIC")]
    [InlineData(@"C:\p\a.heif")]
    public void WebpAndHeic_GoToTheWicChain_ForDecodeAndReadInfo(string path)
    {
        var router = CreateRouter();

        router.Decode(new DecodeRequest(path, TargetWidth: 0));
        router.ReadInfo(path);

        Assert.Equal([path, path], _wic.Paths);
        Assert.Empty(_inner.Paths);
    }

    [Theory]
    [InlineData(@"C:\p\a.jpg")]
    [InlineData(@"C:\p\a.png")]
    [InlineData(@"C:\p\a.cr2")]
    [InlineData(@"C:\p\a.avif")]
    public void OtherFormats_GoToTheInnerChain_WithoutRunningTheCodecProbe(string path)
    {
        _enabled = false; // the switch only concerns WebP/HEIC
        _codecs = WicCodecSupport.None("none");
        var router = CreateRouter();

        router.Decode(new DecodeRequest(path, TargetWidth: 0));
        router.ReadInfo(path);

        Assert.Equal([path, path], _inner.Paths);
        Assert.Empty(_wic.Paths);
        Assert.Equal(0, _probeCalls); // a JPEG-only session never pays for the WIC/MF enumeration
    }

    [Fact]
    public void SwitchOff_RefusesWithALocalizedNotSupportedException_BeforeAnyDecoderRuns()
    {
        _enabled = false;
        var router = CreateRouter();

        var decodeError = Assert.Throws<NotSupportedException>(() => router.Decode(new DecodeRequest(@"C:\p\a.webp", TargetWidth: 0)));
        var infoError = Assert.Throws<NotSupportedException>(() => router.ReadInfo(@"C:\p\b.heic"));

        Assert.IsNotType<MissingImageCodecException>(decodeError);
        Assert.True(UserFacingError.IsLocalized(decodeError));
        Assert.Contains("a.webp", InEnglish(decodeError), StringComparison.Ordinal);
        Assert.Contains("b.heic", InEnglish(infoError), StringComparison.Ordinal);
        Assert.Empty(_wic.Paths);
        Assert.Empty(_inner.Paths);
        Assert.Equal(0, _probeCalls);
    }

    [Fact]
    public void HeifWithoutHevc_RefusesWithInstallGuidance_AndLogsOnceForManyFiles()
    {
        _codecs = new WicCodecSupport(WebP: true, HeifContainer: true, HevcDecoder: false, "probe-detail");
        var router = CreateRouter();

        var errors = Enumerable.Range(1, 3)
            .Select(i => Assert.Throws<MissingImageCodecException>(() => router.Decode(new DecodeRequest($@"C:\p\IMG_{i}.HEIC", TargetWidth: 0))))
            .ToList();
        Assert.Throws<MissingImageCodecException>(() => router.ReadInfo(@"C:\p\IMG_9.heif"));

        var text = InEnglish(errors[0]);
        Assert.Contains("IMG_1.HEIC", text, StringComparison.Ordinal);
        Assert.Contains("HEIF Image Extensions", text, StringComparison.Ordinal);
        Assert.Contains("HEVC Video Extensions", text, StringComparison.Ordinal);
        var vietnamese = InVietnamese(errors[0]);
        Assert.Contains("Microsoft Store", vietnamese, StringComparison.Ordinal);
        Assert.NotEqual(text, vietnamese); // translated, not the English fallback
        Assert.Contains("IMG_1.HEIC", errors[0].Message, StringComparison.Ordinal); // English log text names the file too
        Assert.Empty(_wic.Paths);
        var warning = Assert.Single(_log.Warnings);
        Assert.Contains("probe-detail", warning, StringComparison.Ordinal);

        router.Decode(new DecodeRequest(@"C:\p\still.webp", TargetWidth: 0)); // WebP is unaffected by the missing HEVC decoder
        Assert.Equal([@"C:\p\still.webp"], _wic.Paths);
    }

    [Fact]
    public void WebpCodecMissing_RefusesWithWebpGuidance_LoggedSeparatelyFromHeif()
    {
        _codecs = WicCodecSupport.None("nothing");
        var router = CreateRouter();

        var webpError = Assert.Throws<MissingImageCodecException>(() => router.Decode(new DecodeRequest(@"C:\p\a.webp", TargetWidth: 0)));
        Assert.Throws<MissingImageCodecException>(() => router.Decode(new DecodeRequest(@"C:\p\b.webp", TargetWidth: 0)));
        Assert.Throws<MissingImageCodecException>(() => router.Decode(new DecodeRequest(@"C:\p\c.heic", TargetWidth: 0)));

        Assert.Contains("WebP Image Extensions", InEnglish(webpError), StringComparison.Ordinal);
        Assert.DoesNotContain("HEVC", InEnglish(webpError), StringComparison.Ordinal);
        Assert.Equal(2, _log.Warnings.Count);
        Assert.Contains(_log.Warnings, w => w.Contains("WebP", StringComparison.Ordinal));
        Assert.Contains(_log.Warnings, w => w.Contains("HEIC", StringComparison.Ordinal));
    }

    [Fact]
    public void SwitchIsReadOnEveryCall_SoASettingsChangeAppliesAtOnce()
    {
        var router = CreateRouter();
        router.Decode(new DecodeRequest(@"C:\p\a.webp", TargetWidth: 0));

        _enabled = false;

        Assert.Throws<NotSupportedException>(() => router.Decode(new DecodeRequest(@"C:\p\a.webp", TargetWidth: 0)));
        Assert.Single(_wic.Paths);
    }

    [Theory]
    [InlineData(@"C:\p\a.jpg", false, false, false)]
    [InlineData(@"C:\p\a.webp", true, true, false)]
    [InlineData(@"C:\p\a.webp", false, true, true)]
    [InlineData(@"C:\p\a.webp", true, false, true)]
    [InlineData(@"C:\p\a.heic", true, true, true)] // WebP codec present but HEIF needs its own
    [InlineData(null, false, false, false)]
    public void WillRefuse_MatchesWhatDecodeWouldDo(string? path, bool enabled, bool webpCodec, bool expected)
    {
        var codecs = new WicCodecSupport(WebP: webpCodec, HeifContainer: false, HevcDecoder: false, "t");

        Assert.Equal(expected, WebpHeicRoutingDecoder.WillRefuse(path, () => enabled, () => codecs));
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new WebpHeicRoutingDecoder(null!, _wic, () => true, () => AllCodecs));
        Assert.Throws<ArgumentNullException>(() => new WebpHeicRoutingDecoder(_inner, null!, () => true, () => AllCodecs));
        Assert.Throws<ArgumentNullException>(() => new WebpHeicRoutingDecoder(_inner, _wic, null!, () => AllCodecs));
        Assert.Throws<ArgumentNullException>(() => new WebpHeicRoutingDecoder(_inner, _wic, () => true, null!));
    }

    private sealed class RecordingDecoder : IImageDecoder
    {
        public List<string> Paths { get; } = [];

        public IDecodedImage Decode(DecodeRequest request)
        {
            Paths.Add(request.Path);
            return null!;
        }

        public ImageInfo ReadInfo(string path)
        {
            Paths.Add(path);
            return new ImageInfo(1, 1, 1);
        }
    }

    private sealed class RecordingLog : ILog
    {
        public List<string> Warnings { get; } = [];
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? ex = null) { }
    }
}
