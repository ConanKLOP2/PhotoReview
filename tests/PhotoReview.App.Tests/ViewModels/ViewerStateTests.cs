using PhotoReview.Core.Model;
using PhotoReview.App.ViewModels;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

[Trait("Category", "HotPath")]
public sealed class ViewerStateTests
{
    [Fact]
    public void ScalingQuality_DefaultsToHighQualityAndNotifiesOnChange()
    {
        var state = new ViewerState();
        var changed = new List<string?>();
        state.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal(ScalingQuality.HighQuality, state.ScalingQuality);
        state.ScalingQuality = ScalingQuality.Linear;

        Assert.Contains(nameof(ViewerState.ScalingQuality), changed);
    }

    [Fact]
    public void AppSettings_ScalingQualityDefaultsToHighQualityAndRoundTrips()
    {
        var settings = new AppSettings();
        Assert.Equal(ScalingQuality.HighQuality, settings.ScalingQuality);

        settings.ScalingQuality = ScalingQuality.Linear;
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(ScalingQuality.Linear, loaded.ScalingQuality);
    }

    [Fact]
    public void DefaultState_IsFitAndUniform()
    {
        var state = new ViewerState();

        Assert.Equal(1.0, state.Zoom);
        Assert.Equal(ViewerStretchMode.Uniform, state.Stretch);
        Assert.True(state.IsFit);
        Assert.False(state.IsFullscreen);
    }

    [Fact]
    public void SetZoom_ChangesModeToNone_ClampsBetweenMinAndMax()
    {
        var state = new ViewerState();

        state.SetZoom(2.0);
        Assert.Equal(2.0, state.Zoom);
        Assert.Equal(ViewerStretchMode.None, state.Stretch);
        Assert.False(state.IsFit);
        Assert.Equal(double.PositiveInfinity, state.MaxImageWidth);
        Assert.Equal(double.PositiveInfinity, state.MaxImageHeight);

        // feat/mouse-zoom: the absolute range is 10 %..800 % (click-to-zoom range); stepping stays 25 %..400 %.
        state.SetZoom(0.05);
        Assert.Equal(0.10, state.Zoom);

        state.SetZoom(10.0);
        Assert.Equal(8.0, state.Zoom);
    }

    [Theory]
    [InlineData(1.0, 0.25, 1.25)]
    [InlineData(3.9, 0.25, 4.0)]    // up: capped at the stepping max
    [InlineData(1.0, -0.25, 0.75)]
    [InlineData(0.3, -0.25, 0.25)]  // down: floored at the stepping min
    [InlineData(0.10, 0.25, 0.35)]  // a 10 % click zoom can step up
    [InlineData(8.0, -0.25, 4.0)]   // from an 800 % click zoom one step down lands on 400 %
    [InlineData(5.5, -0.25, 4.0)]
    public void CalculateStepZoom_StepsWithinTheSteppingRange(double current, double step, double expected)
    {
        Assert.Equal(expected, ViewerState.CalculateStepZoom(current, step)!.Value, 6);
    }

    [Theory]
    [InlineData(4.0, 0.25)]   // at the stepping max
    [InlineData(8.0, 0.25)]   // above it: zooming in must not shrink to 400 %
    [InlineData(0.25, -0.25)] // at the stepping min
    [InlineData(0.10, -0.25)] // below it: zooming out must not enlarge to 25 %
    public void CalculateStepZoom_NeverMovesAgainstTheStepDirection(double current, double step)
    {
        Assert.Null(ViewerState.CalculateStepZoom(current, step));
    }

    [Fact]
    public void ZoomIn_AboveStepRange_KeepsClickZoom()
    {
        var state = new ViewerState();
        state.SetZoom(8.0);

        state.ZoomIn();
        Assert.Equal(8.0, state.Zoom);

        state.ZoomOut();
        Assert.Equal(4.0, state.Zoom);
    }

