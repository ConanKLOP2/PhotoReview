using System.Reflection;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
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
                WpfTestHost.SuppressWindowPlacement(window);
                window.Show();
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var load = (Task)typeof(MainWindow).GetMethod("LoadFolderAsync", flags)!
                    .Invoke(window, [folder, null])!;
                await load;
                var files = (List<string>)typeof(MainWindow).GetField("_files", flags)!.GetValue(window)!;
                if (files.Count < 2) throw new InvalidOperationException("Probe needs at least two images");
                // Two overloads exist (string / ImageCacheKey); pick the path overload explicitly.
                var tryCached = typeof(MainWindow).GetMethod("TryGetCachedPreview", flags,
                    [typeof(string), typeof(BitmapImage).MakeByRefType()])!;
                var second = files[1];
                var deadline = DateTime.UtcNow.AddSeconds(45);
                var ready = false;
                while (DateTime.UtcNow < deadline)
                {
                    var probeArgs = new object?[] { second, null };
                    ready = (bool)tryCached.Invoke(window, probeArgs)!;
                    if (ready) break;
                    await Task.Delay(100);
                }
                if (!ready) throw new TimeoutException("Next image did not reach RAM cache");
                var show = (Task)typeof(MainWindow).GetMethod("ShowImageAsync", flags)!.Invoke(window, [1])!;
                var status = ((TextBlock)window.FindName("StatusText")!).Text;
                if (status.Contains("Đang tải", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Warm Next showed loading: {status}");
                await show;
                var index = (int)typeof(MainWindow).GetField("_index", flags)!.GetValue(window)!;
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
                        var probeArgs = new object?[] { files[sample], null };
                        if ((bool)tryCached.Invoke(window, probeArgs)!) readyCount++;
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
                    var probeArgs = new object?[] { files[sample], null };
                    if (!(bool)tryCached.Invoke(window, probeArgs)!)
                        throw new TimeoutException($"Image {sample + 1} was not warm before navigation");
                    var sw = Stopwatch.StartNew();
                    show = (Task)typeof(MainWindow).GetMethod("ShowImageAsync", flags)!.Invoke(window, [sample])!;
                    status = ((TextBlock)window.FindName("StatusText")!).Text;
                    if (status.Contains("Đang tải", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Warm navigation showed loading at {sample + 1}: {status}");
                    await show;
                    durations[sample] = sw.ElapsedMilliseconds;
                    index = (int)typeof(MainWindow).GetField("_index", flags)!.GetValue(window)!;
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
