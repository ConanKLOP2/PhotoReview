using System.Threading.Tasks;
using System.Windows;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>Stryker round 1 (App): the window can unload after the convergence loop, before the final scroll home.</summary>
public sealed class FitViewControllerMutationGapTests
{
    private static readonly ViewportSnapshot Stable = new(
        Zoom: 1,
        Stretch: ViewerStretchMode.Uniform,
        MaxImageWidth: 800,
        MaxImageHeight: 600,
        ActualImageWidth: 800,
        ActualImageHeight: 600,
        ExtentWidth: 800,
        ExtentHeight: 600,
        ViewportWidth: 800,
        ViewportHeight: 600,
        HorizontalOffset: 0,
        VerticalOffset: 0,
        HorizontalScrollbarVisibility: Visibility.Collapsed,
        VerticalScrollbarVisibility: Visibility.Collapsed);

    private sealed class UnloadingSurface : IFitSurface
    {
        public int Captures { get; private set; }
        public int ScrollHomes { get; private set; }
        public int Layouts { get; private set; }
        public bool IsLoaded { get; private set; } = true;
        public (double Width, double Height) ViewportSize => (800, 600);

        public void UpdateLayout() => Layouts++;
        public void UpdateFitSize() { }
        public Task YieldToRenderAsync() => Task.CompletedTask;

        public ViewportSnapshot Capture()
        {
            if (++Captures == 2) IsLoaded = false; // the window closes right after the converging pass
            return Stable;
        }

        public void ScrollHome() => ScrollHomes++;
    }

    [Fact]
    public async Task ApplyFitAsync_WindowUnloadsAfterTheLoopConverged_DoesNotLayOutOrScrollHome()
    {
        var surface = new UnloadingSurface();
        var controller = new FitViewController(surface, new ViewerState(), new ViewportOperationVersion(), () => { });

        await controller.ApplyFitAsync();

        Assert.Equal(2, surface.Captures);
        Assert.Equal(0, surface.ScrollHomes);
        Assert.Equal(2, surface.Layouts); // the two passes only, not the final one
    }
}