    [Fact]
    public void ZoomIn_And_ZoomOut_StepCorrectly()
    {
        var state = new ViewerState();

        state.ZoomIn();
        Assert.Equal(1.25, state.Zoom);

        state.ZoomOut();
        Assert.Equal(1.0, state.Zoom);

        // Giảm liên tục kẹp ở 0.25
        for (var i = 0; i < 10; i++) state.ZoomOut();
        Assert.Equal(0.25, state.Zoom);

        // Tăng liên tục kẹp ở 4.0
        for (var i = 0; i < 20; i++) state.ZoomIn();
        Assert.Equal(4.0, state.Zoom);
    }

    [Fact]
    public void WheelZoom_PositiveAndNegative_StepsCorrectly()
    {
        var state = new ViewerState();

        state.WheelZoom(120);
        Assert.Equal(1.25, state.Zoom);

        state.WheelZoom(-120);
        Assert.Equal(1.0, state.Zoom);
    }

    [Fact]
    public void ResetFit_WithViewportRestoresViewportLimits()
    {
        var state = new ViewerState();
        state.SetZoom(3);

        state.ResetFit(1280, 720);

        Assert.True(state.IsFit);
        Assert.Equal(1280, state.MaxImageWidth);
        Assert.Equal(720, state.MaxImageHeight);
    }

    [Fact]
    public void UniformImagePoint_RemovesLetterboxAndMapsToSourceCoordinates()
    {
        var point = MainWindowHelpers.CalculateUniformImagePoint(1000, 800, 1000, 500, 500, 400);

        Assert.Equal(500, point.X, 6);
        Assert.Equal(250, point.Y, 6);
    }

    [Fact]
    public void AnchorDeltaOffsets_KeepSameImagePointAtPointer()
    {
        var result = MainWindowHelpers.CalculateOffsetsFromAnchorDelta(
            40, 30, 300, 200, 440, 290, 1600, 1200, 800, 600);

        Assert.Equal(180, result.Horizontal, 6);
        Assert.Equal(120, result.Vertical, 6);
    }

    [Theory]
    [InlineData(100, 80, 20, -15, 1000, 800, 400, 300, 80, 95)]
    [InlineData(0, 0, -1000, 1000, 1000, 800, 400, 300, 600, 0)]
    public void PanOffsets_MoveOppositeDragAndClampToExtent(
        double horizontal, double vertical, double deltaX, double deltaY,
        double extentWidth, double extentHeight, double viewportWidth, double viewportHeight,
        double expectedHorizontal, double expectedVertical)
    {
        var result = MainWindowHelpers.CalculatePanOffsets(horizontal, vertical, deltaX, deltaY,
            extentWidth, extentHeight, viewportWidth, viewportHeight);

        Assert.Equal(expectedHorizontal, result.Horizontal, 6);
        Assert.Equal(expectedVertical, result.Vertical, 6);
    }

    [Theory]
    [InlineData(0, 0, false)]      // click without drag
    [InlineData(3.9, -3.9, false)] // below threshold on both axes
    [InlineData(4, 0, true)]       // horizontal threshold is inclusive
    [InlineData(0, -4, true)]      // vertical threshold, negative direction
    [InlineData(-10, 2, true)]     // beyond on one axis only
    public void DragThreshold_IsPerAxisAbsoluteAndInclusive(double totalDeltaX, double totalDeltaY, bool expected)
    {
        Assert.Equal(expected, MainWindowHelpers.IsBeyondDragThreshold(totalDeltaX, totalDeltaY, 4, 4));
    }

    [Fact]
    public void ResetFit_RestoresUniformAndCalculatesViewport()
    {
        var state = new ViewerState();
        state.SetZoom(3.0);

        state.ResetFit(1920, 1080);

        Assert.Equal(1.0, state.Zoom);
        Assert.Equal(ViewerStretchMode.Uniform, state.Stretch);
        Assert.True(state.IsFit);
        Assert.Equal(1920, state.MaxImageWidth);
        Assert.Equal(1080, state.MaxImageHeight);
    }

