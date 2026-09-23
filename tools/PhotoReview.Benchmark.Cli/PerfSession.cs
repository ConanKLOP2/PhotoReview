using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App;
using PhotoReview.App.Composition;
using PhotoReview.App.Diagnostics;
using PhotoReview.App.Services;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;

/// <summary>
/// D06 in-process scenario driver: <c>--perf-session &lt;scenario.json&gt; &lt;folder&gt; &lt;outDir&gt;
/// [--mode Fast|Preview|Original] [--repeat N] [--alias NAME] [--commit SHA]</c>.
/// <para>
/// Safety (PERF-DIAGNOSIS-TASKS rule 4): keys are delivered only as WPF routed events raised on the
/// window inside this process. No SendInput/SendKeys/keybd_event/AttachThreadInput/SetForegroundWindow,
/// no Activate()/Focus(), no clipboard. The window is shown with ShowActivated=false and may sit behind
/// other windows. The real config.json and window-placement.json are never written; session/journal/log
/// go to <c>&lt;outDir&gt;\data</c>; scenarios that move files run on a temporary copy only.
/// </para>
/// </summary>
internal static class PerfSession
{
    private const string TempMarker = ".perf-session-temp";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- Scenario schema -------------------------------------------------------------------------

    internal sealed class Scenario
    {
        public string Name { get; set; } = "";
        public string? Note { get; set; }
        /// <summary>Optional copy preparation: kind "files" (first N images) or "compare" (N pairs a.jpg + a (1).jpg).</summary>
        public CopySpec? Copy { get; set; }
        public List<Step> Steps { get; set; } = [];
    }

    internal sealed class CopySpec
    {
        public string Kind { get; set; } = "files";
        public int? Count { get; set; }
        public int? Pairs { get; set; }
    }

    internal sealed class Step
    {
        public string? Open { get; set; }
        public int? Index { get; set; }
        public string? Key { get; set; }
        public string? Action { get; set; }
        public int? Repeat { get; set; }
        public int? IntervalMs { get; set; }
        public int? WaitMs { get; set; }
        public bool? WaitIdle { get; set; }
        public int? TimeoutMs { get; set; }
        /// <summary>
        /// Event-driven pacing for a "key" step: wait for this key's image to be presented AND preload to
        /// go idle (capped at <see cref="SettleMaxMs"/>), then wait at least <see cref="SettleMinMs"/>
        /// before the next key. Replaces the fixed <see cref="IntervalMs"/> delay for this step. Absent
        /// (or false), <see cref="IntervalMs"/> behaves exactly as before.
        /// </summary>
        public bool? Settle { get; set; }
        /// <summary>Minimum pacing floor after a key settles (default 50ms).</summary>
        public int? SettleMinMs { get; set; }
        /// <summary>Cap on how long to wait for a key to settle before counting a timeout (default IntervalMs, else 3000ms).</summary>
        public int? SettleMaxMs { get; set; }
        /// <summary>Zoom levels; 0 means Fit (ToggleFit shortcut), &gt;0 calls SetZoom(value).</summary>
        public double[]? Zoom { get; set; }
        public int? HoldMs { get; set; }
        public PanSpec? Pan { get; set; }

        public string Kind =>
            Open is not null ? "open" :
            Key is not null ? "key" :
            Action is not null ? "action" :
            WaitMs is not null ? "wait" :
            WaitIdle == true ? "waitIdle" :
            Zoom is not null ? "zoom" :
            Pan is not null ? "pan" : "unknown";
    }

    internal sealed class PanSpec
    {
        public double Dx { get; set; }
        public double Dy { get; set; }
        public int Steps { get; set; } = 30;
        public int IntervalMs { get; set; } = 16;
    }

    private sealed record StepRecord(int Step, string Kind, long StartQpc, long EndQpc, double Ms, string? Detail);

    private sealed class Options
    {
        public required string ScenarioPath { get; init; }
        public required string Folder { get; init; }
        public required string OutDir { get; init; }
        public string? Mode { get; set; }
        public string? Decoder { get; set; }
        public int Repeat { get; set; } = 1;
        public string? Alias { get; set; }
        public string? Commit { get; set; }
    }

    // ---- Entry point -----------------------------------------------------------------------------

