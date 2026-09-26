using System;
using System.Threading.Tasks;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// AR13b (moved verbatim from MainWindow.ApplyFitViewAsync, T89): resets the viewer to Fit and converges the layout
/// over up to three passes (UpdateLayout -> UpdateFitSize -> yield at Render priority -> snapshot -> stability
/// check), then scrolls home. Do not reduce the loop to a single pass without T89 evidence. A later viewport
/// operation (another Fit, or a zoom at a point) supersedes a pending one through the shared
/// <see cref="ViewportOperationVersion"/>. UI thread only; holds no reference to the window.
/// </summary>
internal sealed class FitViewController(IFitSurface surface, ViewerState viewer, ViewportOperationVersion viewportVersion, Action cancelPan)
{
    public async Task ApplyFitAsync()
    {
        cancelPan();
        var version = viewportVersion.Next();
        var (width, height) = surface.ViewportSize;
        viewer.ResetFit(width, height);

        // Converge layout: loop until viewport stabilizes or max iterations reached
        ViewportSnapshot? lastSnapshot = null;
        for (var pass = 0; pass < 3; pass++)
        {
            if (version != viewportVersion.Current || !surface.IsLoaded) return;
            surface.UpdateLayout();
            surface.UpdateFitSize();
            await surface.YieldToRenderAsync();

            // Check viewport convergence
            var currentSnapshot = surface.Capture();
            if (lastSnapshot != null && ViewportConvergence.IsStableViewport(lastSnapshot, currentSnapshot))
            {
                // Viewport stable, no need to continue
                break;
            }
            lastSnapshot = currentSnapshot;
        }

        if (version != viewportVersion.Current || !surface.IsLoaded) return;
        surface.UpdateLayout();
        surface.ScrollHome();
    }
}