    [Fact]
    public void UpdateViewport_OnlyWhenUniform()
    {
        var state = new ViewerState();
        state.ResetFit(1000, 800);

        // Khi đang ở chế độ Uniform: cập nhật kích thước viewport
        state.UpdateViewport(1200, 900);
        Assert.Equal(1200, state.MaxImageWidth);
        Assert.Equal(900, state.MaxImageHeight);

        // Chuyển sang Zoom tự do
        state.SetZoom(2.0);
        Assert.Equal(double.PositiveInfinity, state.MaxImageWidth);

        // Khi đang Zoom (Stretch == None): UpdateViewport không tác động
        state.UpdateViewport(1600, 1200);
        Assert.Equal(double.PositiveInfinity, state.MaxImageWidth);
        Assert.Equal(double.PositiveInfinity, state.MaxImageHeight);
    }

    [Theory]
    [InlineData(InitialViewMode.Fit, 1.0, ViewerStretchMode.Uniform)]
    [InlineData(InitialViewMode.Percent100, 1.0, ViewerStretchMode.None)]
    [InlineData(InitialViewMode.Percent200, 2.0, ViewerStretchMode.None)]
    // PR-B: 400 % was removed from the Settings combo; a legacy config value now behaves like 200 % (SettingsNormalizer
    // migrates it on load, but ViewerState itself treats a stray Percent400 the same way defensively).
    [InlineData(InitialViewMode.Percent400, 2.0, ViewerStretchMode.None)]
    public void ApplyInitialViewMode_ConfiguresStateCorrectly(InitialViewMode mode, double expectedZoom, ViewerStretchMode expectedStretch)
    {
        var state = new ViewerState();
        state.ApplyInitialViewMode(mode, 1920, 1080);

        Assert.Equal(expectedZoom, state.Zoom);
        Assert.Equal(expectedStretch, state.Stretch);
    }

    [Fact]
    public void ApplyInitialViewMode_FitWidth_ZoomsToFitWidthAfterRefreshingViewport()
    {
        var state = new ViewerState();
        state.SetSourceSize(2000, 4000); // tall portrait: width-fit != height-fit
        state.DpiScale = 1.0;

        var applied = state.ApplyInitialViewMode(InitialViewMode.FitWidth, 1000, 2000);

        Assert.True(applied);
        Assert.Equal(0.5, state.Zoom, 6); // 1000 / 2000 source width
        Assert.Equal(ViewerStretchMode.None, state.Stretch);
    }

    [Fact]
    public void SwapSourceSize_AfterFitWidthOnPreviousImageAndSizeChange_KeepsZoomInsteadOfRefittingAgainstStaleViewport()
    {
        var state = new ViewerState { DpiScale = 1.0 };
        state.SetSourceSize(2000, 4000);
        state.ApplyInitialViewMode(InitialViewMode.FitWidth, 1000, 2000);
        Assert.Equal(0.5, state.Zoom, 6);

        state.SetSourceSize(3000, 2000); // navigate to B (KeepZoomAcrossImages: zoom survives, ApplyInitialViewMode never runs)
        state.SwapSourceSize(3010, 2007); // B's RAW original arrives

        Assert.Equal(0.5, state.Zoom, 6); // not 1000 / 3010
    }

    [Fact]
    public void SwapSourceSize_AfterFitWidthOnPreviousImageAndSameSizedNewImage_KeepsZoom()
    {
        var state = new ViewerState { DpiScale = 1.0 };
        state.SetSourceSize(2000, 4000);
        state.ApplyInitialViewMode(InitialViewMode.FitWidth, 1000, 2000);

        state.SetSourceSize(2000, 4000, newImage: true); // same dimensions, different image
        state.SwapSourceSize(2010, 4020);

        Assert.Equal(0.5, state.Zoom, 6);
    }

    [Fact]
    public void SwapSourceSize_SameImageAfterFitWidth_StillRefitsAndSameSizeSetKeepsTheAxis()
    {
        var state = new ViewerState { DpiScale = 1.0 };
        state.SetSourceSize(2000, 4000);
        state.ApplyInitialViewMode(InitialViewMode.FitWidth, 1000, 2000);

        state.SetSourceSize(2000, 4000); // thumbnail -> preview of the same image: no new image
        state.SwapSourceSize(2010, 4020);

        Assert.Equal(1000.0 / 2010, state.Zoom, 6); // the fitted width still fills the viewport
    }

