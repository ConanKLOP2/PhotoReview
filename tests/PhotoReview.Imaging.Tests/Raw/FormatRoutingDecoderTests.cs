using System.IO;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Raw;

namespace PhotoReview.Imaging.Tests.Raw;

public sealed class FormatRoutingDecoderTests
{
    [Theory]
    [InlineData("photo.cr2")]
    [InlineData("PHOTO.CR2")]
    [InlineData("photo.Dng")]
    [InlineData(@"C:\raw dir\photo.NEF")]
    public void Routing_RawExtensionWhenEnabled_UsesRawDecoderRegardlessOfCase(string path)
    {
        var (router, standard, raw, _) = Create(rawEnabled: true);

        router.ReadInfo(path);
        router.Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        Assert.Equal((0, 0), (standard.ReadInfoCount, standard.DecodeCount));
        Assert.Equal((1, 1), (raw.ReadInfoCount, raw.DecodeCount));
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("photo.PNG")]
    [InlineData("photo")]
    [InlineData("photo.nrw")]
    [InlineData(@"C:\folder.cr2\photo.jpg")]
    public void Routing_NonRawPath_UsesStandardDecoderWhetherRawIsEnabledOrNot(string path)
    {
        foreach (var enabled in new[] { true, false })
        {
            var (router, standard, raw, _) = Create(enabled);

            router.ReadInfo(path);
            router.Decode(new DecodeRequest(path, DecodeBox.Unbounded));

            Assert.Equal((1, 1), (standard.ReadInfoCount, standard.DecodeCount));
            Assert.Equal((0, 0), (raw.ReadInfoCount, raw.DecodeCount));
        }
    }

    [Theory]
    [InlineData("photo.cr2")]
    [InlineData("photo.DNG")]
    public void Routing_RawExtensionWhenDisabled_ThrowsNotSupportedWithoutTouchingAnyDecoder(string path)
    {
        var (router, standard, raw, _) = Create(rawEnabled: false);

        var info = Assert.Throws<NotSupportedException>(() => router.ReadInfo(path));
        var decode = Assert.Throws<NotSupportedException>(() => router.Decode(new DecodeRequest(path, DecodeBox.Unbounded)));

        Assert.Contains("RAW support is disabled", info.Message, StringComparison.Ordinal);
        Assert.Contains("RAW support is disabled", decode.Message, StringComparison.Ordinal);
        Assert.Equal((0, 0), (standard.ReadInfoCount, standard.DecodeCount));
        Assert.Equal((0, 0), (raw.ReadInfoCount, raw.DecodeCount));
    }

    [Fact]
    public void Routing_SettingToggledAtRuntime_IsReevaluatedOnEveryCall()
    {
        var (router, _, raw, state) = Create(rawEnabled: false);

        Assert.Throws<NotSupportedException>(() => router.ReadInfo("photo.arw"));
        state.Enabled = true;
        router.ReadInfo("photo.arw");
        state.Enabled = false;
        Assert.Throws<NotSupportedException>(() => router.ReadInfo("photo.arw"));

        Assert.Equal(1, raw.ReadInfoCount);
    }

    [Fact]
    public void ReadInfo_BlankPath_ThrowsArgumentException()
    {
        var (router, _, _, _) = Create(rawEnabled: true);

        Assert.ThrowsAny<ArgumentException>(() => router.ReadInfo(" "));
    }

    // Needs the real corpus, so it is Native: strict CI (PHOTOREVIEW_RAW_CORPUS_STRICT=1) fails when a sample is missing instead of passing vacuously.
    [Theory]
    [Trait("Category", "Native")]
    [InlineData("Leica - M8 - 8bit 8bit uncompressed (3_2).DNG")]
    [InlineData("Canon - EOS 350D - RAW (3_2).CR2")]
    public void Routing_RealRawFileWithWpfChainWhenDisabled_FailsInsteadOfDecodingAThumbnail(string fileName)
    {
        if (RawCorpus.TryGetFile(fileName) is not { } path) return;
        var wpf = new WpfBitmapImageDecoder();
        var router = new FormatRoutingDecoder(wpf, new RoutingProbeDecoder(), () => false);

        // Without the router guard WPF decodes the DNG's 320x240 IFD0 thumbnail as if it were the photo.
        Assert.Throws<NotSupportedException>(() => router.Decode(new DecodeRequest(path, DecodeBox.Unbounded)));
        Assert.Throws<NotSupportedException>(() => router.ReadInfo(path));
    }

    private static (FormatRoutingDecoder Router, RoutingProbeDecoder Standard, RoutingProbeDecoder Raw, EnabledState State) Create(bool rawEnabled)
    {
        var standard = new RoutingProbeDecoder();
        var raw = new RoutingProbeDecoder();
        var state = new EnabledState { Enabled = rawEnabled };
        return (new FormatRoutingDecoder(standard, raw, () => state.Enabled), standard, raw, state);
    }

    private sealed class EnabledState
    {
        internal bool Enabled { get; set; }
    }

    private sealed class RoutingProbeDecoder : IImageDecoder
    {
        internal int ReadInfoCount { get; private set; }
        internal int DecodeCount { get; private set; }

        public ImageInfo ReadInfo(string path)
        {
            ReadInfoCount++;
            return new ImageInfo(1, 1, 1);
        }

        public IDecodedImage Decode(DecodeRequest request)
        {
            DecodeCount++;
            return new WpfDecodedImage(System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
                System.Windows.Media.PixelFormats.Bgr32, null, new byte[4], 4), false);
        }
    }
}
