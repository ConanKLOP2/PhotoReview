
namespace PhotoReview.Core.Tests.Settings;

/// <summary>F-IMG-4: 0 means "preview disk cache off" and must survive normalisation; only negatives are repaired.</summary>
[Trait("Category", "HotPath")]
public sealed class PreviewDiskCacheCapacitySettingsTests
{
    [Fact(DisplayName = "A preview disk cache capacity of 0 is kept by the normalizer (it means off)")]
    public void ZeroCapacityIsNotReplacedByDefault()
    {
        var settings = new AppSettings { PreviewDiskCacheCapacityBytes = 0 };

        var repairs = SettingsNormalizer.Normalize(settings);

        Assert.Equal(0, settings.PreviewDiskCacheCapacityBytes);
        Assert.DoesNotContain(nameof(AppSettings.PreviewDiskCacheCapacityBytes), repairs);
    }

    [Fact(DisplayName = "A negative preview disk cache capacity is repaired to the default")]
    public void NegativeCapacityFallsBackToDefault()
    {
        var settings = new AppSettings { PreviewDiskCacheCapacityBytes = -1 };

        SettingsNormalizer.Normalize(settings);

        Assert.Equal(PerformanceOptions.PreviewDiskCacheCapacityBytes, settings.PreviewDiskCacheCapacityBytes);
    }
}