    public static async Task<int> RunAsync(string[] args)
    {
        Options options;
        Scenario scenario;
        string outDir, source, dataRoot, copyRoot;
        try
        {
            options = ParseArgs(args);
            scenario = LoadScenario(options.ScenarioPath);
            outDir = options.OutDir;
            source = options.Folder;
            dataRoot = Path.Combine(outDir, "data");
            copyRoot = Path.Combine(outDir, "copy");
            ValidatePaths(source, outDir, dataRoot, copyRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"perf-session: {ex.Message}");
            Console.Error.WriteLine("usage: --perf-session <scenario.json> <folder> <outDir> [--mode Fast|Preview|Original] [--repeat N] [--alias NAME] [--commit SHA]");
            return 2;
        }

        Directory.CreateDirectory(outDir);
        CreateTempDir(dataRoot);
        // Both must be set before the listener, AppSettings or any window/journal/session object exists.
        Environment.SetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE", outDir);
        Environment.SetEnvironmentVariable("PHOTOREVIEW_DATA_ROOT", dataRoot);
        if (!File.Exists(AppSettings.ConfigPath))
            Console.WriteLine($"WARNING: {AppSettings.ConfigPath} does not exist; AppSettings.Load() (app code) will create a default one.");

        var usesCopy = scenario.Copy is not null || scenario.Steps.Any(s => s.Action is not null);
        var sourceImageCount = Directory.EnumerateFiles(source).Count(ImageFileTypes.IsSupported);
        var exit = 0;
        try
        {
            exit = await WpfTestHost.RunAsync(async dispatcher =>
            {
                foreach (var note in WpfTestHost.Notes) Console.WriteLine($"host: {note}");
                var failures = 0;
                for (var iteration = 1; iteration <= options.Repeat; iteration++)
                {
                    var iterationDir = options.Repeat == 1 ? outDir : Path.Combine(outDir, $"iter-{iteration:00}");
                    Directory.CreateDirectory(iterationDir);
                    string folder = source;
                    if (usesCopy)
                    {
                        DeleteTempDir(copyRoot);
                        CreateTempDir(copyRoot);
                        folder = PrepareCopy(scenario, source, copyRoot);
                    }
                    try
                    {
                        var ok = await RunIterationAsync(dispatcher, scenario, options, folder, usesCopy, copyRoot,
                            sourceImageCount, iteration, iterationDir);
                        if (!ok) failures++;
                    }
                    finally
                    {
                        if (usesCopy) DeleteTempDir(copyRoot);
                    }
                }
                return failures == 0 ? 0 : 1;
            }, TimeSpan.FromHours(6));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL perf-session: {ex}");
            exit = 1;
        }
        finally
        {
            // The listener was disposed by WpfTestHost; the data root is no longer used by any window.
            AppLog.Shutdown();
            DeleteTempDir(dataRoot);
            DeleteTempDir(copyRoot);
        }
        Console.WriteLine(exit == 0 ? $"PASS: perf-session {scenario.Name} out={outDir}" : $"FAIL: perf-session {scenario.Name} out={outDir}");
        return exit;
    }

    // ---- One iteration ---------------------------------------------------------------------------

