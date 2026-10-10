using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// Audit C-03 (2026-10-10): the defaults that shape RAM use, disk cache and preload were only compared with the very constants
/// that define them (tautology), so changing a default (reserve 2 GiB to 1 GiB, disk cache 4 GiB to 8 GiB, preload memory limit
/// 0.90 to 0.95, source-bytes cache off to on, keyboard zoom step 10 to 15) left every test green. These literals are the SHIPPED
/// defaults (AGENTS.md priorities: RAM, disk reads, review speed); changing one is a product decision and must change this file.
/// </summary>
public sealed class SettingsDefaultsLiteralTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact(DisplayName = "shipped performance defaults are pinned by literal values")]
    public void PerformanceDefaults_AreTheShippedLiterals()
    {
        var defaults = new AppSettings();

        Assert.Equal(16 * GiB, defaults.ImageCacheCapacityBytes);
        Assert.Equal(50, defaults.ImageCacheRamPercent);
        Assert.Equal(2 * GiB, defaults.MemoryReserveBytes);
        Assert.Equal(8, defaults.PreloadWorkerCount);
        Assert.Equal(0.9, defaults.PreloadMemoryLoadLimit);
        Assert.Equal(4 * GiB, defaults.PreviewDiskCacheCapacityBytes);
        Assert.False(defaults.UseSourceBytesCache);
        Assert.Equal(16 * GiB, defaults.SourceBytesCapacityBytes);
        Assert.Equal(32, defaults.PreloadForwardCount);
        Assert.Equal(8, defaults.PreloadBackwardCount);
    }

    [Fact(DisplayName = "shipped viewing/input defaults are pinned by literal values")]
    public void ViewingDefaults_AreTheShippedLiterals()
    {
        var defaults = new AppSettings();

        Assert.Equal(10, defaults.KeyboardZoomStepPercent);
        Assert.Equal(10, defaults.ArrowPanStepPercent);
        Assert.Equal(100, defaults.ClickZoomPercent);
        Assert.False(defaults.ClickToZoomEnabled);
        Assert.False(defaults.ClickZoomKeyTogglesFit);
        Assert.False(defaults.KineticPanEnabled);
        Assert.False(defaults.KeepZoomAcrossImages);
        Assert.False(defaults.ConfirmBeforeDelete);
        Assert.False(defaults.AllowPermanentDeleteWithoutRecycleBin);
        Assert.True(defaults.RawSupportEnabled);
        Assert.True(defaults.WebpHeicSupportEnabled);
        Assert.True(defaults.TouchpadSwipeEnabled);
        Assert.Equal(200, defaults.TouchpadSwipeDistancePerImage);
        Assert.Equal(FitWidthAnchor.Centre, defaults.FitWidthAnchor);
        Assert.Equal(FitWidthAnchor.BottomThird, defaults.FitWidthAnchor2);
    }
}
