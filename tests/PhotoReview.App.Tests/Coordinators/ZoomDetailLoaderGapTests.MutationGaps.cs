using PhotoReview.App.Coordinators;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>Stryker round 1 (App): a navigation alone (no Reset, same target object) must drop a decode that finishes late.</summary>
public sealed partial class ZoomDetailLoaderGapTests
{
    [Fact]
    public async Task NavigationDuringInFlightDecode_WithoutAReset_DropsTheOriginalWhenItFinishesLate()
    {
        using var gate = new SemaphoreSlim(0);
        _decoder.GateByName["a.jpg"] = gate;
        _loader.SetZoom(1.0);
        Present("a.jpg");
        var load = _loader.PendingLoad;
        Assert.NotNull(load);

        _clock.NextNavigation(); // the user navigated on; the loader's target object is still the old one
        gate.Release();
        await load!;

        Assert.False(_loader.IsShowingOriginal);
    }

    [Fact]
    public async Task CancelledDecode_IsNotAFailureAndDoesNotRetryOrShowAnything()
    {
        using var gate = new SemaphoreSlim(0);
        _decoder.CancelNames.Add("a.jpg");
        _decoder.GateByName["a.jpg"] = gate;
        _loader.SetZoom(1.0);
        Present("a.jpg");
        var load = _loader.PendingLoad;
        Assert.NotNull(load);

        gate.Release();
        await load!;

        Assert.Equal(1, _decoder.OriginalDecodes("a.jpg")); // observed as a cancel: no update that would start the load again
        Assert.Null(_loader.PendingLoad);
        Assert.False(_loader.IsShowingOriginal);
        Assert.Empty(_shown);
    }
}