    [Fact]
    public void SwapSourceSize_FitWidthThenViewportGrows_RefitsAgainstTheCurrentViewport()
    {
        var state = new ViewerState { DpiScale = 1.0 };
        state.SetSourceSize(2000, 4000);
        state.ApplyInitialViewMode(InitialViewMode.FitWidth, 1000, 2000);

        state.UpdateViewport(1900, 2000); // the window grew; zoom is not refit here (Stretch is None) but the axis viewport must follow
        state.SwapSourceSize(2010, 4020); // same image, bigger decode

        Assert.Equal(1900.0 / 2010, state.Zoom, 6); // not 1000 / 2010 (stale)
    }

    [Fact]
    public void SwapSourceSize_FitHeightThenViewportGrows_RefitsAgainstTheCurrentViewport()
    {
        var state = new ViewerState { DpiScale = 1.0 };
        state.SetSourceSize(4000, 2000);
        state.ApplyInitialViewMode(InitialViewMode.FitHeight, 2000, 1000);

        state.UpdateViewport(2000, 1700);
        state.SwapSourceSize(4020, 2010);

        Assert.Equal(1700.0 / 2010, state.Zoom, 6);
    }

    [Fact]
    public void ApplyInitialViewMode_FitHeight_ZoomsToFitHeightAfterRefreshingViewport()
    {
        var state = new ViewerState();
        state.SetSourceSize(2000, 4000);
        state.DpiScale = 1.0;

        var applied = state.ApplyInitialViewMode(InitialViewMode.FitHeight, 1000, 2000);

        Assert.True(applied);
        Assert.Equal(0.5, state.Zoom, 6); // 2000 / 4000 source height
        Assert.Equal(ViewerStretchMode.None, state.Stretch);
    }

    [Fact]
    public void ApplyInitialViewMode_ClickZoomLevel_ZoomsToConfiguredPercent()
    {
        var state = new ViewerState();

        var applied = state.ApplyInitialViewMode(InitialViewMode.ClickZoomLevel, 1920, 1080, clickZoomPercent: 150);

        Assert.True(applied);
        Assert.Equal(1.5, state.Zoom, 6);
    }

    [Fact]
    public void ApplyInitialViewMode_KeepZoomAcrossImages_IsANoOpAndReturnsFalse()
    {
        var state = new ViewerState();
        state.SetZoom(3.0);

        var applied = state.ApplyInitialViewMode(InitialViewMode.Fit, 1920, 1080, keepZoomAcrossImages: true);

        Assert.False(applied);
        Assert.Equal(3.0, state.Zoom); // untouched: still the zoom from before the image change
        Assert.False(state.IsFit);
    }

    [Theory]
    [InlineData(1000, 500, 100, 1.0)]  // width alone determines the zoom regardless of height
    [InlineData(1000, 500, 200, 2.0)]  // DPI 2.0: same viewport DIPs need twice the zoom to fill the same source width
    public void FitWidthZoom_FillsViewportWidthAtDpiScale(int sourceWidth, int sourceHeight, int dpiPercent, double expectedZoom)
    {
        var state = new ViewerState();
        state.SetSourceSize(sourceWidth, sourceHeight);
        state.DpiScale = dpiPercent / 100.0;
        state.UpdateViewport(sourceWidth, 10000, force: true); // viewport DIP width == source width at 100 % DPI

        Assert.Equal(expectedZoom, state.FitWidthZoom, 6);
    }

    [Fact]
    public void FitWidthZoom_UnknownSourceOrViewport_IsZero()
    {
        var state = new ViewerState();
        Assert.Equal(0, state.FitWidthZoom);

        state.SetSourceSize(1000, 1000);
        Assert.Equal(0, state.FitWidthZoom); // viewport still unknown (Infinity)
    }

