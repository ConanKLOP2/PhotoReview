using PhotoReview.Core.Model;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Settings;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

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

        state.SetZoom(0.05);
        Assert.Equal(0.25, state.Zoom);

        state.SetZoom(10.0);
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

    [Theory]
    [InlineData(1.0, 2.0, 100, 80, 0, 0, 2000, 1600, 800, 600, 100, 80)]
    [InlineData(2.0, 1.0, 120, 90, 300, 200, 1000, 800, 800, 600, 90, 55)]
    public void ZoomViewportOffsets_KeepPointUnderMouseAnchored(
        double oldZoom, double newZoom, double mouseX, double mouseY,
        double oldHorizontal, double oldVertical, double newExtentWidth, double newExtentHeight,
        double viewportWidth, double viewportHeight, double expectedHorizontal, double expectedVertical)
    {
        var result = MainWindowHelpers.CalculateZoomViewportOffsets(oldZoom, newZoom, mouseX, mouseY,
            oldHorizontal, oldVertical, newExtentWidth, newExtentHeight, viewportWidth, viewportHeight);

        Assert.Equal(expectedHorizontal, result.Horizontal, 6);
        Assert.Equal(expectedVertical, result.Vertical, 6);
    }

    [Fact]
    public void ZoomViewportOffsets_ClampToExtent()
    {
        var result = MainWindowHelpers.CalculateZoomViewportOffsets(1, 4, 1000, 1000, 0, 0, 500, 400, 800, 600);

        Assert.Equal(0, result.Horizontal);
        Assert.Equal(0, result.Vertical);
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
    [InlineData(InitialViewMode.Percent400, 4.0, ViewerStretchMode.None)]
    public void ApplyInitialViewMode_ConfiguresStateCorrectly(InitialViewMode mode, double expectedZoom, ViewerStretchMode expectedStretch)
    {
        var state = new ViewerState();
        state.ApplyInitialViewMode(mode, 1920, 1080);

        Assert.Equal(expectedZoom, state.Zoom);
        Assert.Equal(expectedStretch, state.Stretch);
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
