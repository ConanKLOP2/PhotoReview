using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using Xunit;

namespace PhotoReview.App.Tests.Input;

// R04: a Fit whose first pass was superseded by a newer viewport operation must not run its
// scrollbar-overflow correction (which would start a fresh version and re-apply the obsolete Fit).
public sealed partial class PointerInputControllerTests
{
    private void ArrangeSupersededFirstPass()
    {
        _viewer.SetSourceSize(2000, 4000);
        _viewer.DpiScale = 1.0;
        _surface.ExtentWidth = 1200; // overflows the 800 DIP viewport width
        _surface.ExtentHeight = 900; // overflows the 600 DIP viewport height
        _surface.HoldYields = true;
    }

    private async Task SupersedeThenReleaseAsync(Task fit)
    {
        _version.Next();
        _viewer.SetZoom(2.0); // a newer viewport operation wins
        _surface.HoldYields = false; // before releasing, so a (wrong) correction pass would run to completion
        _surface.ReleaseYields();
        await fit.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private void AssertNewerZoomSurvived()
    {
        Assert.Empty(_surface.Scrolls);
        Assert.Equal(1, _surface.YieldCount);
    }

    [Fact]
    public async Task FitWidthAsync_SupersededDuringFirstPass_CorrectionDoesNotReapplyFit()
    {
        ArrangeSupersededFirstPass();
        await SupersedeThenReleaseAsync(_controller.FitWidthAsync());
        Assert.Equal(2.0, _viewer.Zoom, 6);
        AssertNewerZoomSurvived();
    }

    [Fact]
    public async Task FitHeightAsync_SupersededDuringFirstPass_CorrectionDoesNotReapplyFit()
    {
        ArrangeSupersededFirstPass();
        await SupersedeThenReleaseAsync(_controller.FitHeightAsync());
        Assert.Equal(2.0, _viewer.Zoom, 6);
        AssertNewerZoomSurvived();
    }

    [Fact]
    public async Task ApplyInitialViewAsync_FitWidth_SupersededDuringFirstPass_CorrectionDoesNotReapplyFit()
    {
        ArrangeSupersededFirstPass();
        await SupersedeThenReleaseAsync(_controller.ApplyInitialViewAsync(InitialViewMode.FitWidth, AppSettings.DefaultClickZoomPercent));
        Assert.Equal(2.0, _viewer.Zoom, 6);
        AssertNewerZoomSurvived();
    }
}
