using PhotoReview.App.Coordinators;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// Q-TOUCHPAD-REFRESH: <see cref="ZoomDetailLoader.Refresh"/> -- decodes the original only when the preview has fewer pixels than
/// the screen shows (Fit included, e.g. after the window grew), re-shows a held original at once, retries a failed decode, and
/// keeps the original at Fit until the zoom actually changes or the image changes. The preview is 600 px wide, the original 6000.
/// </summary>
public sealed partial class ZoomDetailLoaderGapTests
{
    /// <summary>Runs <paramref name="start"/> with <paramref name="name"/>'s decode held on a gate, then lets it finish and awaits the whole load
    /// (an ungated load can clear PendingLoad before its final Update has run). Returns what <paramref name="start"/> returned.</summary>
    private async Task<T> WithGatedDecodeAsync<T>(string name, Func<T> start)
    {
        using var gate = new SemaphoreSlim(0);
        _decoder.GateByName[name] = gate;
        var result = start();
        var load = _loader.PendingLoad;
        Assert.NotNull(load);
        gate.Release();
        await load!;
        _decoder.GateByName.Remove(name);
        return result;
    }

    [Fact]
    public void Refresh_AtFit_PreviewAlreadyLargeEnough_LoadsNothing()
    {
        _loader.SetZoom(null);
        Present("a.jpg");

        var result = _loader.Refresh(0.1); // 600 px on screen = the preview's 600

        Assert.Equal(ZoomDetailRefreshResult.PreviewSufficient, result);
        Assert.Null(_loader.PendingLoad);
        Assert.Equal(0, _decoder.OriginalDecodes("a.jpg"));
        Assert.Empty(_shown);
    }

    [Fact]
    public async Task Refresh_AtFit_ScreenShowsMorePixelsThanThePreview_DecodesAndKeepsTheOriginalAtFit()
    {
        using var gate = new SemaphoreSlim(0);
        _decoder.GateByName["a.jpg"] = gate;
        _loader.SetZoom(null);
        Present("a.jpg");

        var result = _loader.Refresh(0.2); // 1200 px on screen > 600

        Assert.Equal(ZoomDetailRefreshResult.LoadingOriginal, result);
        var load = _loader.PendingLoad;
        Assert.NotNull(load);
        gate.Release();
        await load!;
        Assert.True(_loader.IsShowingOriginal);
        Assert.Same(_loader.HeldOriginal!.PlatformImage, _shown[^1]);

        _loader.SetZoom(null); // a repeated Fit (no zoom change) keeps the refreshed original
        Assert.True(_loader.IsShowingOriginal);
        Assert.Single(_shown);
    }

    [Fact]
    public async Task Refresh_HoldEndsWhenTheZoomChanges_FitThenShowsThePreviewAgain()
    {
        _loader.SetZoom(null);
        Present("a.jpg");
        await WithGatedDecodeAsync("a.jpg", () => _loader.Refresh(0.2));
        Assert.True(_loader.IsShowingOriginal);

        _loader.SetZoom(1.0);
        _loader.SetZoom(null);

        Assert.False(_loader.IsShowingOriginal); // normal Fit behaviour again: the preview
    }

    [Fact]
    public async Task Refresh_AtFit_WithAHeldOriginal_ShowsItAtOnceWithoutDecoding()
    {
        _loader.SetZoom(1.0);
        await WithGatedDecodeAsync("a.jpg", () => Present("a.jpg"));
        _loader.SetZoom(null); // back to Fit: the preview again, the original stays held
        Assert.False(_loader.IsShowingOriginal);
        var shownBefore = _shown.Count;

        var result = _loader.Refresh(0.2);

        Assert.Equal(ZoomDetailRefreshResult.ShowedOriginal, result);
        Assert.True(_loader.IsShowingOriginal);
        Assert.Equal(shownBefore + 1, _shown.Count);
        Assert.Equal(1, _decoder.OriginalDecodes("a.jpg"));
    }

    [Fact]
    public async Task Refresh_AfterAFailedDecode_RetriesIt()
    {
        _decoder.FailNames.Add("a.jpg");
        _loader.SetZoom(1.0);
        await WithGatedDecodeAsync("a.jpg", () => Present("a.jpg"));
        Assert.Equal(1, _decoder.OriginalDecodes("a.jpg"));
        _loader.SetZoom(2.0);
        Assert.Equal(1, _decoder.OriginalDecodes("a.jpg")); // the failure latch blocks a zoom-triggered retry

        _decoder.FailNames.Remove("a.jpg");
        var result = await WithGatedDecodeAsync("a.jpg", () => _loader.Refresh(2.0));

        Assert.Equal(ZoomDetailRefreshResult.RetryingOriginal, result);
        Assert.Equal(2, _decoder.OriginalDecodes("a.jpg"));
        Assert.True(_loader.IsShowingOriginal);
    }

    [Fact]
    public async Task Refresh_WhileTheOriginalIsShown_DoesNothing()
    {
        _loader.SetZoom(1.0);
        await WithGatedDecodeAsync("a.jpg", () => Present("a.jpg"));
        var shownBefore = _shown.Count;

        Assert.Equal(ZoomDetailRefreshResult.AlreadyFullResolution, _loader.Refresh(1.0));
        Assert.Equal(shownBefore, _shown.Count);
        Assert.Equal(1, _decoder.OriginalDecodes("a.jpg"));
    }

    [Fact]
    public void Refresh_WithoutATarget_ReportsFullResolution()
    {
        Assert.Equal(ZoomDetailRefreshResult.AlreadyFullResolution, _loader.Refresh(1.0));
    }

    [Fact]
    public void Refresh_ZoomedPreviewLargeEnough_LoadsNothing()
    {
        _loader.SetZoom(0.05); // 300 px on screen: no zoom-triggered decode either
        Present("a.jpg");

        Assert.Equal(ZoomDetailRefreshResult.PreviewSufficient, _loader.Refresh(0.05));
        Assert.Equal(0, _decoder.OriginalDecodes("a.jpg"));
    }

    [Fact]
    public async Task Refresh_HoldDoesNotSurviveNavigation()
    {
        _loader.SetZoom(null);
        Present("a.jpg");
        await WithGatedDecodeAsync("a.jpg", () => _loader.Refresh(0.2));

        Present("b.jpg"); // the next image at Fit: back to the preview-only behaviour

        Assert.Null(_loader.PendingLoad);
        Assert.Equal(0, _decoder.OriginalDecodes("b.jpg"));
        Assert.False(_loader.IsShowingOriginal);
    }
}
