using PhotoReview.App.ViewModels;
using PhotoReview.Core.Settings;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>Q-R45: the on-image zoom HUD (<see cref="InfoOverlayViewModel.IsZoomIndicatorVisible"/>/<see cref="InfoOverlayViewModel.ZoomIndicatorText"/>).</summary>
[Trait("Category", "HotPath")]
public sealed class InfoOverlayZoomIndicatorTests
{
    [Fact]
    public void IsZoomIndicatorVisible_False_WhenSettingOff_EvenWithImage()
    {
        var settings = new AppSettings { ShowZoomIndicator = false };
        var overlay = new InfoOverlayViewModel(() => settings, (_, _) => default, hasImage: () => true, getZoomPercent: () => 100);

        Assert.False(overlay.IsZoomIndicatorVisible);
    }

    [Fact]
    public void IsZoomIndicatorVisible_False_WhenNoImage_EvenWithSettingOn()
    {
        var settings = new AppSettings { ShowZoomIndicator = true };
        var overlay = new InfoOverlayViewModel(() => settings, (_, _) => default, hasImage: () => false, getZoomPercent: () => 100);

        Assert.False(overlay.IsZoomIndicatorVisible);
    }

    [Fact]
    public void IsZoomIndicatorVisible_True_WhenSettingOnAndImageShown()
    {
        var settings = new AppSettings { ShowZoomIndicator = true };
        var overlay = new InfoOverlayViewModel(() => settings, (_, _) => default, hasImage: () => true, getZoomPercent: () => 100);

        Assert.True(overlay.IsZoomIndicatorVisible);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(37)]
    [InlineData(400)]
    public void ZoomIndicatorText_FormatsWholePercent(int percent)
    {
        var settings = new AppSettings { ShowZoomIndicator = true };
        var overlay = new InfoOverlayViewModel(() => settings, (_, _) => default, hasImage: () => true, getZoomPercent: () => percent);

        Assert.Equal($"{percent}%", overlay.ZoomIndicatorText);
    }

    [Fact]
    public void DisplayZoomPercent_Fit_ReadsKnownFitZoom()
    {
        var state = new ViewerState();
        state.SetSourceSize(1000, 1000);
        state.UpdateViewport(500, 500, force: true); // FitZoom = 500/1000 = 0.5 -> 50 %
        state.ResetFit(500, 500);

        Assert.True(state.IsFit);
        Assert.Equal(50, state.DisplayZoomPercent);
    }

    [Fact]
    public void DisplayZoomPercent_PlainZoom_ReadsZoomDirectly()
    {
        var state = new ViewerState();
        state.SetZoom(1.5);

        Assert.Equal(150, state.DisplayZoomPercent);
    }

    [Fact]
    public void DisplayZoomPercent_FitWithUnknownFitZoom_FallsBackTo100()
    {
        var state = new ViewerState(); // no source size/viewport known: FitZoom is 0

        Assert.True(state.IsFit);
        Assert.Equal(100, state.DisplayZoomPercent);
    }
}
