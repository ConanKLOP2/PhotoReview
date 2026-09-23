using System.Windows.Media;

namespace PhotoReview.App.Services;

/// <summary>
/// perf(startup): helpers that move unavoidable WPF start-up cost to where it can overlap other work.
/// </summary>
internal static class StartupWarmup
{
    /// <summary>
    /// Creates the UI dispatcher's <c>MediaContext</c> now. Its constructor connects the composition
    /// channels to WPF's render thread and blocks the UI thread in <c>DUCE.Channel.SyncFlush</c> until
    /// that thread has initialized (~350 ms at a cold start, measured). It otherwise happens inside
    /// MainWindow's <c>InitializeComponent</c>, the first time a visual gets a child; calling it
    /// explicitly lets App_Startup run CPU work (config.json) on the thread pool during that wait.
    /// </summary>
    public static void ConnectRenderThread()
    {
        // VisualCollection.Add -> Visual.VerifyAPIReadWrite -> MediaContext.From(Dispatcher), which
        // creates (once per dispatcher) the same MediaContext the main window then reuses.
        var root = new ContainerVisual();
        root.Children.Add(new DrawingVisual());
    }
}
