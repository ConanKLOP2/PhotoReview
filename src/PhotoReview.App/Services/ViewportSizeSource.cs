namespace PhotoReview.App.Services;

/// <summary>
/// AR02a (F3): production seam so the composition root can query the real viewport size for
/// <c>ApplyInitialViewMode</c> without <c>MainViewModelCompositionRoot</c> depending on
/// <see cref="MainWindow"/> directly. <see cref="MainWindow"/>'s DI constructor sets
/// <see cref="Get"/> to its own viewport accessor right after <c>InitializeComponent()</c>;
/// until then (and in any host that never wires it, e.g. a headless test), it returns (0, 0),
/// matching the previous hard-coded behaviour.
/// </summary>
public sealed class ViewportSizeSource
{
    public Func<(double Width, double Height)> Get { get; set; } = static () => (0, 0);
}
