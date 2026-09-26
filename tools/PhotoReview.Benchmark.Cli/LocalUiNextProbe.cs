using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App;
using PhotoReview.App.Composition;
using PhotoReview.App.Services;
using PhotoReview.Core.Abstractions;
using PhotoReview.PerfAnalysis;

internal static class LocalUiNextProbe
{
    /// <summary>
    /// <c>--ui-next-probe &lt;folder&gt; [--cache-dir DIR]</c>. Like <c>--perf-session</c>, the session resume, journal
    /// and log go to a temporary data root (PHOTOREVIEW_DATA_ROOT) deleted at exit, and the preview/thumbnail disk
    /// caches go to a temporary directory too unless <c>--cache-dir</c> names one -- so the probe never overwrites the
    /// user's real session, journal, log or preview cache.
    /// </summary>
    public static async Task RunAsync(string folder, string? cacheDir = null)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "PhotoReview-UiNextProbe-" + Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(tempRoot, "data");
        var cacheRoot = cacheDir is null ? Path.Combine(tempRoot, "cache") : Path.GetFullPath(cacheDir);
        Directory.CreateDirectory(dataRoot);
        // Must be set before any AppSettings/journal/session/log object exists.
        Environment.SetEnvironmentVariable(PhotoReview.Core.AppPaths.DataRootEnvironmentVariable, dataRoot);
        try
        {
            await RunProbeAsync(folder, cacheRoot);
        }
        finally
        {
            AppLog.Shutdown(); // the log lives under the temp data root
            try { Directory.Delete(tempRoot, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"WARNING: could not delete {tempRoot}: {ex.Message}");
            }
        }
    }

    private static async Task RunProbeAsync(string folder, string cacheRoot)
    {
        // D06: runs on the shared WpfTestHost, which fixes the icon pack URI, starts the perf CSV
        // listener when PHOTOREVIEW_PERF_TRACE is set and attaches the dispatcher hooks.
        var result = await WpfTestHost.RunAsync(async _ =>
        {
            MainWindow? window = null;
            // AR02c: production DI graph (AppHost.BuildServices == App.ConfigureServices), so --ui-next-probe
            // measures the shipped preload/cache/decoder configuration (F2). The one override moves the disk
            // caches off the real %LOCALAPPDATA%\PhotoReview\{cache,thumbnails} (see CacheDirOverrideAppPaths).
            using var services = AppHost.BuildServices(overrides =>
                overrides.AddSingleton<IAppPaths>(_ => new CacheDirOverrideAppPaths(PhotoReview.Core.AppPaths.FromEnvironment(), cacheRoot)));
            try
            {
                var settingsStore = services.GetRequiredService<SettingsStore>();
                settingsStore.Load(); // must run before MainWindow is resolved -- see PerfSession.cs comment
                window = services.GetRequiredService<MainWindow>();
                window.ShowActivated = false;
                window.WindowState = WindowState.Minimized;
                // Never restore/save the user's real window placement (SetWindowPlacement could show/activate the window).
                window.SuppressWindowPlacement();
                var settings = window.Settings;
                if (!ReferenceEquals(settings, settingsStore.Current))
                    throw new InvalidOperationException("AR02c invariant broken: window.Settings is not the DI SettingsStore.Current instance.");
                var targetDecodeBox = services.GetRequiredService<PreviewStateContext>().TargetDecodeBox();
                Console.WriteLine($"config: graph=production cacheBytes={settings.ImageCacheCapacityBytes} " +
                    $"preloadWorkers={settings.PreloadWorkerCount} decoder={settings.DecoderBackend} " +
                    $"sourceBytesCache={settings.UseSourceBytesCache} loadingMode={settings.LoadingMode} " +
                    $"preload=true diskCache=true targetDecodeWidth={targetDecodeBox.Width} targetDecodeHeight={targetDecodeBox.Height}");
                window.Show();
                var load = window.LoadFolderAsync(folder, null);
                await load;
                var files = window.Files;
                if (files.Count < 2) throw new InvalidOperationException("Probe needs at least two images");
                var second = files[1];
                var deadline = DateTime.UtcNow.AddSeconds(45);
                var ready = false;
                while (DateTime.UtcNow < deadline)
                {
                    ready = window.TryGetCachedPreview(second, out var _);
                    if (ready) break;
                    await Task.Delay(100);
                }
                if (!ready) throw new TimeoutException("Next image did not reach RAM cache");
                var presenter = window.ViewModel.Presenter;
                var show = window.ShowImageAsync(1);
                var status = ((TextBlock)window.FindName("StatusText")!).Text;
                await show;
                // State, not UI text: the loading status is shown exactly when the preview was not in RAM.
                if (!presenter.LastPresentStartedFromRam)
                    throw new InvalidOperationException($"Warm Next showed loading: {status}");
                var index = window.CurrentIndex;
                if (index != 1) throw new InvalidOperationException($"Warm Next selected wrong index: {index}");
                var sampleCount = Math.Min(30, files.Count);
                deadline = DateTime.UtcNow.AddSeconds(150);
                var lastProgress = DateTime.UtcNow;
                while (DateTime.UtcNow < deadline)
                {
                    var allReady = true;
                    var readyCount = 0;
                    for (var sample = 0; sample < sampleCount; sample++)
                    {
                        if (window.TryGetCachedPreview(files[sample], out var _)) readyCount++;
                        else allReady = false;
                    }
                    if (allReady) break;
                    if ((DateTime.UtcNow - lastProgress).TotalSeconds >= 5)
                    {
                        Console.WriteLine($"WPF warm progress: {readyCount}/{sampleCount}");
                        lastProgress = DateTime.UtcNow;
                    }
                    await Task.Delay(200);
                }
                var durations = new double[sampleCount];
                for (var sample = 0; sample < sampleCount; sample++)
                {
                    if (!window.TryGetCachedPreview(files[sample], out var _))
                        throw new TimeoutException($"Image {sample + 1} was not warm before navigation");
                    var swStart = Stopwatch.GetTimestamp();
                    show = window.ShowImageAsync(sample);
                    status = ((TextBlock)window.FindName("StatusText")!).Text;
                    await show;
                    durations[sample] = Stopwatch.GetElapsedTime(swStart).TotalMilliseconds;
                    if (!presenter.LastPresentStartedFromRam)
                        throw new InvalidOperationException($"Warm navigation showed loading at {sample + 1}: {status}");
                    index = window.CurrentIndex;
                    if (index != sample) throw new InvalidOperationException($"Navigation selected {index}, expected {sample}");
                    if ((sample + 1) % 10 == 0) Console.WriteLine($"WPF navigation progress: {sample + 1}/{sampleCount}");
                }
                Array.Sort(durations);
                double At(double fraction) => PerfStats.NearestRank(durations, fraction * 100.0);
                return $"PASS: local WPF warm navigation count={sampleCount} " +
                    $"medianMs={At(.5):F2} p95Ms={At(.95):F2} maxMs={durations[^1]:F2} " +
                    $"firstNextStatus={status}";
            }
            finally
            {
                window?.Close();
            }
        }, TimeSpan.FromSeconds(240));
        Console.WriteLine(result);
    }
}
