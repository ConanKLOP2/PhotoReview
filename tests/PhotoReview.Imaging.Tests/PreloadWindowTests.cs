using PhotoReview.Core.Settings;

namespace PhotoReview.Imaging.Tests;

/// <summary>
/// feat/preload-window-setting: <see cref="PreloadWindow"/> itself, plus the pure
/// <see cref="PreloadWindow.FromSettings"/> composition helper (extracted so App.xaml.cs's use of it is
/// covered without a WPF composition-root test -- mirrors the existing PreloadWorkerCount plumbing).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreloadWindowTests
{
    [Fact]
    public void Default_MatchesTheHistoricalConstants()
    {
        var window = PreloadWindow.Default;

        Assert.Equal(PreloadOrderService.ForwardLookahead, window.Forward);
        Assert.Equal(PreloadOrderService.BackwardLookahead, window.Backward);
        Assert.Equal(41, window.ImageCount);
    }

    [Theory]
    [InlineData(1, 0, 2)]
    [InlineData(3, 1, 5)]
    [InlineData(32, 8, 41)]
    [InlineData(500, 500, 1001)]
    public void ImageCount_IsForwardPlusBackwardPlusCurrent(int forward, int backward, int expected)
    {
        Assert.Equal(expected, new PreloadWindow(forward, backward).ImageCount);
    }

    [Fact]
    public void Create_RejectsForwardBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PreloadWindow.Create(0, 8));
    }

    [Fact]
    public void Create_RejectsNegativeBackward()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PreloadWindow.Create(32, -1));
    }

    [Fact]
    public void Create_AcceptsTheAllowedBoundaries()
    {
        var window = PreloadWindow.Create(1, 0);

        Assert.Equal(1, window.Forward);
        Assert.Equal(0, window.Backward);
    }

    [Fact]
    public void FromSettings_ReadsForwardAndBackwardFromAppSettings()
    {
        var settings = new AppSettings { PreloadForwardCount = 64, PreloadBackwardCount = 16 };

        var window = PreloadWindow.FromSettings(settings);

        Assert.Equal(64, window.Forward);
        Assert.Equal(16, window.Backward);
    }

    [Fact]
    public void FromSettings_Defaults_MatchPreloadWindowDefault()
    {
        var window = PreloadWindow.FromSettings(new AppSettings());

        Assert.Equal(PreloadWindow.Default, window);
    }

    [Fact]
    public void FromSettings_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PreloadWindow.FromSettings(null!));
    }
}
