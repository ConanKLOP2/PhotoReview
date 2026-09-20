using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PhotoReview.App;

internal static class LocalUiNextProbe
{
    public static async Task RunAsync(string folder)
    {
        // D06: runs on the shared WpfTestHost, which fixes the icon pack URI, starts the perf CSV
        // listener when PHOTOREVIEW_PERF_TRACE is set and attaches the dispatcher hooks.
        var result = await WpfTestHost.RunAsync(async _ =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow { ShowActivated = false, WindowState = WindowState.Minimized };
                // Never restore/save the user's real window placement (SetWindowPlacement could show/activate the window).
                window.SuppressWindowPlacement();
                window.Show();
                var load = window.LoadFolderAsync(folder, null);
                await load;
                var files = window._files;
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
                var show = window.ShowImageAsync(1);
                var status = ((TextBlock)window.FindName("StatusText")!).Text;
                if (status.Contains("Đang tải", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Warm Next showed loading: {status}");
                await show;
                var index = window._index;
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
                var durations = new long[sampleCount];
                for (var sample = 0; sample < sampleCount; sample++)
                {
                    if (!window.TryGetCachedPreview(files[sample], out var _))
                        throw new TimeoutException($"Image {sample + 1} was not warm before navigation");
                    var sw = Stopwatch.StartNew();
                    show = window.ShowImageAsync(sample);
                    status = ((TextBlock)window.FindName("StatusText")!).Text;
                    if (status.Contains("Đang tải", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Warm navigation showed loading at {sample + 1}: {status}");
                    await show;
                    durations[sample] = sw.ElapsedMilliseconds;
                    index = window._index;
                    if (index != sample) throw new InvalidOperationException($"Navigation selected {index}, expected {sample}");
                    if ((sample + 1) % 10 == 0) Console.WriteLine($"WPF navigation progress: {sample + 1}/{sampleCount}");
                }
                Array.Sort(durations);
                long At(double fraction) => durations[(int)Math.Ceiling(durations.Length * fraction) - 1];
                return $"PASS: local WPF warm navigation count={sampleCount} " +
                    $"medianMs={At(.5)} p95Ms={At(.95)} maxMs={durations[^1]} " +
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
