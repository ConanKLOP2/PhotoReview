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
}
