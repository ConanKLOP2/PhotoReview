using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// AR13b: the Fit convergence loop (T89) moved out of MainWindow, driven through a fake <see cref="IFitSurface"/>.
/// The loop must keep its passes and their order; these tests pin both.
/// </summary>
public sealed class FitViewControllerTests
{
    private readonly FakeFitSurface _surface = new();
    private readonly ViewerState _viewer = new();
    private readonly ViewportOperationVersion _version = new();
    private readonly FitViewController _controller;

    public FitViewControllerTests()
    {
        _controller = new FitViewController(_surface, _viewer, _version, () => _surface.Log.Add("CancelPan"));
    }

    [Fact]
    public async Task StableOnTheSecondPass_StopsThere_ThenScrollsHomeOnce()
    {
        _surface.Snapshots = [Snapshot(800), Snapshot(800), Snapshot(900)];

        await _controller.ApplyFitAsync();

        Assert.Equal(
            "CancelPan | UpdateLayout UpdateFitSize Yield Capture | UpdateLayout UpdateFitSize Yield Capture | UpdateLayout ScrollHome",
            Trace());
    }

    [Fact]
    public async Task NeverStable_RunsAllThreePasses_ThenScrollsHome()
    {
        _surface.Snapshots = [Snapshot(800), Snapshot(820), Snapshot(840), Snapshot(860)];

        await _controller.ApplyFitAsync();

        Assert.Equal(3, _surface.Count("Capture"));
        Assert.Equal(3, _surface.Count("UpdateFitSize"));
        Assert.Equal(1, _surface.Count("ScrollHome"));
        Assert.EndsWith("| UpdateLayout ScrollHome", Trace(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetsTheViewerToFitWithTheViewportSize_AfterCancellingThePan()
    {
        _viewer.SetZoom(3.0);
        _surface.ViewportSize = (1000, 700);
        _surface.Snapshots = [Snapshot(800), Snapshot(800)];

        await _controller.ApplyFitAsync();

        Assert.True(_viewer.IsFit);
        Assert.Equal(1000, _viewer.MaxImageWidth);
        Assert.Equal(700, _viewer.MaxImageHeight);
        Assert.Equal("CancelPan", _surface.Log[0]);
    }

    [Fact]
    public async Task ASecondFitWhileTheFirstWaits_SupersedesIt_SoHomeIsScrolledOnce()
    {
        _surface.Snapshots = [Snapshot(800), Snapshot(800), Snapshot(800), Snapshot(800)];
        _surface.HoldYields = true;

        var first = _controller.ApplyFitAsync();
        var second = _controller.ApplyFitAsync();
        _surface.HoldYields = false;
        _surface.ReleaseYields();
        await first;
        await second;

        Assert.Equal(1, _surface.Count("ScrollHome"));
    }

    [Fact]
    public async Task AZoomWhileTheFitWaits_SupersedesIt()
    {
        _surface.Snapshots = [Snapshot(800), Snapshot(820), Snapshot(840)];
        _surface.HoldYields = true;

        var fit = _controller.ApplyFitAsync();
        _version.Next(); // ZoomAtPoint took a newer version
        _surface.HoldYields = false; // a pass that wrongly continued must finish (and fail the asserts), not hang
        _surface.ReleaseYields();
        await fit;

        Assert.Equal(1, _surface.Count("Capture")); // the pass that was waiting finishes its snapshot, then stops
        Assert.Equal(0, _surface.Count("ScrollHome"));
    }

    [Fact]
    public async Task NotLoaded_DoesNoLayoutWork()
    {
        _surface.IsLoaded = false;

        await _controller.ApplyFitAsync();

        Assert.Equal("CancelPan", Trace());
    }

    /// <summary>The call log, one pass per "|"-separated group (a pass starts with UpdateLayout).</summary>
    private string Trace() => string.Join(" ", _surface.Log).Replace(" UpdateLayout", " | UpdateLayout", StringComparison.Ordinal);

    private static ViewportSnapshot Snapshot(double viewportWidth) => new(
        Zoom: 1,
        Stretch: ViewerStretchMode.Uniform,
        MaxImageWidth: viewportWidth,
        MaxImageHeight: 600,
        ActualImageWidth: viewportWidth,
        ActualImageHeight: 600,
        ExtentWidth: viewportWidth,
        ExtentHeight: 600,
        ViewportWidth: viewportWidth,
        ViewportHeight: 600,
        HorizontalOffset: 0,
        VerticalOffset: 0,
        HorizontalScrollbarVisibility: Visibility.Collapsed,
        VerticalScrollbarVisibility: Visibility.Collapsed);

    private sealed class FakeFitSurface : IFitSurface
    {
        private readonly List<TaskCompletionSource> _heldYields = [];
        private int _captured;

        public List<string> Log { get; } = [];
        public ViewportSnapshot[] Snapshots { get; set; } = [];
        public bool HoldYields { get; set; }
        public (double Width, double Height) ViewportSize { get; set; } = (800, 600);
        public bool IsLoaded { get; set; } = true;

        public int Count(string call) => Log.FindAll(entry => entry == call).Count;

        public void UpdateLayout() => Log.Add("UpdateLayout");
        public void UpdateFitSize() => Log.Add("UpdateFitSize");
        public void ScrollHome() => Log.Add("ScrollHome");

        public ViewportSnapshot Capture()
        {
            Log.Add("Capture");
            return Snapshots[_captured++];
        }

        public Task YieldToRenderAsync()
        {
            Log.Add("Yield");
            if (!HoldYields) return Task.CompletedTask;
            var pending = new TaskCompletionSource();
            _heldYields.Add(pending);
            return pending.Task;
        }

        public void ReleaseYields()
        {
            var held = _heldYields.ToArray();
            _heldYields.Clear();
            foreach (var pending in held) pending.SetResult();
        }
    }
}
