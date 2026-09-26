using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;
using Xunit.Abstractions;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Measurement (not a gate): drag-pan and kinetic glide on a real zoomed photo in the real, shown
/// <see cref="MainWindow"/>, driven in-process through <see cref="MainWindow.PointerInput"/> (no OS input
/// injection). Records every CompositionTarget.Rendering tick (wall time, RenderingTime, the scroll offsets the
/// previous frame laid out, layout end, GC) and reports frame pacing, per-frame step evenness and UI cost.
/// </summary>
/// <remarks>
/// Set PHOTOREVIEW_KINETIC_IMAGE to a real photo (read only; nothing is written next to it). Optional:
/// PHOTOREVIEW_KINETIC_PRELOAD=1 (real preload graph instead of none), PHOTOREVIEW_KINETIC_CSV=&lt;file&gt; (raw ticks),
/// PHOTOREVIEW_KINETIC_TRIALS=N (default 5), PHOTOREVIEW_KINETIC_MOVE_HZ (simulated mouse rate, default 125).
/// </remarks>
[Collection("GlobalState")]
[Trait("Category", "Manual")]
public sealed class KineticPanFrameMeasurementTests(ITestOutputHelper output)
{
    private const string DisableDiskCacheVariable = "PHOTOREVIEW_DIAG_DISABLE_DISKCACHE";
    private static readonly TimeSpan PresentTimeout = TimeSpan.FromSeconds(30);

    // Pointer velocity during the drag (DIP/ms): the image scrolls right/down (offsets grow).
    private const double DragVelocityX = -1.4;
    private const double DragVelocityY = -0.7;
    private const double DragMs = 350;

