using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// <see cref="DecoderBackend.LibRaw"/> is registered internally for RAW files only; it is not a value the Settings combo can
/// show. A persisted 3 would silently make every JPEG decode through LibRaw while the combo displays "WPF".
/// </summary>
[Trait("Category", "HotPath")]
public sealed class DecoderBackendNormalizationTests
{
    [Fact]
    public void Normalize_InternalOnlyLibRawBackend_IsRepairedToTheDefaultAndReported()
    {
        var settings = new AppSettings { DecoderBackend = DecoderBackend.LibRaw };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(new AppSettings().DecoderBackend, settings.DecoderBackend);
        Assert.Contains(nameof(AppSettings.DecoderBackend), repairs);
    }

    [Theory]
    [InlineData(DecoderBackend.Wpf)]
    [InlineData(DecoderBackend.WicDirect)]
    [InlineData(DecoderBackend.TurboJpeg)]
    public void Normalize_UserSelectableBackend_IsKept(DecoderBackend backend)
    {
        var settings = new AppSettings { DecoderBackend = backend };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(backend, settings.DecoderBackend);
        Assert.DoesNotContain(nameof(AppSettings.DecoderBackend), repairs);
    }
}
