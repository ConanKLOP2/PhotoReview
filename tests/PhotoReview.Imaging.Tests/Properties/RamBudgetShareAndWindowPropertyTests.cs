using PhotoReview.Core.Settings;

namespace PhotoReview.Imaging.Tests.Properties;

/// <summary>
/// Share and window-monotonicity properties of <see cref="RamBudgetPolicy"/> / <see cref="PreloadWindow"/> over random RAM sizes,
/// requests and windows: no budget may exceed its documented share of physical RAM, and a bigger window never lowers the
/// floor while more RAM never raises it.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class RamBudgetShareAndWindowPropertyTests
{
    private static long Physical(Random rng) => rng.Next(5) switch
    {
        0 => rng.NextInt64(1, 1L << 20),
        1 => rng.NextInt64(1L << 28, 1L << 36),
        2 => 32L * 1024 * 1024 * 1024,
        3 => rng.NextInt64(1L << 36, 1L << 50),
        _ => 8L * 1024 * 1024 * 1024,
    };

    private static PreloadWindow Window(Random rng) =>
        new(rng.Next(PerformanceOptions.MinPreloadForwardCount, PerformanceOptions.MaxPreloadCount + 1),
            rng.Next(PerformanceOptions.MinPreloadBackwardCount, PerformanceOptions.MaxPreloadCount + 1));

    [Fact(DisplayName = "No clamp ever returns more than its share of physical RAM (50% overall, 20% source bytes) or more than was asked")]
    public void Clamps_NeverExceedTheirShare()
    {
        PropertyRunner.Check("RamBudgetPolicy shares", iterations: 20_000, (rng, _) =>
        {
            var physical = Physical(rng);
            var requested = PropertyRunner.EdgyLong(rng);
            var source = PropertyRunner.EdgyLong(rng);
            var half = (long)(physical * RamBudgetPolicy.MaxPhysicalMemoryShare);
            var fifth = (long)(physical * RamBudgetPolicy.MaxSourceBytesShare);

            var overall = RamBudgetPolicy.ClampToPhysicalMemory(requested, physical);
            Assert.True(overall <= half && overall <= requested, $"overall {overall} (phys {physical}, req {requested})");

            var src = RamBudgetPolicy.ClampSourceBytesToPhysicalMemory(requested, physical);
            Assert.True(src <= fifth && src <= overall, $"source {src} (phys {physical}, req {requested})");

            var preview = RamBudgetPolicy.ClampPreviewToPhysicalMemory(requested, physical, source);
            Assert.True(preview <= overall, "preview above overall clamp");
            Assert.True(preview <= Math.Max(0, half - Math.Max(0, source)), $"preview {preview} + source {source} over half of {physical}");
        });
    }

    [Fact(DisplayName = "Percent budgets: source + minimum preview window fit the percent share, and the preview budget is >= 1 and within the share")]
    public void PercentBudgets_FitTheirShare()
    {
        PropertyRunner.Check("RamBudgetPolicy percent budgets", iterations: 20_000, (rng, _) =>
        {
            var physical = Physical(rng);
            var window = Window(rng);
            var percent = rng.Next(-20, 200);
            var requestedSource = PropertyRunner.EdgyLong(rng);

            var cachePercent = RamBudgetPolicy.ClampCachePercent(percent, physical, window);
            var share = RamBudgetPolicy.BytesForPercent(cachePercent, physical);

            var source = RamBudgetPolicy.SourceBytesForPercent(requestedSource, percent, physical, window);
            if (source > 0)
            {
                Assert.True(source + RamBudgetPolicy.MinimumPreviewWindowBytes(window) <= share, $"source {source} leaves no room for the window in {share}");
                Assert.True(source <= (long)(physical * RamBudgetPolicy.MaxSourceBytesShare));
            }

            var preview = RamBudgetPolicy.PreviewBytesForPercent(percent, physical, Math.Max(0, source), window);
            Assert.True(preview >= 1);
            Assert.True(preview <= Math.Max(1, share), $"preview {preview} above share {share}");
            if (share > Math.Max(0, source)) Assert.True(preview + Math.Max(0, source) <= share, "preview + source exceed the percent share");
        });
    }

    [Fact(DisplayName = "MinimumCachePercent is monotonic: bigger window never lowers it, more RAM never raises it, always within [min, max]")]
    public void MinimumCachePercent_IsMonotonic()
    {
        PropertyRunner.Check("RamBudgetPolicy minimum percent monotonic", iterations: 20_000, (rng, _) =>
        {
            var physical = Physical(rng);
            var window = Window(rng);
            var bigger = new PreloadWindow(window.Forward + rng.Next(0, 50), window.Backward + rng.Next(0, 50));

            var at = RamBudgetPolicy.MinimumCachePercent(physical, window);
            Assert.InRange(at, PerformanceOptions.MinImageCacheRamPercent, PerformanceOptions.MaxImageCacheRamPercent);
            if (at < PerformanceOptions.MaxImageCacheRamPercent)
                Assert.True(RamBudgetPolicy.BytesForPercent(at, physical) >= RamBudgetPolicy.MinimumPreviewWindowBytes(window), "the floor percent cannot hold the window");
            Assert.True(RamBudgetPolicy.MinimumCachePercent(physical, bigger) >= at, "bigger window lowered the floor");
            Assert.True(RamBudgetPolicy.MinimumCachePercent(physical + rng.NextInt64(0, physical), window) <= at, "more RAM raised the floor");
        });
    }

    [Fact(DisplayName = "PreloadWindow.FromSettings of normalised settings stays in the configured bounds and ImageCount = forward + backward + 1")]
    public void PreloadWindow_FromNormalisedSettings_IsBounded()
    {
        PropertyRunner.Check("PreloadWindow.FromSettings", iterations: 2000, (rng, _) =>
        {
            var settings = new AppSettings
            {
                PreloadForwardCount = (int)Math.Clamp(PropertyRunner.EdgyLong(rng), int.MinValue, int.MaxValue),
                PreloadBackwardCount = (int)Math.Clamp(PropertyRunner.EdgyLong(rng), int.MinValue, int.MaxValue),
            };
            SettingsNormalizer.Normalize(settings);

            var window = PreloadWindow.FromSettings(settings);

            Assert.InRange(window.Forward, PerformanceOptions.MinPreloadForwardCount, PerformanceOptions.MaxPreloadCount);
            Assert.InRange(window.Backward, PerformanceOptions.MinPreloadBackwardCount, PerformanceOptions.MaxPreloadCount);
            Assert.Equal(window.Forward + window.Backward + 1, window.ImageCount);
        });
    }
}