    [Fact]
    public async Task Measure_DragAndGlide_FramePacing()
    {
        var path = Environment.GetEnvironmentVariable("PHOTOREVIEW_KINETIC_IMAGE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            output.WriteLine("PHOTOREVIEW_KINETIC_IMAGE is not set to an existing photo; nothing measured.");
            return;
        }
        var preload = Environment.GetEnvironmentVariable("PHOTOREVIEW_KINETIC_PRELOAD") == "1";
        var trials = int.TryParse(Environment.GetEnvironmentVariable("PHOTOREVIEW_KINETIC_TRIALS"), out var t) ? t : 5;
        var moveHz = double.TryParse(Environment.GetEnvironmentVariable("PHOTOREVIEW_KINETIC_MOVE_HZ"), CultureInfo.InvariantCulture, out var hz) ? hz : 125;
        var csvPath = Environment.GetEnvironmentVariable("PHOTOREVIEW_KINETIC_CSV");

        using var dataRoot = new DataRootFixture();
        using var noDiskCache = new EnvironmentScope(DisableDiskCacheVariable, "1");
        using var timer = new TimerResolutionScope();
        MainWindow? window = null;
        var presented = new List<string>();
        var csv = new StringBuilder("trial,phase,tick,wallMs,renderingMs,h,v,gen0,gen2,moves,layoutEndMs,opMs\n");
        var byMode = Modes.ToDictionary(m => m, _ => (Drag: new List<PhaseStats>(), Glide: new List<PhaseStats>()));
        var perceivedByMode = Modes.ToDictionary(m => m, _ => PresentLatencyModelsMs.Select(_ => new List<Perceived>()).ToArray());

        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                window = TestAppHost.CreateMainWindow(null, new TestHostHooks { OnPresented = presented.Add, DisablePreload = !preload });
                window.Settings.KineticPanEnabled = true;
                window.Width = 1200;
                window.Height = 800;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = double.TryParse(Environment.GetEnvironmentVariable("PHOTOREVIEW_KINETIC_LEFT"), CultureInfo.InvariantCulture, out var left) ? left : 40;
                window.Top = double.TryParse(Environment.GetEnvironmentVariable("PHOTOREVIEW_KINETIC_TOP"), CultureInfo.InvariantCulture, out var top) ? top : 40;
                window.Show();
                _ = window.ViewModel.OpenPathAsync(path);
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, PresentTimeout), "The image was never presented.");
                window.SetZoom(1.0);
                var zoomDetail = window.ViewModel.Presenter.ZoomDetail;
                Assert.True(await StaTestHost.WaitForAsync(() => zoomDetail.IsShowingOriginal, PresentTimeout), "The original never replaced the preview.");
                await Frames(10);

                var dpi = VisualTreeHelper.GetDpi(window);
                var refresh = DwmTiming.Query();
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"image {Path.GetFileName(path)}; DPI scale {dpi.DpiScaleX:0.###}; DWM refresh {refresh.RefreshHz:0.###} Hz (period {refresh.PeriodMs:0.###} ms); " +
                    $"extent {window.ImageScroll.ExtentWidth:0}x{window.ImageScroll.ExtentHeight:0} viewport {window.ImageScroll.ViewportWidth:0}x{window.ImageScroll.ViewportHeight:0}; " +
                    $"preload {(preload ? "on" : "off")}; mouse {moveHz:0} Hz; RenderCapability.Tier {RenderCapability.Tier >> 16}"));

                for (var trial = 0; trial < trials * Modes.Length; trial++)
                {
                    var mode = Modes[trial % Modes.Length];
                    PhaseStats drag, glide;
                    string rows;
                    List<Perceived> seen;
                    window.Settings.KineticGlideSmoothing = SmoothingFor(mode);
                    using (new ModeScope(mode)) (drag, glide, rows, seen) = await RunTrialAsync(window, trial, moveHz, refresh.PeriodMs);
                    byMode[mode].Drag.Add(drag);
                    byMode[mode].Glide.Add(glide);
                    for (var l = 0; l < seen.Count; l++) perceivedByMode[mode][l].Add(seen[l]);
                    csv.Append(rows);
                    output.WriteLine($"trial {trial} [{mode}]: GLIDE {glide}");
                    output.WriteLine($"trial {trial} [{mode}]: SEEN  {seen[0]}");
                    await Frames(20);
                }
                var monitorTiming = PhotoReview.Platform.Windows.WindowsDisplayClock.Instance.GetTiming(new System.Windows.Interop.WindowInteropHelper(window).Handle);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"monitor under the window: {(monitorTiming is { } mt ? Stopwatch.Frequency / (double)mt.RefreshPeriod : double.NaN):0.000} Hz (vblank observer)"));
            }, TimeSpan.FromMinutes(5));
        }
        finally
        {
            if (window is not null) await StaTestHost.RunAsync(() => { window.Close(); return Task.CompletedTask; });
        }

        foreach (var mode in Modes)
        {
            output.WriteLine($"[{mode}] DRAG  total: " + PhaseStats.Combine(byMode[mode].Drag));
            output.WriteLine($"[{mode}] GLIDE total: " + PhaseStats.Combine(byMode[mode].Glide));
            for (var l = 0; l < PresentLatencyModelsMs.Length; l++)
                output.WriteLine($"[{mode}] SEEN(lat {PresentLatencyModelsMs[l]:0.0} ms) total: " + Perceived.Combine(perceivedByMode[mode][l]));
        }
        if (!string.IsNullOrWhiteSpace(csvPath)) File.WriteAllText(csvPath, csv.ToString());
    }

    private static async Task<(PhaseStats Drag, PhaseStats Glide, string Csv, List<Perceived> Seen)> RunTrialAsync(MainWindow window, int trial, double moveHz, double periodMs)
    {
        var scroll = window.ImageScroll;
        var pointer = window.PointerInput;
        scroll.ScrollToHorizontalOffset(scroll.ScrollableWidth * 0.1);
        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * 0.1);
        await Frames(5);
        // The vblank grid of the monitor under the window (every mode, so the observer thread runs in all of them).
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        for (var warm = 0; warm < 200 && PhotoReview.Platform.Windows.WindowsDisplayClock.Instance.GetTiming(hwnd) is null; warm++) await Frames(1);

        var ticks = new List<Tick>(1024);
        var clock = Stopwatch.StartNew();
        var startTimestamp = Stopwatch.GetTimestamp();
        double lastLayoutEnd = 0;
        var moves = 0;
        var phase = "pre";
        var released = false;
        // UI cost of a frame: the dispatcher operation that raised the Rendering tick (Rendering handlers + layout +
        // render walk), timed by the dispatcher hooks. A tick raised outside any operation (a window message) keeps NaN.
        var opStart = double.NaN;
        var opTick = -1;
        var hooks = StaTestHost.Dispatcher.Hooks;
        DispatcherHookEventHandler opStarted = (_, _) => { opStart = clock.Elapsed.TotalMilliseconds; opTick = -1; };
        DispatcherHookEventHandler opCompleted = (_, _) =>
        {
            if (opTick >= 0 && !double.IsNaN(opStart)) ticks[opTick] = ticks[opTick] with { OpMs = clock.Elapsed.TotalMilliseconds - opStart };
            opStart = double.NaN;
            opTick = -1;
        };
        var glideStartedAt = double.NaN;
        var lastChangeAt = 0.0;
        PhotoReview.Core.Abstractions.DisplayTiming? lastTiming = null;
        var glideDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onLayout = (_, _) => lastLayoutEnd = clock.Elapsed.TotalMilliseconds;
        EventHandler onRender = (_, e) =>
        {
            var args = (RenderingEventArgs)e;
            var now = clock.Elapsed.TotalMilliseconds;
            // Keeps the monitor's vblank observer running in every mode (it stops after 1.5 s without a query).
            if (PhotoReview.Platform.Windows.WindowsDisplayClock.Instance.GetTiming(hwnd) is { } observed) lastTiming = observed;
            if (ticks.Count > 0 && (ticks[^1].H != scroll.HorizontalOffset || ticks[^1].V != scroll.VerticalOffset)) lastChangeAt = now;
            if (!double.IsNaN(glideStartedAt) && (now - Math.Max(lastChangeAt, glideStartedAt) >= 250 || now - glideStartedAt > 5000)) glideDone.TrySetResult();
            ticks.Add(new Tick(phase, clock.Elapsed.TotalMilliseconds, args.RenderingTime.TotalMilliseconds,
                scroll.HorizontalOffset, scroll.VerticalOffset, GC.CollectionCount(0), GC.CollectionCount(2), moves, lastLayoutEnd));
            if (opTick < 0) opTick = ticks.Count - 1;
            moves = 0;
        };
        hooks.OperationStarted += opStarted;
        hooks.OperationCompleted += opCompleted;
        scroll.LayoutUpdated += onLayout;
        CompositionTarget.Rendering += onRender;
        try
        {
            await Frames(3);
            var start = new Point(700, 450);
            phase = "drag";
            Assert.True(pointer.OnImagePress(MouseButton.Left, 1, start, 0), "The press was not taken as a pan (image not pannable?).");
            var pressAt = clock.Elapsed.TotalMilliseconds;

            // Mouse moves posted at Input priority from a background thread at moveHz, like WM_MOUSEMOVE arriving between frames.
            var dispatcher = StaTestHost.Dispatcher;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var interval = 1000.0 / moveHz;
            var feeder = new Thread(() =>
            {
                var next = pressAt + interval;
                while (true)
                {
                    while (clock.Elapsed.TotalMilliseconds < next)
                    {
                        if (next - clock.Elapsed.TotalMilliseconds > 2) Thread.Yield(); else Thread.SpinWait(50);
                    }
                    var at = next - pressAt;
                    var last = at >= DragMs;
                    var point = new Point(start.X + DragVelocityX * at, start.Y + DragVelocityY * at);
                    var stamp = (int)Math.Round(at);
                    dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                    {
                        moves++;
                        pointer.OnImageMove(true, point, stamp);
                        if (last)
                        {
                            phase = "glide";
                            released = true;
                            pointer.OnImageRelease(point, stamp);
                            done.TrySetResult();
                        }
                    });
                    if (last) return;
                    next += interval;
                }
            }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            feeder.Start();
            await done.Task;

            // Glide until the offsets have not changed for 250 ms of wall time (or 5 s), judged inside the one Rendering
            // subscription: re-subscribing to CompositionTarget.Rendering per frame makes WPF post extra render ticks.
            glideStartedAt = clock.Elapsed.TotalMilliseconds;
            await glideDone.Task;
            Assert.True(released);
        }
        finally
        {
            CompositionTarget.Rendering -= onRender;
            scroll.LayoutUpdated -= onLayout;
            hooks.OperationStarted -= opStarted;
            hooks.OperationCompleted -= opCompleted;
            pointer.StopKinetic();
        }

        var csv = new StringBuilder();
        for (var i = 0; i < ticks.Count; i++)
        {
            var k = ticks[i];
            csv.Append(string.Create(CultureInfo.InvariantCulture,
                $"{trial},{k.Phase},{i},{k.WallMs:0.000},{k.RenderingMs:0.000},{k.H:0.0000},{k.V:0.0000},{k.Gen0},{k.Gen2},{k.Moves},{k.LayoutEndMs:0.000},{k.OpMs:0.000}\n"));
        }
        // The vblank grid in this trial's clock: DWM's last vblank (QPC) after the glide, stepped back by the period.
        var timing = lastTiming;
        var ticksPerMs = Stopwatch.Frequency / 1000.0;
        var seen = new List<Perceived>();
        foreach (var latency in PresentLatencyModelsMs)
        {
            seen.Add(timing is { } t
                ? Perceived.From(ticks, (t.LastVBlank - startTimestamp) / ticksPerMs, t.RefreshPeriod / ticksPerMs, latency)
                : Perceived.Empty);
        }
        return (PhaseStats.From(ticks, "drag", periodMs), PhaseStats.From(ticks, "glide", periodMs), csv.ToString(), seen);
    }

    /// <summary>
    /// Control: the same <see cref="PhotoReview.App.Input.KineticScroller"/> glide driven by the same RenderingTime
    /// logic, moving a small rectangle (TranslateTransform) in an otherwise empty window: no ScrollViewer, no photo.
    /// Pacing seen here is WPF/DWM on this machine, not the viewer.
    /// </summary>
    [Fact]
    public async Task Measure_TrivialScene_FramePacing()
    {
        var trials = int.TryParse(Environment.GetEnvironmentVariable("PHOTOREVIEW_KINETIC_TRIALS"), out var t) ? t : 5;
        var sceneByMode = Modes.ToDictionary(m => m, _ => new List<PhaseStats>());
        using var timer = new TimerResolutionScope();
        Window? window = null;
        await StaTestHost.RunAsync(async () =>
        {
            var translate = new TranslateTransform();
            var canvas = new System.Windows.Controls.Canvas { Background = Brushes.Black };
            canvas.Children.Add(new System.Windows.Shapes.Rectangle { Width = 200, Height = 200, Fill = Brushes.OrangeRed, RenderTransform = translate });
            window = new Window
            {
                Width = 1200, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual, Content = canvas,
                Left = double.TryParse(Environment.GetEnvironmentVariable("PHOTOREVIEW_KINETIC_LEFT"), CultureInfo.InvariantCulture, out var left) ? left : 40,
                Top = double.TryParse(Environment.GetEnvironmentVariable("PHOTOREVIEW_KINETIC_TOP"), CultureInfo.InvariantCulture, out var top) ? top : 40,
            };
            window.Show();
            await Frames(10);
            var refresh = DwmTiming.Query();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"scene trivial; DWM refresh {refresh.RefreshHz:0.###} Hz"));
            for (var trial = 0; trial < trials * Modes.Length; trial++)
            {
                var mode = Modes[trial % Modes.Length];
                using var scope = new ModeScope(mode);
                translate.X = 0;
                translate.Y = 0;
                var ticks = new List<Tick>(1024);
                var clock = Stopwatch.StartNew();
                var kinetic = new PhotoReview.App.Input.KineticScroller();
                kinetic.Start(-1.4, -0.7);
                var bounds = new PhotoReview.App.Input.ScrollBounds(100000, 100000, 0, 0);
                var hasFrame = false;
                var last = TimeSpan.Zero;
                var stoppedAt = double.NaN;
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler onRender = (_, e) =>
                {
                    var args = (RenderingEventArgs)e;
                    var now = clock.Elapsed.TotalMilliseconds;
                    ticks.Add(new Tick("glide", now, args.RenderingTime.TotalMilliseconds, translate.X, translate.Y, GC.CollectionCount(0), GC.CollectionCount(2), 0, 0));
                    if (!kinetic.IsActive)
                    {
                        if (double.IsNaN(stoppedAt)) stoppedAt = now;
                        if (now - stoppedAt > 100) done.TrySetResult();
                        return;
                    }
                    if (!hasFrame) { hasFrame = true; last = args.RenderingTime; return; }
                    var elapsed = (args.RenderingTime - last).TotalMilliseconds;
                    if (elapsed <= 0) return;
                    last = args.RenderingTime;
                    var (x, y) = kinetic.Step(elapsed, translate.X, translate.Y, bounds);
                    translate.X = x;
                    translate.Y = y;
                };
                CompositionTarget.Rendering += onRender;
                await done.Task;
                CompositionTarget.Rendering -= onRender;
                var stats = PhaseStats.From(ticks, "glide", refresh.PeriodMs);
                sceneByMode[mode].Add(stats);
                output.WriteLine($"trial {trial} [{mode}]: GLIDE {stats}");
                await Frames(10);
            }
            window.Close();
        }, TimeSpan.FromMinutes(2));
        foreach (var mode in Modes) output.WriteLine($"[{mode}] scene GLIDE total: " + PhaseStats.Combine(sceneByMode[mode]));
    }
    /// <summary>
    /// PHOTOREVIEW_KINETIC_MODES (comma list, trials interleaved so machine load hits every mode alike):
    /// base; prio (process AboveNormal + UI thread Highest); spin (UI thread kept busy by a self-reposting
    /// Background operation); timer (1 ms timer resolution).
    /// </summary>
    private static readonly string[] Modes =
        (Environment.GetEnvironmentVariable("PHOTOREVIEW_KINETIC_MODES") ?? "base").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed class ModeScope : IDisposable
    {
        private readonly string _mode;
        private readonly ProcessPriorityClass _class;
        private readonly ThreadPriority _thread;
        private bool _spinning;

        public ModeScope(string mode)
        {
            _mode = mode;
            _class = Process.GetCurrentProcess().PriorityClass;
            _thread = Thread.CurrentThread.Priority;
            switch (mode)
            {
                case "prio":
                    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
                    Thread.CurrentThread.Priority = ThreadPriority.Highest;
                    break;
                case "uiprio":
                    Thread.CurrentThread.Priority = ThreadPriority.Highest;
                    break;
                case "spin":
                    _spinning = true;
                    Spin();
                    break;
                case "timer":
                    _ = TimerResolutionScope.Begin();
                    break;
                case "storm":
                    // Re-subscribing to CompositionTarget.Rendering from inside a tick makes WPF post another render
                    // tick at once (the effect seen in the first harness version): the UI thread never waits for a frame.
                    _storm = (_, _) =>
                    {
                        CompositionTarget.Rendering -= _storm;
                        if (_spinning) CompositionTarget.Rendering += _storm;
                    };
                    _spinning = true;
                    CompositionTarget.Rendering += _storm;
                    break;
            }
        }

        private EventHandler? _storm;

        private void Spin()
        {
            if (_spinning) StaTestHost.Dispatcher.BeginInvoke(DispatcherPriority.Background, Spin);
        }

        public void Dispose()
        {
            _spinning = false;
            if (_storm is not null) CompositionTarget.Rendering -= _storm;
            Process.GetCurrentProcess().PriorityClass = _class;
            Thread.CurrentThread.Priority = _thread;
            if (_mode == "timer") TimerResolutionScope.End();
        }
    }

    /// <summary>
    /// Assumed time from the end of the UI frame (the dispatcher operation that stepped the glide) until the render
    /// thread's frame can be picked up by the compositor. Unknown on a real machine, so each is reported.
    /// </summary>
    private static readonly double[] PresentLatencyModelsMs = [0.5, 2.0, 4.0];

    private static PhotoReview.Core.Model.KineticGlideSmoothing SmoothingFor(string mode) => mode switch
    {
        "predict" => PhotoReview.Core.Model.KineticGlideSmoothing.Predict,
        _ => PhotoReview.Core.Model.KineticGlideSmoothing.Off,
    };

    /// <summary>
    /// Approximate perceived judder of a glide. Model: a frame whose UI work ended at c (tick + dispatcher operation)
    /// is on screen from the first compositor vblank at or after c + latency until the next shown frame; of several
    /// frames aiming at one vblank only the last is seen. For the shown frames: interval to the next shown frame in
    /// refreshes (I), displacement (s), speed v = s / I. "Cadence breaks" = I differs from the previous I (the eye sees
    /// a hold pattern change); "speed error" = v against the median v of its 7 neighbours (the eye sees a jump or a
    /// lag). "Judder" = a shown frame with either (|speed error| &gt; 20 %).
    /// </summary>
    internal sealed record Perceived(double Seconds, int Shown, int Hidden, int CadenceBreaks, int SpeedJudder, int Judder, List<double> SpeedErrors, Dictionary<long, int> Intervals)
    {
        public static readonly Perceived Empty = new(0, 0, 0, 0, 0, 0, [], []);

        public static Perceived From(List<Tick> all, double vblankMs, double periodMs, double latencyMs)
        {
            var glide = all.Where(k => k.Phase == "glide").ToList();
            var events = new List<(long Refresh, double H, double V)>();
            for (var i = 0; i + 1 < glide.Count; i++)
            {
                if (glide[i + 1].H == glide[i].H && glide[i + 1].V == glide[i].V) continue;
                var committed = glide[i].WallMs + (double.IsNaN(glide[i].OpMs) ? 0.5 : glide[i].OpMs);
                var refresh = (long)Math.Ceiling((committed + latencyMs - vblankMs) / periodMs);
                events.Add((refresh, glide[i + 1].H, glide[i + 1].V));
            }
            var shown = events.GroupBy(e => e.Refresh).Select(g => g.Last()).OrderBy(e => e.Refresh).ToList();
            if (shown.Count < 3) return Empty with { Shown = shown.Count };
            var intervals = new List<long>();
            var speeds = new List<double>();
            for (var j = 0; j + 1 < shown.Count; j++)
            {
                var interval = shown[j + 1].Refresh - shown[j].Refresh;
                var step = Math.Sqrt(Math.Pow(shown[j + 1].H - shown[j].H, 2) + Math.Pow(shown[j + 1].V - shown[j].V, 2));
                intervals.Add(interval);
                speeds.Add(step / interval);
            }
            var breaks = 0;
            for (var j = 1; j < intervals.Count; j++) if (intervals[j] != intervals[j - 1]) breaks++;
            var errors = new List<double>();
            var speedJudder = 0;
            var judder = 0;
            for (var j = 0; j < speeds.Count; j++)
            {
                var broke = j > 0 && intervals[j] != intervals[j - 1];
                var jumped = false;
                if (j >= 3 && j + 3 < speeds.Count)
                {
                    var window = speeds.Skip(j - 3).Take(7).Order().ToArray();
                    var median = window[3];
                    if (median > 0.02)
                    {
                        var error = speeds[j] / median - 1;
                        errors.Add(error);
                        jumped = Math.Abs(error) > 0.2;
                        if (jumped) speedJudder++;
                    }
                }
                if (broke || jumped) judder++;
            }
            var seconds = (shown[^1].Refresh - shown[0].Refresh) * periodMs / 1000;
            return new Perceived(seconds, shown.Count, events.Count - shown.Count, breaks, speedJudder, judder, errors,
                intervals.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count()));
        }

        public static Perceived Combine(IReadOnlyList<Perceived> list)
        {
            var intervals = new Dictionary<long, int>();
            foreach (var p in list)
                foreach (var (k, v) in p.Intervals) intervals[k] = intervals.GetValueOrDefault(k) + v;
            return new Perceived(list.Sum(p => p.Seconds), list.Sum(p => p.Shown), list.Sum(p => p.Hidden), list.Sum(p => p.CadenceBreaks),
                list.Sum(p => p.SpeedJudder), list.Sum(p => p.Judder), list.SelectMany(p => p.SpeedErrors).ToList(), intervals);
        }

        public override string ToString()
        {
            var s = Seconds > 0 ? Seconds : double.NaN;
            var rms = SpeedErrors.Count > 0 ? Math.Sqrt(SpeedErrors.Average(e => e * e)) : 0;
            var total = Intervals.Values.Sum();
            var hist = string.Join(" ", Intervals.OrderBy(p => p.Key).Where(p => p.Value * 100 >= total).Select(p => $"{p.Key}:{100.0 * p.Value / total:0}%"));
            return string.Create(CultureInfo.InvariantCulture,
                $"shown {Shown / s:0.0} fps (+{Hidden} unseen); intervals(refreshes) {hist}; cadence breaks {CadenceBreaks / s:0.0}/s; " +
                $"speed err rms {rms:0.000}, >20% {SpeedJudder / s:0.0}/s; JUDDER {Judder / s:0.0}/s");
        }
    }

    private static async Task Frames(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler handler = null!;
            handler = (_, _) => { CompositionTarget.Rendering -= handler; tcs.TrySetResult(); };
            CompositionTarget.Rendering += handler;
            await tcs.Task;
        }
    }

    internal readonly record struct Tick(string Phase, double WallMs, double RenderingMs, double H, double V, int Gen0, int Gen2, int Moves, double LayoutEndMs)
    {
        public double OpMs { get; init; } = double.NaN;
    }

    /// <summary>
    /// Per-phase numbers. A "frame" is a tick with a new RenderingTime. The step applied in frame k is the offset
    /// change between the offsets read at frame k and at frame k+1 (the tick reads what the previous frame laid out).
    /// </summary>
    internal sealed record PhaseStats(
        int Ticks, int DuplicateTicks, int Frames, double DurationMs,
        List<double> WallDt, List<double> RenderDt, List<double> StepRatio, int ZeroSteps, int PacingMismatch,
        int LongGaps, List<double> UiCostMs, int Gen0, int Gen2, List<int> MovesPerFrame, double PeriodMs)
    {
        public static PhaseStats From(List<Tick> all, string phase, double periodMs)
        {
            var ticks = all.Where(k => k.Phase == phase).ToList();
            var frames = new List<(Tick Tick, int Index)>();
            var duplicates = 0;
            for (var i = 0; i < ticks.Count; i++)
            {
                if (i > 0 && ticks[i].RenderingMs == ticks[i - 1].RenderingMs) { duplicates++; continue; }
                frames.Add((ticks[i], all.IndexOf(ticks[i])));
            }
            var wallDt = new List<double>();
            var renderDt = new List<double>();
            var steps = new List<double>();
            var uiCost = new List<double>();
            var moves = new List<int>();
            var mismatch = 0;
            var longGaps = 0;
            for (var f = 1; f < frames.Count; f++)
            {
                wallDt.Add(frames[f].Tick.WallMs - frames[f - 1].Tick.WallMs);
                renderDt.Add(frames[f].Tick.RenderingMs - frames[f - 1].Tick.RenderingMs);
            }
            // step applied during frame f = offset(f+1) - offset(f); its intended elapsed time = renderDt of frame f.
            for (var f = 0; f + 1 < frames.Count; f++)
            {
                var a = frames[f].Tick;
                var b = frames[f + 1].Tick;
                steps.Add(Math.Sqrt((b.H - a.H) * (b.H - a.H) + (b.V - a.V) * (b.V - a.V)));
                if (!double.IsNaN(a.OpMs)) uiCost.Add(a.OpMs);
                moves.Add(b.Moves);
            }
            for (var i = 0; i < wallDt.Count; i++)
            {
                if (wallDt[i] > 1.5 * periodMs) longGaps++;
                if (Math.Round(wallDt[i] / periodMs) != Math.Round(renderDt[i] / periodMs)) mismatch++;
            }
            // Evenness: each step against the geometric mean of its neighbours (removes the smooth friction decay).
            var ratio = new List<double>();
            var zero = 0;
            for (var i = 1; i + 1 < steps.Count; i++)
            {
                if (steps[i] == 0) { zero++; continue; }
                var neighbours = Math.Sqrt(steps[i - 1] * steps[i + 1]);
                if (neighbours > 0.25) ratio.Add(steps[i] / neighbours);
            }
            var gen0 = ticks.Count > 0 ? ticks[^1].Gen0 - ticks[0].Gen0 : 0;
            var gen2 = ticks.Count > 0 ? ticks[^1].Gen2 - ticks[0].Gen2 : 0;
            var duration = frames.Count > 1 ? frames[^1].Tick.WallMs - frames[0].Tick.WallMs : 0;
            return new PhaseStats(ticks.Count, duplicates, frames.Count, duration, wallDt, renderDt, ratio, zero, mismatch, longGaps, uiCost, gen0, gen2, moves, periodMs);
        }

        public static PhaseStats Combine(IReadOnlyList<PhaseStats> list) => new(
            list.Sum(s => s.Ticks), list.Sum(s => s.DuplicateTicks), list.Sum(s => s.Frames), list.Sum(s => s.DurationMs),
            list.SelectMany(s => s.WallDt).ToList(), list.SelectMany(s => s.RenderDt).ToList(), list.SelectMany(s => s.StepRatio).ToList(),
            list.Sum(s => s.ZeroSteps), list.Sum(s => s.PacingMismatch), list.Sum(s => s.LongGaps), list.SelectMany(s => s.UiCostMs).ToList(),
            list.Sum(s => s.Gen0), list.Sum(s => s.Gen2), list.SelectMany(s => s.MovesPerFrame).ToList(), list.Count > 0 ? list[0].PeriodMs : 0);

        public override string ToString()
        {
            var judder = StepRatio.Count(r => Math.Abs(r - 1) > 0.25);
            var rms = StepRatio.Count > 0 ? Math.Sqrt(StepRatio.Average(r => (r - 1) * (r - 1))) : 0;
            var seconds = DurationMs / 1000;
            return string.Create(CultureInfo.InvariantCulture,
                $"frames {Frames} (+{DuplicateTicks} dup ticks) in {DurationMs:0} ms = {(seconds > 0 ? Frames / seconds : 0):0.0} fps; " +
                $"wallDt {Describe(WallDt)}; renderDt {Describe(RenderDt)}; gaps>1.5P {LongGaps}; wall/render vsync mismatch {PacingMismatch}; " +
                $"step ratio rms {rms:0.000}, judder(>25%) {judder}/{StepRatio.Count} = {(seconds > 0 ? judder / seconds : 0):0.0}/s, zero steps {ZeroSteps}; " +
                $"UI cost {Describe(UiCostMs)}; moves/frame {(MovesPerFrame.Count > 0 ? string.Join("", MovesPerFrame.GroupBy(m => m).OrderBy(g => g.Key).Select(g => $"[{g.Key}]x{g.Count()} ")) : "-")}; GC0 {Gen0} GC2 {Gen2}");
        }

        private static string Describe(List<double> values)
        {
            if (values.Count == 0) return "-";
            var sorted = values.Order().ToArray();
            var mean = values.Average();
            var sd = Math.Sqrt(values.Average(x => (x - mean) * (x - mean)));
            return string.Create(CultureInfo.InvariantCulture,
                $"mean {mean:0.00} sd {sd:0.00} p50 {sorted[sorted.Length / 2]:0.00} p95 {sorted[(int)(sorted.Length * 0.95)]:0.00} max {sorted[^1]:0.00}");
        }
    }

    /// <summary>PHOTOREVIEW_KINETIC_TIMER1=1: 1 ms system timer resolution for the whole measurement (experiment).</summary>
    private sealed class TimerResolutionScope : IDisposable
    {
        private readonly bool _active = Environment.GetEnvironmentVariable("PHOTOREVIEW_KINETIC_TIMER1") == "1";

        public TimerResolutionScope()
        {
            if (_active) _ = TimeBeginPeriod(1);
        }

        public void Dispose()
        {
            if (_active) _ = TimeEndPeriod(1);
        }

        public static uint Begin() => TimeBeginPeriod(1);

        public static void End() => _ = TimeEndPeriod(1);

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint period);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint period);
    }

    private static class DwmTiming
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct Ratio { public uint Numerator; public uint Denominator; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct TimingInfo
        {
            public uint cbSize;
            public Ratio rateRefresh;
            public ulong qpcRefreshPeriod;
            public Ratio rateCompose;
            public ulong qpcVBlank, cRefresh;
            public uint cDXRefresh;
            public ulong qpcCompose, cFrame;
            public uint cDXPresent;
            public ulong cRefreshFrame, cFrameSubmitted;
            public uint cDXPresentSubmitted;
            public ulong cFrameConfirmed;
            public uint cDXPresentConfirmed;
            public ulong cRefreshConfirmed;
            public uint cDXRefreshConfirmed;
            public ulong cFramesLate;
            public uint cFramesOutstanding;
            public ulong cFrameDisplayed, qpcFrameDisplayed, cRefreshFrameDisplayed, cFrameComplete, qpcFrameComplete;
            public ulong cFramePending, qpcFramePending, cFramesDisplayed, cFramesComplete, cFramesPending, cFramesAvailable;
            public ulong cFramesDropped, cFramesMissed, cRefreshNextDisplayed, cRefreshNextPresented, cRefreshesDisplayed;
            public ulong cRefreshesPresented, cRefreshStarted, cPixelsReceived, cPixelsDrawn, cBuffersEmpty;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref TimingInfo info);

        public static (double RefreshHz, double PeriodMs) Query()
        {
            var info = new TimingInfo { cbSize = (uint)Marshal.SizeOf<TimingInfo>() };
            if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) != 0 || info.rateRefresh.Denominator == 0) return (60, 1000.0 / 60);
            var hz = (double)info.rateRefresh.Numerator / info.rateRefresh.Denominator;
            return (hz, 1000.0 / hz);
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentScope(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
