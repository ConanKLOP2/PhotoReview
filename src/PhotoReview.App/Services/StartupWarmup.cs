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

    /// <summary>
    /// P-1: runs the config.json parse pipeline once on built-in defaults, on the thread pool, from the very start of
    /// the process (App's static constructor, before WPF's Application exists). The first parse pays ~100-150 ms of
    /// one-time cost (System.Text.Json metadata of the ~100 AppSettings properties and the JIT of their converters,
    /// measured with dotnet-trace: <c>AppSettingsPropInit</c> alone ~60 ms) which the UI thread otherwise waits for
    /// after connecting the render thread. Nothing is read or written: the real <see cref="SettingsStore.Load"/> then
    /// finds the metadata ready. A failure only loses the warm-up.
    /// </summary>
    public static Task WarmSettingsParser() => Task.Run(() =>
    {
        try
        {
            var defaults = System.Text.Json.JsonSerializer.Serialize(new AppSettings(), AppSettingsJsonContext.Default.AppSettings);
            _ = SettingsStore.ParseText(defaults);
            PhotoReview.Core.Diagnostics.PhotoReviewPerf.StartupMark("settingsParserWarm");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Warm-up only: the real load reports its own failures.
        }
    });
}
