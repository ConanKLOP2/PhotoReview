using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// Q-TOUCHPAD-REFRESH: <see cref="MainViewModel.RefreshView"/> on the real view-model + presenter + zoom-detail loader. It asks
/// the loader for the pixels the CURRENT view needs (Fit included), forces HighQuality scaling for this image only, and never
/// changes the zoom (the window adds nothing that could scroll). Preview: 600 px wide; original: 6000 x 4000.
/// </summary>
public sealed partial class MainViewModelZoomSwapTests
{
    [Fact]
    public async Task RefreshView_AtFit_WindowLargerThanThePreview_LoadsTheOriginalAndStaysAtFit()
    {
        var decoder = new SizedDecoder();
        decoder.Sizes["a.jpg"] = (6000, 4000, 6000, 4000);
        var vm = CreateViewModel(decoder, "a.jpg");
        try
        {
            await vm.Presenter.PresentAsync(0);
            _viewer.DpiScale = 1.0;
            _viewer.UpdateViewport(1200, 800, force: true); // Fit zoom 0.2 -> 1200 px on screen, the preview has 600
            Assert.True(_viewer.IsFit);

            var outcome = vm.RefreshView();

            Assert.NotNull(outcome);
            Assert.Equal(ZoomDetailRefreshResult.LoadingOriginal, outcome.Resolution);
            var load = vm.Presenter.ZoomDetail.PendingLoad;
            Assert.NotNull(load);
            decoder.OriginalGate.Release();
            await load!;

            Assert.True(vm.Presenter.ZoomDetail.IsShowingOriginal);
            Assert.True(_viewer.IsFit); // the view did not move: still Fit, same zoom
            Assert.Equal(1.0, _viewer.Zoom);
            Assert.Equal(ScalingQuality.HighQuality, _viewer.EffectiveScalingQuality);
        }
        finally
        {
            decoder.OriginalGate.Release();
        }
    }

    [Fact]
    public async Task RefreshView_AtFit_PreviewLargeEnough_LoadsNothing()
    {
        var decoder = new SizedDecoder();
        decoder.Sizes["a.jpg"] = (6000, 4000, 6000, 4000);
        var vm = CreateViewModel(decoder, "a.jpg");
        try
        {
            await vm.Presenter.PresentAsync(0);
            _viewer.DpiScale = 1.0;
            _viewer.UpdateViewport(600, 400, force: true); // Fit zoom 0.1 -> 600 px = the preview

            var outcome = vm.RefreshView();

            Assert.Equal(ZoomDetailRefreshResult.PreviewSufficient, outcome!.Resolution);
            Assert.Null(vm.Presenter.ZoomDetail.PendingLoad);
            Assert.False(vm.Presenter.ZoomDetail.IsShowingOriginal);
        }
        finally
        {
            decoder.OriginalGate.Release();
        }
    }

    [Fact]
    public async Task RefreshView_Zoomed_UsesTheZoomNotTheFitZoom_AndKeepsIt()
    {
        var decoder = new SizedDecoder();
        decoder.Sizes["a.jpg"] = (6000, 4000, 6000, 4000);
        var vm = CreateViewModel(decoder, "a.jpg");
        try
        {
            await vm.Presenter.PresentAsync(0);
            _viewer.DpiScale = 1.0;
            _viewer.UpdateViewport(6000, 4000, force: true); // a Fit zoom of 1.0 would need the original...
            _viewer.SetZoom(0.1); // ...but the view is zoomed to 600 px: the preview is enough

            var outcome = vm.RefreshView();

            Assert.Equal(ZoomDetailRefreshResult.PreviewSufficient, outcome!.Resolution);
            Assert.Equal(0.1, _viewer.Zoom);
            Assert.False(_viewer.IsFit);
        }
        finally
        {
            decoder.OriginalGate.Release();
        }
    }

    [Fact]
    public async Task RefreshView_LinearScaling_IsForcedToHighQualityForThisImageOnly()
    {
        var decoder = new SizedDecoder();
        decoder.Sizes["a.jpg"] = (6000, 4000, 6000, 4000);
        decoder.Sizes["b.jpg"] = (6000, 4000, 6000, 4000);
        var vm = CreateViewModel(decoder, "a.jpg", "b.jpg");
        try
        {
            await vm.Presenter.PresentAsync(0);
            _viewer.ScalingQuality = ScalingQuality.Linear;
            Assert.Equal(ScalingQuality.Linear, _viewer.EffectiveScalingQuality);

            var outcome = vm.RefreshView();

            Assert.True(outcome!.ScalingChanged);
            Assert.Equal(ScalingQuality.HighQuality, _viewer.EffectiveScalingQuality);
            Assert.Equal(ScalingQuality.Linear, _viewer.ScalingQuality); // the setting itself is untouched

            Assert.False(vm.RefreshView()!.ScalingChanged); // already HighQuality now

            await vm.NextAsync();
            Assert.Equal(ScalingQuality.Linear, _viewer.EffectiveScalingQuality); // the next image follows the setting again
        }
        finally
        {
            decoder.OriginalGate.Release();
            decoder.NextGate.Release();
        }
    }

    [Fact]
    public void RefreshView_WithoutImages_DoesNothing()
    {
        var vm = CreateViewModel(new SizedDecoder());

        Assert.Null(vm.RefreshView());
        Assert.False(_viewer.ForceHighQualityScaling);
    }
}
