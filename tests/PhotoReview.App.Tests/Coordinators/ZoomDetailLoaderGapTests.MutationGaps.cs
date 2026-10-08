using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>Stryker round 1 (App): a navigation alone (no Reset, same target object) must drop a decode that finishes late.</summary>
[Collection("GlobalState")] // the log test below swaps the process-wide AppLog
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

    [Fact]
    public async Task Reset_CancelsAQueuedDecodeSoItNeverStartsOnceTheSlotFrees()
    {
        // The original decodes run one at a time: a.jpg holds the slot (blocked in the decoder), b.jpg's load waits for it.
        using var gateA = new SemaphoreSlim(0);
        _decoder.GateByName["a.jpg"] = gateA;
        _loader.SetZoom(1.0);
        Present("a.jpg");
        var loadA = _loader.PendingLoad;
        Assert.NotNull(loadA);
        Present("b.jpg"); // Reset() cancels A's token (A is already inside the decoder), then B queues behind A
        var loadB = _loader.PendingLoad;
        Assert.NotNull(loadB);
        Assert.NotSame(loadA, loadB);

        using var gateC = new SemaphoreSlim(0);
        _decoder.GateByName["c.jpg"] = gateC;
        Present("c.jpg"); // Reset() cancels B's token while B is still queued
        var loadC = _loader.PendingLoad;
        Assert.NotNull(loadC);

        gateA.Release(); // the slot frees: a cancelled B must be dropped before it reaches the decoder
        await loadA!;
        await loadB!;
        gateC.Release();
        await loadC!;

        Assert.Equal(0, _decoder.OriginalDecodes("b.jpg"));
        Assert.Equal(1, _decoder.OriginalDecodes("c.jpg"));
        Assert.True(_loader.IsShowingOriginal);
    }

    [Fact]
    public async Task ASupersededTarget_OnTheSameNavigation_DropsItsOriginalWhenItFinishesLate()
    {
        using var gateA = new SemaphoreSlim(0);
        using var gateB = new SemaphoreSlim(0);
        _decoder.GateByName["a.jpg"] = gateA;
        _decoder.GateByName["b.jpg"] = gateB;
        _loader.SetZoom(1.0);
        var token = _clock.NextNavigation();
        foreach (var name in new[] { "a.jpg", "b.jpg" })
        {
            var path = Path.Combine(_tempDir, name);
            File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xD9]);
        }
        void PresentSameNavigation(string name)
        {
            var path = Path.Combine(_tempDir, name);
            var key = ImageCacheKey.Create(path, false, new DecodeBox(600, 0));
            _loader.OnPreviewPresented(token, path, key, new FakeImage(600, 400, 6000, 4000, downscaled: true));
        }

        PresentSameNavigation("a.jpg");
        var loadA = _loader.PendingLoad;
        PresentSameNavigation("b.jpg"); // a new target for the SAME navigation token: the navigation is still current
        var loadB = _loader.PendingLoad;
        Assert.NotSame(loadA, loadB);

        gateA.Release();
        await loadA!;

        Assert.False(_loader.IsShowingOriginal); // a.jpg's pixels must never be shown for b.jpg
        Assert.Empty(_shown);
        gateB.Release();
        await loadB!;
        Assert.True(_loader.IsShowingOriginal);
    }

    [Fact]
    public async Task WhenShowingTheOriginalThrows_TheFailureIsLoggedAndTheLoadStillCompletes()
    {
        using var capture = new CapturedAppLog();
        var loader = new ZoomDetailLoader(_service, _clock, (_, _, _) => throw new InvalidOperationException("show failed"));
        using var gate = new SemaphoreSlim(0);
        _decoder.GateByName["a.jpg"] = gate;
        loader.SetZoom(1.0);
        var path = Path.Combine(_tempDir, "a.jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xD9]);
        var token = _clock.NextNavigation();
        loader.OnPreviewPresented(token, path, ImageCacheKey.Create(path, false, new DecodeBox(600, 0)), new FakeImage(600, 400, 6000, 4000, downscaled: true));
        var load = loader.PendingLoad;
        Assert.NotNull(load);

        gate.Release();
        await load!;

        Assert.Contains("ZoomDetail update after original decode failed", capture.Text(), StringComparison.Ordinal);
    }
}