    private static async Task<bool> RunIterationAsync(Dispatcher dispatcher, Scenario scenario, Options options,
        string folder, bool usesCopy, string copyRoot, int sourceImageCount, int iteration, string iterationDir)
    {
        using var process = Process.GetCurrentProcess();
        var gcStart = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var pauseStart = GC.GetTotalPauseDuration();
        var cpuStart = process.TotalProcessorTime;
        var startUtc = DateTime.UtcNow;
        var startQpc = Stopwatch.GetTimestamp();
        var steps = new List<StepRecord>();
        var errors = new List<string>();
        var keysSent = 0;
        var keysHandled = 0;
        var idleTimeouts = 0;
        var keySettleMs = new List<double>();
        var keySettleTimeouts = 0;

        // AR02c: build the production DI graph (AppHost.BuildServices == App.ConfigureServices, no
        // test-root overrides) so --perf-session measures the shipped configuration (F2). SettingsStore
        // must be Load()-ed before MainWindow is resolved: PreviewImageService/PreloadScheduler are
        // singletons created while the MainViewModel dependency chain resolves (i.e. before MainWindow's
        // own constructor body runs), and they capture SettingsStore.Current by value at that point --
        // exactly the order App.App_Startup uses (store.Load() before GetRequiredService<MainWindow>()).
        using var services = AppHost.BuildServices();
        var settingsStore = services.GetRequiredService<SettingsStore>();
        settingsStore.Load();
        var window = services.GetRequiredService<MainWindow>();
        window.Width = 1920;
        window.Height = 1080;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 0;
        window.Top = 0;
        window.WindowState = WindowState.Normal;
        window.ShowActivated = false;
        window.SuppressWindowPlacement();
        var settings = window.Settings;
        if (!ReferenceEquals(settings, settingsStore.Current))
            throw new InvalidOperationException("AR02c invariant broken: window.Settings is not the DI SettingsStore.Current instance.");
        var configMode = settings.LoadingMode;
        if (options.Mode is not null && Enum.TryParse<LoadingMode>(options.Mode, true, out var m)) settings.LoadingMode = m; // in-memory only; config.json untouched
        if (options.Decoder is not null && Enum.TryParse<DecoderBackend>(options.Decoder, true, out var d)) settings.DecoderBackend = d; // in-memory only; PreviewImageService reads the backend live
        var forbiddenKeys = CollectForbiddenKeys(settings);
        foreach (var step in scenario.Steps.Where(s => s.Key is not null))
        {
            if (forbiddenKeys.Contains(ParseKey(step.Key!)))
            {
                window.Close();
                throw new InvalidOperationException($"key step '{step.Key}' maps to a file/folder/window action in the current config; use an 'action' step (runs on a copy) instead");
            }
        }
        var actionKey = scenario.Steps.FirstOrDefault(s => s.Action is not null)?.Action;
        var actionDestination = Path.Combine(copyRoot, "moved");
        // The user's real actions may point at absolute folders (e.g. another fixture). Replace them in
        // memory: no action at all unless the scenario uses one, then a single Move into <outDir>\copy\moved.
        settings.Actions = actionKey is null ? [] :
        [
            new ReviewAction { Name = "perf-session move", Shortcut = actionKey, Operation = FileOperationType.Move, Destination = actionDestination, Confirm = false },
        ];

        var dpi = 1.0;
        var fileCount = 0;
        try
        {
            window.Show();
            dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
            var stepNo = 0;
            foreach (var step in scenario.Steps)
            {
                stepNo++;
                var t0 = Stopwatch.GetTimestamp();
                string? detail = null;
                switch (step.Kind)
                {
                    case "open":
                        {
                            string? initial = null;
                            if (string.Equals(step.Open, "file", StringComparison.OrdinalIgnoreCase))
                            {
                                var sorted = ImageSortService.Sort(Directory.EnumerateFiles(folder).Where(ImageFileTypes.IsSupported), settings.ImageSortMode);
                                if (sorted.Count == 0) throw new InvalidOperationException("open file: folder has no images");
                                initial = sorted[Math.Clamp(step.Index ?? 0, 0, sorted.Count - 1)];
                                detail = $"file index={Math.Clamp(step.Index ?? 0, 0, sorted.Count - 1)}";
                            }
                            else detail = "folder";
                            await window.LoadFolderAsync(folder, initial);
                            fileCount = GetFiles(window).Count;
                            detail += $" files={fileCount}";
                            break;
                        }
                    case "key":
                        {
                            var key = ParseKey(step.Key!);
                            if (forbiddenKeys.Contains(key))
                                throw new InvalidOperationException($"key step '{key}' maps to a file/folder/window action; use an 'action' step (runs on a copy) instead");
                            var repeat = Math.Max(1, step.Repeat ?? 1);
                            if (step.Settle == true)
                            {
                                var settleMinMs = Math.Max(0, step.SettleMinMs ?? 50);
                                var settleMaxMs = Math.Max(settleMinMs, step.SettleMaxMs ?? step.IntervalMs ?? 3000);
                                var timeouts = 0;
                                for (var i = 0; i < repeat; i++)
                                {
                                    var presentedBefore = window.Metrics.Snapshot().PresentedImages;
                                    keysSent++;
                                    if (SendKey(window, key)) keysHandled++;
                                    var (settled, settleElapsedMs) = await WaitKeySettleAsync(
                                        dispatcher, window, presentedBefore, TimeSpan.FromMilliseconds(settleMaxMs));
                                    keySettleMs.Add(settleElapsedMs);
                                    if (!settled) { timeouts++; keySettleTimeouts++; }
                                    var remainingMs = settleMinMs - settleElapsedMs;
                                    if (remainingMs > 0) await Task.Delay((int)Math.Ceiling(remainingMs));
                                }
                                detail = $"{key} x{repeat} settle min={settleMinMs}ms max={settleMaxMs}ms timeouts={timeouts}";
                            }
                            else
                            {
                                for (var i = 0; i < repeat; i++)
                                {
                                    keysSent++;
                                    if (SendKey(window, key)) keysHandled++;
                                    if (step.IntervalMs is > 0) await Task.Delay(step.IntervalMs.Value);
                                    else await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                                }
                                detail = $"{key} x{repeat} @{step.IntervalMs ?? 0}ms";
                            }
                            break;
                        }
                    case "action":
                        {
                            var key = ParseKey(step.Action!);
                            if (!usesCopy) throw new InvalidOperationException("action steps require a temporary copy");
                            var repeat = Math.Max(1, step.Repeat ?? 1);
                            var done = 0;
                            for (var i = 0; i < repeat; i++)
                            {
                                if (GetFiles(window).Count == 0) { detail = "folder empty"; break; }
                                VerifyActionTarget(window, settings, folder, copyRoot);
                                keysSent++;
                                if (SendKey(window, key)) keysHandled++;
                                done++;
                                await WaitUntilAsync(() => !window.IsFileActionInProgress,
                                    TimeSpan.FromSeconds(30));
                                if (step.IntervalMs is > 0) await Task.Delay(step.IntervalMs.Value);
                            }
                            var moved = Directory.Exists(actionDestination) ? Directory.GetFiles(actionDestination).Length : 0;
                            detail = $"{key} x{done} @{step.IntervalMs ?? 0}ms moved={moved} remaining={GetFiles(window).Count}" + (detail is null ? "" : $" ({detail})");
                            break;
                        }
                    case "wait":
                        await Task.Delay(Math.Max(0, step.WaitMs!.Value));
                        detail = $"{step.WaitMs}ms";
                        break;
                    case "waitIdle":
                        {
                            var (idle, how) = await WaitIdleAsync(dispatcher, window, TimeSpan.FromMilliseconds(step.TimeoutMs ?? 30000));
                            if (!idle) idleTimeouts++;
                            detail = how;
                            break;
                        }
                    case "zoom":
                        {
                            var hold = Math.Max(0, step.HoldMs ?? 1000);
                            foreach (var level in step.Zoom!)
                            {
                                if (level <= 0)
                                {
                                    if (Enum.TryParse<Key>(settings.Shortcuts.ToggleFit, true, out var fitKey) && !forbiddenKeys.Contains(fitKey))
                                    {
                                        keysSent++;
                                        if (SendKey(window, fitKey)) keysHandled++;
                                    }
                                    else window.ResetFitView();
                                }
                                else window.SetZoom(level);
                                await Task.Delay(hold);
                            }
                            detail = $"[{string.Join(",", step.Zoom!)}] hold={hold}ms";
                            break;
                        }
                    case "pan":
                        {
                            var pan = step.Pan!;
                            var scroll = (ScrollViewer)window.FindName("ImageScroll")!;
                            var n = Math.Max(1, pan.Steps);
                            for (var i = 0; i < n; i++)
                            {
                                if (pan.Dx != 0) scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset + pan.Dx / n);
                                if (pan.Dy != 0) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + pan.Dy / n);
                                await Task.Delay(Math.Max(1, pan.IntervalMs));
                            }
                            detail = $"dx={pan.Dx} dy={pan.Dy} steps={n} extent={scroll.ExtentWidth:0}x{scroll.ExtentHeight:0}";
                            break;
                        }
                    default:
                        throw new InvalidOperationException($"step {stepNo}: unknown step");
                }
                var t1 = Stopwatch.GetTimestamp();
                steps.Add(new StepRecord(stepNo, step.Kind, t0, t1, PhotoReviewPerf.Ms(t0), detail));
                Console.WriteLine($"  [{iteration}] step {stepNo} {step.Kind} {detail} ({PhotoReviewPerf.Ms(t0):0} ms)");
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex is TargetInvocationException { InnerException: { } inner } ? inner.ToString() : ex.ToString());
            Console.Error.WriteLine($"  [{iteration}] ERROR {errors[^1]}");
        }

        var metrics = window.Metrics.Snapshot();
        // AR02c: effective configuration of the production graph this run actually used, so numbers
        // cannot be compared across the AR02c boundary (legacy test-root graph) by mistake (F2).
        var effectiveConfig = new
        {
            graph = "production",
            imageCacheCapacityBytes = settings.ImageCacheCapacityBytes,
            preloadWorkerCount = settings.PreloadWorkerCount,
            decoderBackend = settings.DecoderBackend.ToString(),
            useSourceBytesCache = settings.UseSourceBytesCache,
            sourceBytesCapacityBytes = settings.SourceBytesCapacityBytes,
            loadingMode = settings.LoadingMode.ToString(),
            preloadEnabled = true, // AR02a: production registers no IPreloadController override
            diskCacheEnabled = true, // AR02a: PreviewImageService/ThumbnailCache always get IAppPaths cache dirs
            targetDecodeWidth = services.GetRequiredService<PreviewStateContext>().TargetDecodeWidth(),
            dataRoot = "<outDir>\\data",
        };
        window.Close();
        var endQpc = Stopwatch.GetTimestamp();
        var endUtc = DateTime.UtcNow;
        var sortedKeySettleMs = keySettleMs.OrderBy(v => v).ToArray();

        process.Refresh();
        var processInfo = new
        {
            iteration,
            elapsedMs = PhotoReviewPerf.Ms(startQpc),
            peakWorkingSetBytes = process.PeakWorkingSet64,
            workingSetBytes = process.WorkingSet64,
            privateBytes = process.PrivateMemorySize64,
            managedHeapBytes = GC.GetTotalMemory(false),
            gcCount = new { gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2) },
            gcCountDelta = new { gen0 = GC.CollectionCount(0) - gcStart[0], gen1 = GC.CollectionCount(1) - gcStart[1], gen2 = GC.CollectionCount(2) - gcStart[2] },
            gcPauseTotalMs = GC.GetTotalPauseDuration().TotalMilliseconds,
            gcPauseDeltaMs = (GC.GetTotalPauseDuration() - pauseStart).TotalMilliseconds,
            cpuTotalMs = process.TotalProcessorTime.TotalMilliseconds,
            cpuDeltaMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
        };
        var sessionInfo = new
        {
            scenario = scenario.Name,
            scenarioFile = Path.GetFileName(options.ScenarioPath),
            iteration,
            repeat = options.Repeat,
            mode = settings.LoadingMode,
            modeSource = options.Mode is null ? "config" : "override",
            configMode,
            folderAlias = options.Alias,
            folderPathId = PathIdOf(options.Folder),
            openedCopy = usesCopy,
            sourceImageCount,
            openedImageCount = fileCount,
            commit = options.Commit,
            entryVersion = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            appVersion = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            effectiveConfig,
            env = DiagEnvironment(),
            window = new { width = 1920, height = 1080, left = 0, top = 0, dpiScale = dpi, showActivated = false, foregroundNotGuaranteed = true },
            keyDelivery = "WPF routed PreviewKeyDown (+KeyDown if unhandled) raised on the window in-process; no OS input queue",
            keysSent,
            keysHandled,
            idleTimeouts,
            keySettle = keySettleMs.Count == 0 ? null : new
            {
                count = keySettleMs.Count,
                p50Ms = Math.Round(Percentile(sortedKeySettleMs, 50), 1),
                p95Ms = Math.Round(Percentile(sortedKeySettleMs, 95), 1),
                timeouts = keySettleTimeouts,
            },
            startUtc,
            endUtc,
            startQpc,
            endQpc,
            qpcFrequency = Stopwatch.Frequency,
            hostNotes = WpfTestHost.Notes,
            steps,
            errors,
        };
        await File.WriteAllTextAsync(Path.Combine(iterationDir, "metrics.json"), JsonSerializer.Serialize(metrics, WriteOptions));
        await File.WriteAllTextAsync(Path.Combine(iterationDir, "process.json"), JsonSerializer.Serialize(processInfo, WriteOptions));
        await File.WriteAllTextAsync(Path.Combine(iterationDir, "session.json"), JsonSerializer.Serialize(sessionInfo, WriteOptions));
        Console.WriteLine($"  [{iteration}] config: graph={effectiveConfig.graph} cacheBytes={effectiveConfig.imageCacheCapacityBytes} " +
            $"preloadWorkers={effectiveConfig.preloadWorkerCount} decoder={effectiveConfig.decoderBackend} " +
            $"sourceBytesCache={effectiveConfig.useSourceBytesCache} loadingMode={effectiveConfig.loadingMode} " +
            $"preload={effectiveConfig.preloadEnabled} diskCache={effectiveConfig.diskCacheEnabled} targetDecodeWidth={effectiveConfig.targetDecodeWidth}");
        Console.WriteLine($"  [{iteration}] done keys={keysHandled}/{keysSent} presented={metrics.PresentedImages} hits={metrics.CacheHits} misses={metrics.CacheMisses} " +
            $"crossThreadPresents={metrics.CrossThreadPresentCount} peakWS={processInfo.peakWorkingSetBytes / (1024 * 1024)}MB errors={errors.Count}");
        return errors.Count == 0;
    }

    // ---- Input -----------------------------------------------------------------------------------

    /// <summary>
    /// Delivers one key press as WPF routed events inside this process. MainWindow wires its handler to
    /// PreviewKeyDown (MainWindow.xaml), so PreviewKeyDown is raised first and KeyDown only when unhandled.
    /// <see cref="Keyboard.PrimaryDevice"/> is only referenced as the event's device object and
    /// <see cref="PresentationSource.FromVisual"/> only looks up the window's HwndSource; neither posts
    /// anything to the OS input queue, changes focus or activates the window. Timestamp = TickCount, so
    /// KeyInput's inputDelay reflects only this call, not real OS input latency.
    /// </summary>
    private static bool SendKey(Window window, Key key)
    {
        var source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("window has no PresentationSource (not shown?)");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(args);
        if (!args.Handled)
        {
            args.RoutedEvent = Keyboard.KeyDownEvent;
            window.RaiseEvent(args);
        }
        return args.Handled;
    }

    private static Key ParseKey(string value) =>
        Enum.TryParse<Key>(value, true, out var key) && key != Key.None ? key : throw new FormatException($"unknown key '{value}'");

    /// <summary>Keys that touch files, other folders, or the window itself; never sent by a plain key step.</summary>
    private static HashSet<Key> CollectForbiddenKeys(AppSettings settings)
    {
        var keys = new HashSet<Key> { Key.Escape, Key.System };
        var s = settings.Shortcuts;
        foreach (var name in new[] { s.SendToRecycleBin, s.Undo, s.NextFolder, s.PreviousFolder, s.Fullscreen, s.MoveToFolder2 })
            if (Enum.TryParse<Key>(name, true, out var k)) keys.Add(k);
        foreach (var action in settings.Actions ?? [])
            if (Enum.TryParse<Key>(action.Shortcut, true, out var k)) keys.Add(k);
        return keys;
    }

    /// <summary>Re-checks, right before each action key, that everything it can touch lives in the copy.</summary>
    private static void VerifyActionTarget(MainWindow window, AppSettings settings, string copyFolder, string copyRoot)
    {
        var files = GetFiles(window);
        var index = window.CurrentIndex;
        if (index < 0 || index >= files.Count) throw new InvalidOperationException("action: no current image");
        var compareSelected = window.CompareSelectedPath;
        foreach (var path in new[] { files[index], compareSelected })
        {
            if (path is null) continue;
            if (!IsUnder(path, copyFolder)) throw new InvalidOperationException($"action target is outside the temporary copy: {path}");
        }
        if (files.Any(f => !IsUnder(f, copyFolder))) throw new InvalidOperationException("action: catalog contains files outside the temporary copy");
        foreach (var action in settings.Actions)
        {
            if (!Path.IsPathRooted(action.Destination) || !IsUnder(action.Destination, copyRoot))
                throw new InvalidOperationException($"action destination is outside the temporary copy: {action.Destination}");
        }
    }

    // ---- Waiting ---------------------------------------------------------------------------------

    private static async Task<(bool Idle, string How)> WaitIdleAsync(Dispatcher dispatcher, MainWindow window, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var last = window.Metrics.Snapshot();
        var stableSince = sw.Elapsed;
        while (sw.Elapsed < timeout)
        {
            await Task.Delay(250);
            var now = window.Metrics.Snapshot();
            if (last is not null && now is not null && !MetricsEquivalent(now, last)) { last = now; stableSince = sw.Elapsed; }
            var stable = sw.Elapsed - stableSince;
            var preloadDone = true;
            var actionIdle = !window.IsFileActionInProgress;
            if (actionIdle && stable >= TimeSpan.FromSeconds(1) && (preloadDone || stable >= TimeSpan.FromSeconds(5)))
            {
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                return (true, $"idle after {sw.ElapsedMilliseconds}ms ({(preloadDone ? "preload done" : "metrics stable 5s")})");
            }
        }
        return (false, $"TIMEOUT after {sw.ElapsedMilliseconds}ms (continuing)");
    }

    /// <summary>
    /// Event-driven settle for one key step (rule 1 of the faster-harness plan): polls every ~10ms,
    /// no fixed delay and no 5s "metrics stable" fallback -- <paramref name="max"/> (settleMaxMs) is the
    /// only cap. Settled once this key's image has been presented (PresentedImages advanced past
    /// <paramref name="presentedBefore"/>, taken right before the key was sent) and preload has gone
    /// idle (<see cref="PreloadScheduler.IsIdle"/> via <see cref="IPreloadController"/>, reached through
    /// the production DI graph's <c>MainViewModel.PreloadController</c>). The preload kick for a
    /// navigation is issued synchronously before its PresentedImages increment (see
    /// <c>ImagePresenter.ShowImageAsync</c>), so by the time "presented" is observed the scheduler
    /// already reflects this key's preload lifetime, not a stale one from an earlier key.
    /// </summary>
    private static async Task<(bool Settled, double ElapsedMs)> WaitKeySettleAsync(
        Dispatcher dispatcher, MainWindow window, long presentedBefore, TimeSpan max)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var presented = window.Metrics.Snapshot().PresentedImages > presentedBefore;
            var preloadIdle = window.ViewModel.PreloadController?.IsIdle ?? true;
            if (presented && preloadIdle)
            {
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                return (true, sw.Elapsed.TotalMilliseconds);
            }
            if (sw.Elapsed >= max) return (false, sw.Elapsed.TotalMilliseconds);
            await Task.Delay(10);
        }
    }

    /// <summary>Linear-interpolated percentile (nearest-rank would step too coarsely for small N here).</summary>
    private static double Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0) return 0;
        if (sortedValues.Length == 1) return sortedValues[0];
        var rank = percentile / 100.0 * (sortedValues.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sortedValues[lower];
        return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * (rank - lower);
    }

    private static bool MetricsEquivalent(ReviewMetricsSnapshot a, ReviewMetricsSnapshot b) =>
        a.CacheHits == b.CacheHits &&
        a.CacheMisses == b.CacheMisses &&
        a.SourceBytesRead == b.SourceBytesRead &&
        a.SourceReads == b.SourceReads &&
        a.DecodeMilliseconds == b.DecodeMilliseconds &&
        a.PresentedImages == b.PresentedImages &&
        a.PresentMilliseconds == b.PresentMilliseconds &&
        a.PreloadHits == b.PreloadHits &&
        a.InflightJoins == b.InflightJoins &&
        a.DiskCacheHits == b.DiskCacheHits &&
        a.QueueWaitMilliseconds == b.QueueWaitMilliseconds &&
        a.UiAssignMilliseconds == b.UiAssignMilliseconds &&
        a.SourceOpenCount == b.SourceOpenCount &&
        a.StatCount == b.StatCount &&
        a.SessionWriteCount == b.SessionWriteCount &&
        a.CrossThreadPresentCount == b.CrossThreadPresentCount &&
        a.DecoderFallbackCount == b.DecoderFallbackCount;

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    private static IReadOnlyList<string> GetFiles(MainWindow window) =>
        window.Files;

    // ---- Copies and paths ------------------------------------------------------------------------

    private static string PrepareCopy(Scenario scenario, string source, string copyRoot)
    {
        var images = Path.Combine(copyRoot, "images");
        Directory.CreateDirectory(images);
        var sorted = ImageSortService.Sort(Directory.EnumerateFiles(source).Where(ImageFileTypes.IsSupported), "Name");
        var spec = scenario.Copy ?? new CopySpec { Kind = "files" };
        var copied = 0;
        if (string.Equals(spec.Kind, "compare", StringComparison.OrdinalIgnoreCase))
        {
            var pairs = Math.Min(spec.Pairs ?? 20, sorted.Count);
            for (var i = 0; i < pairs; i++)
            {
                var ext = Path.GetExtension(sorted[i]);
                File.Copy(sorted[i], Path.Combine(images, $"pair{i:000}{ext}"));
                File.Copy(sorted[i], Path.Combine(images, $"pair{i:000} (1){ext}"));
                copied += 2;
            }
        }
        else
        {
            var actions = scenario.Steps.Where(s => s.Action is not null).Sum(s => Math.Max(1, s.Repeat ?? 1));
            var count = Math.Min(spec.Count ?? actions + 10, sorted.Count);
            foreach (var file in sorted.Take(count))
            {
                File.Copy(file, Path.Combine(images, Path.GetFileName(file)));
                copied++;
            }
        }
        if (copied == 0) throw new InvalidOperationException("copy: source folder has no images");
        Console.WriteLine($"  copy: {copied} file(s) -> {images}");
        return images;
    }

    private static void ValidatePaths(string source, string outDir, string dataRoot, string copyRoot)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        if (IsUnder(outDir, source) || IsUnder(source, outDir))
            throw new InvalidOperationException("outDir and the source folder must not contain each other");
        var appRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview");
        if (IsUnder(outDir, appRoot) || IsUnder(appRoot, outDir))
            throw new InvalidOperationException($"outDir must not overlap the real app data folder {appRoot}");
        foreach (var dir in new[] { dataRoot, copyRoot })
        {
            if (Directory.Exists(dir) && !File.Exists(Path.Combine(dir, TempMarker)))
                throw new InvalidOperationException($"{dir} already exists and was not created by perf-session; refusing to use/delete it");
        }
    }

    private static void CreateTempDir(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, TempMarker), "created by --perf-session; deleted at exit\n");
    }

    /// <summary>Deletes only directories this driver created (marker file present).</summary>
    private static void DeleteTempDir(string dir)
    {
        if (!Directory.Exists(dir) || !File.Exists(Path.Combine(dir, TempMarker))) return;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { Directory.Delete(dir, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 9) Console.Error.WriteLine($"WARNING: could not delete {dir}: {ex.Message}");
                else Thread.Sleep(200);
            }
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static string PathIdOf(string path)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    private static SortedDictionary<string, string> DiagEnvironment()
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || !name.StartsWith("PHOTOREVIEW_", StringComparison.OrdinalIgnoreCase)) continue;
            var upper = name.ToUpperInvariant();
            // Both paths are set by this driver; record them relative to outDir (no personal paths).
            result[upper] = upper switch
            {
                "PHOTOREVIEW_PERF_TRACE" => "<outDir>",
                "PHOTOREVIEW_DATA_ROOT" => "<outDir>\\data",
                _ => entry.Value?.ToString() ?? "",
            };
        }
        return result;
    }

    // ---- Parsing ---------------------------------------------------------------------------------

    private static Options ParseArgs(string[] args)
    {
        // args[0] == "--perf-session"
        if (args.Length < 4) throw new ArgumentException("missing arguments");
        var options = new Options
        {
            ScenarioPath = Path.GetFullPath(args[1]),
            Folder = Path.GetFullPath(args[2]),
            OutDir = Path.GetFullPath(args[3]),
        };
        for (var i = 4; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i].ToLowerInvariant())
            {
                case "--mode":
                    var mode = Next();
                    if (!Enum.TryParse<LoadingMode>(mode, true, out var parsedMode)) throw new ArgumentException($"invalid mode '{mode}' (Fast|Preview|Original)");
                    options.Mode = parsedMode.ToString();
                    break;
                case "--repeat":
                    options.Repeat = int.TryParse(Next(), out var r) && r is >= 1 and <= 1000 ? r : throw new ArgumentException("--repeat must be 1..1000");
                    break;
                case "--decoder":
                    var decoder = Next();
                    if (!Enum.TryParse<DecoderBackend>(decoder, true, out var parsedDecoder) || !Enum.IsDefined(parsedDecoder)) throw new ArgumentException($"invalid decoder '{decoder}' ({string.Join('|', Enum.GetNames<DecoderBackend>())})");
                    options.Decoder = parsedDecoder.ToString();
                    break;
                case "--alias": options.Alias = Next(); break;
                case "--commit": options.Commit = Next(); break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
        }
        return options;
    }

    internal static Scenario LoadScenario(string path)
    {
        var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), ReadOptions)
            ?? throw new FormatException("empty scenario");
        if (string.IsNullOrWhiteSpace(scenario.Name)) scenario.Name = Path.GetFileNameWithoutExtension(path);
        if (scenario.Steps.Count == 0) throw new FormatException("scenario has no steps");
        for (var i = 0; i < scenario.Steps.Count; i++)
        {
            var step = scenario.Steps[i];
            if (step.Kind == "unknown") throw new FormatException($"step {i + 1}: no recognized property");
            if (step.Key is not null) ParseKey(step.Key);
            if (step.Action is not null) ParseKey(step.Action);
            if (step.Open is not null && step.Open is not ("folder" or "file"))
                throw new FormatException($"step {i + 1}: open must be \"folder\" or \"file\"");
            if (step.Settle is not null && step.Key is null)
                throw new FormatException($"step {i + 1}: \"settle\" is only valid on a \"key\" step");
            if (step.SettleMinMs is < 0) throw new FormatException($"step {i + 1}: settleMinMs must be >= 0");
            if (step.SettleMaxMs is <= 0) throw new FormatException($"step {i + 1}: settleMaxMs must be > 0");
            if (step.SettleMinMs is not null && step.SettleMaxMs is not null && step.SettleMinMs > step.SettleMaxMs)
                throw new FormatException($"step {i + 1}: settleMinMs must be <= settleMaxMs");
        }
        var actionKeys = scenario.Steps.Where(s => s.Action is not null).Select(s => ParseKey(s.Action!)).Distinct().Count();
        if (actionKeys > 1) throw new FormatException("only one action key per scenario is supported");
        return scenario;
    }
}
