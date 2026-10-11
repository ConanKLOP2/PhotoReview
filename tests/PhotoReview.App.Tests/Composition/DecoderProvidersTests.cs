using PhotoReview.App.Composition;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Composition;

/// <summary>A missing libraw.dll is an error only when RAW/LibRaw is actually wanted; otherwise it must not spam the log at every startup.</summary>
[Trait("Category", "HotPath")]
public sealed class DecoderProvidersTests
{
    private static bool MissingLibRaw(out string? reason)
    {
        reason = "libraw.dll not found";
        return false;
    }

    private static bool TurboJpegOrLibRawAvailable(out string? reason)
    {
        reason = null;
        return true;
    }

    /// <summary>Stub: the test never loads the real turbojpeg.dll nor lets a missing one write to the real startup log.</summary>
    private static bool TurboJpegAvailable(out string? reason)
    {
        reason = null;
        return true;
    }

    private static List<(DecoderBackend Backend, Func<PhotoReview.Imaging.Decoding.IImageDecoder> Factory)> Create(
        Func<bool>? needed, DecoderProviders.LibRawProbe probe, List<string> errors, List<string> infos) =>
        DecoderProviders.Create(PhysicalSourceReader.Instance, WpfBitmapSourceCodec.Instance, () => new WpfBitmapImageDecoder(PhysicalSourceReader.Instance), needed, probe, TurboJpegAvailable,
            (message, _) => errors.Add(message), infos.Add);

    [Fact]
    public void Create_TurboJpegMissing_LogsThroughTheInjectedLoggerAndDoesNotRegisterIt()
    {
        var errors = new List<string>();
        var providers = DecoderProviders.Create(PhysicalSourceReader.Instance, WpfBitmapSourceCodec.Instance, () => new WpfBitmapImageDecoder(PhysicalSourceReader.Instance), () => false, TurboJpegOrLibRawAvailable,
            (out string? reason) => { reason = "turbojpeg.dll not found"; return false; },
            (message, _) => errors.Add(message), _ => { });

        Assert.Contains(errors, message => message.Contains("TurboJPEG", StringComparison.Ordinal));
        Assert.DoesNotContain(providers, provider => provider.Backend == DecoderBackend.TurboJpeg);
    }

    [Fact]
    public void Create_LibRawMissingWhileRawSupportIsOff_LogsInfoNotAnError()
    {
        var errors = new List<string>();
        var infos = new List<string>();

        var providers = Create(() => false, MissingLibRaw, errors, infos);

        Assert.DoesNotContain(errors, message => message.Contains("LibRaw", StringComparison.Ordinal));
        Assert.Contains(infos, message => message.Contains("LibRaw", StringComparison.Ordinal));
        Assert.DoesNotContain(providers, provider => provider.Backend == DecoderBackend.LibRaw);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void Create_LibRawMissingWhileNeeded_LogsAnError(bool? needed)
    {
        var errors = new List<string>();
        var infos = new List<string>();

        Create(needed is null ? null : () => needed.Value, MissingLibRaw, errors, infos);

        Assert.Contains(errors, message => message.Contains("LibRaw", StringComparison.Ordinal));
        Assert.DoesNotContain(infos, message => message.Contains("LibRaw", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_LibRawAvailable_RegistersTheBackendWithoutLogging()
    {
        var errors = new List<string>();
        var infos = new List<string>();

        var providers = Create(() => false, TurboJpegOrLibRawAvailable, errors, infos);

        Assert.Contains(providers, provider => provider.Backend == DecoderBackend.LibRaw);
        Assert.DoesNotContain(errors, message => message.Contains("LibRaw", StringComparison.Ordinal));
    }
}