    [Theory]
    [InlineData(500, 1000, 100, 1.0)]
    [InlineData(500, 1000, 200, 2.0)]
    public void FitHeightZoom_FillsViewportHeightAtDpiScale(int sourceWidth, int sourceHeight, int dpiPercent, double expectedZoom)
    {
        var state = new ViewerState();
        state.SetSourceSize(sourceWidth, sourceHeight);
        state.DpiScale = dpiPercent / 100.0;
        state.UpdateViewport(10000, sourceHeight, force: true);

        Assert.Equal(expectedZoom, state.FitHeightZoom, 6);
    }

    [Fact]
    public void FitHeightZoom_UnknownSourceOrViewport_IsZero()
    {
        var state = new ViewerState();
        Assert.Equal(0, state.FitHeightZoom);
    }

    [Fact]
    public void ZoomToFitWidth_UnknownSize_DoesNotChangeZoom()
    {
        var state = new ViewerState();
        state.SetZoom(2.0);

        state.ZoomToFitWidth(); // FitWidthZoom is 0 (no source size): must not clobber the current zoom

        Assert.Equal(2.0, state.Zoom);
    }

    [Fact]
    public void ZoomToFitHeight_UnknownSize_DoesNotChangeZoom()
    {
        var state = new ViewerState();
        state.SetZoom(2.0);

        state.ZoomToFitHeight();

        Assert.Equal(2.0, state.Zoom);
    }

    // ---- PR-B: FitWidthAnchor pure helper (contract change: default is Centre, not TopThird) ----

    [Fact]
    public void FitWidthAnchorPoint_Centre_IsImageCentre()
    {
        var point = MainWindowHelpers.CalculateFitWidthAnchorPoint(FitWidthAnchor.Centre);

        Assert.Equal(0.5, point.X, 6);
        Assert.Equal(0.5, point.Y, 6);
    }

    [Fact]
    public void FitWidthAnchorPoint_TopThird_IsOneThirdDownTheImage()
    {
        var point = MainWindowHelpers.CalculateFitWidthAnchorPoint(FitWidthAnchor.TopThird);

        Assert.Equal(0.5, point.X, 6);
        Assert.Equal(1.0 / 3.0, point.Y, 6);
    }

    // ---- PR-C: "set current zoom as click level" pure helper (round + clamp) ----

    [Fact]
    public void ClickLevelFromEffectiveZoom_Null_ReturnsNull_Fit()
    {
        Assert.Null(MainWindowHelpers.CalculateClickLevelFromEffectiveZoom(null));
    }

    [Theory]
    [InlineData(1.0, 100)]
    [InlineData(1.499, 150)] // rounds to the nearest percent
    [InlineData(1.501, 150)]
    [InlineData(2.505, 251)] // AwayFromZero on the .5 boundary, not banker's rounding
    public void ClickLevelFromEffectiveZoom_RoundsToNearestPercent(double zoom, int expected)
    {
        Assert.Equal(expected, MainWindowHelpers.CalculateClickLevelFromEffectiveZoom(zoom));
    }

    [Fact]
    public void ClickLevelFromEffectiveZoom_BelowMinimum_ClampsToMinimum()
    {
        Assert.Equal(AppSettings.MinClickZoomPercent, MainWindowHelpers.CalculateClickLevelFromEffectiveZoom(0.01));
    }

    [Fact]
    public void ClickLevelFromEffectiveZoom_AboveMaximum_ClampsToMaximum()
    {
        Assert.Equal(AppSettings.MaxClickZoomPercent, MainWindowHelpers.CalculateClickLevelFromEffectiveZoom(50.0));
    }

    [Fact]
    public void Fullscreen_ToggleAndExit()
    {
        var state = new ViewerState();
        Assert.False(state.IsFullscreen);

        state.ToggleFullscreen();
        Assert.True(state.IsFullscreen);

        state.ToggleFullscreen();
        Assert.False(state.IsFullscreen);

        state.ToggleFullscreen();
        Assert.True(state.IsFullscreen);

        state.ExitFullscreen();
        Assert.False(state.IsFullscreen);
    }
}
