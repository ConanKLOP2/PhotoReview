using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoReview.App;
using PhotoReview.Core.Model;
using PhotoReview.Integration.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// F11 hitch measurement: records, for every layout/render step of a fullscreen toggle, the window state, window size,
/// ImageScroll viewport, MainImage size and the Fit limits, and asserts the image never renders larger than its final Fit
/// size. A real MainWindow is Show()n (maximized or normal) on the test desktop; frames are observed via SizeChanged,
/// CompositionTarget.Rendering and the dispatcher priorities between them. It cannot prove what DWM composites on a
/// real monitor (restore/maximize animation); it proves every WPF layout pass and every WPF render tick.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class FullscreenTransitionFrameTests(ITestOutputHelper output)
{
    private const double Aspect = 1.5; // the generated 6000x4000 images
    private const double Tolerance = 0.01;
    // Layout rounding (DPI snapping of the Image height) moves a Fit size by a fraction of a DIP on some screens (a 1024x768 CI runner
    // gave 682.4 vs 682.67); a stale frame differs by tens of DIPs, so one DIP still separates them.
    private const double FitTolerance = 1.0;
    private static readonly TimeSpan PresentTimeout = TimeSpan.FromSeconds(20);

    public enum Start { Maximized, Normal }

    private sealed record Step(double T, string Tag, WindowState State, double WinW, double WinH, double ScrW, double ScrH,
        double ImgW, double ImgH, double MaxW, double MaxH, double VmMaxW, double VmMaxH, string Bars, double Opacity);

    private sealed class Probe : IDisposable
    {
        private readonly MainWindow _w;
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public List<Step> Steps { get; } = [];
        public int Frames { get; private set; }

        public Probe(MainWindow w)
        {
            _w = w;
            w.SizeChanged += OnWin;
            w.ImageScroll.SizeChanged += OnScroll;
            w.MainImage.SizeChanged += OnImage;
            w.ViewModel.Viewer.PropertyChanged += OnViewer;
            CompositionTarget.Rendering += OnRender;
        }

        public void Mark(string tag)
        {
            var s = _w.ImageScroll;
            var i = _w.MainImage;
            Steps.Add(new Step(Math.Round(_sw.Elapsed.TotalMilliseconds, 1), tag, _w.WindowState, _w.ActualWidth, _w.ActualHeight,
                s.ActualWidth, s.ActualHeight, i.ActualWidth, i.ActualHeight, i.MaxWidth, i.MaxHeight,
                _w.ViewModel.Viewer.MaxImageWidth, _w.ViewModel.Viewer.MaxImageHeight,
                $"{s.ComputedHorizontalScrollBarVisibility.ToString()[0]}{s.ComputedVerticalScrollBarVisibility.ToString()[0]}", s.Opacity));
        }

        private void OnWin(object? s, SizeChangedEventArgs e) => Mark("SizeChanged:window");
        private void OnScroll(object? s, SizeChangedEventArgs e) => Mark("SizeChanged:scroll");
        private void OnImage(object? s, SizeChangedEventArgs e) => Mark("SizeChanged:image");
        private void OnViewer(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(PhotoReview.App.ViewModels.ViewerState.MaxImageWidth)) Mark("Viewer.MaxImageWidth");
        }
        private void OnRender(object? s, EventArgs e) { Frames++; Mark("RENDER"); }

        public void Dispose()
        {
            _w.SizeChanged -= OnWin;
            _w.ImageScroll.SizeChanged -= OnScroll;
            _w.MainImage.SizeChanged -= OnImage;
            _w.ViewModel.Viewer.PropertyChanged -= OnViewer;
            CompositionTarget.Rendering -= OnRender;
        }
    }

    private static string WriteBigJpeg(string path, int width, int height)
    {
        var stride = width * 3;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 3;
                pixels[i] = (byte)(x * 255 / width);
                pixels[i + 1] = (byte)(y * 255 / height);
                pixels[i + 2] = (byte)((((x / 64) + (y / 64)) % 2 == 0) ? 40 : 220);
            }
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
        var enc = new JpegBitmapEncoder { QualityLevel = 80 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    /// <summary>
    /// Condition-based settle: waits (bounded) until <paramref name="done"/> holds, then until two more render ticks were
    /// observed (best effort, bounded) so any frame queued by the last layout change is captured by the probe.
    /// </summary>
    private static async Task SettleAsync(Probe probe, MainWindow window, Func<bool> done, string what)
    {
        Assert.True(await StaTestHost.WaitForAsync(done, TimeSpan.FromSeconds(15)), $"{what}: transition never settled. {Format(what, probe.Steps)}");
        var target = probe.Frames + 2;
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Tick(object? s, EventArgs e) { if (probe.Frames >= target) tcs.TrySetResult(null); }
        CompositionTarget.Rendering += Tick;
        var bound = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(3) };
        bound.Tick += (_, _) => { bound.Stop(); tcs.TrySetResult(null); };
        bound.Start();
        try
        {
            window.ImageScroll.InvalidateVisual(); // guarantee a composition tick even if nothing else is dirty
            await tcs.Task;
        }
        finally { bound.Stop(); CompositionTarget.Rendering -= Tick; }
    }

    private static bool Converged(MainWindow w, bool fullscreen, WindowState state)
    {
        var v = w.ViewModel.Viewer;
        var s = w.ImageScroll;
        var fitW = Math.Min(s.ActualWidth, s.ActualHeight * Aspect);
        return w.WindowStyle == (fullscreen ? WindowStyle.None : WindowStyle.SingleBorderWindow)
            && w.WindowState == state
            && Math.Abs(v.MaxImageWidth - s.ActualWidth) < Tolerance && Math.Abs(v.MaxImageHeight - s.ActualHeight) < Tolerance
            && Math.Abs(w.MainImage.ActualWidth - fitW) < Tolerance;
    }

    private static async Task<(List<Step> Enter, List<Step> Exit)> RunAsync(Start start)
    {
        using var dataRoot = new DataRootFixture();
        using var folder = new TempRoot("f11-frames");
        WriteBigJpeg(Path.Combine(folder.Path, "a.jpg"), 6000, 4000);
        WriteBigJpeg(Path.Combine(folder.Path, "b.jpg"), 6000, 4000);
        var presented = new List<string>();
        MainWindow? window = null;
        List<Step> enter = [], exit = [];
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                window = TestAppHost.CreateMainWindow(folder.Path, new TestHostHooks { OnPresented = presented.Add, DisablePreload = true });
                window.Settings.InitialViewMode = InitialViewMode.Fit;
                if (start == Start.Maximized) window.WindowState = WindowState.Maximized;
                else { window.Width = 1200; window.Height = 800; window.WindowState = WindowState.Normal; }
                window.Show();
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, PresentTimeout), "never presented");
                using var probe = new Probe(window);
                await SettleAsync(probe, window, () => Converged(window, false, start == Start.Maximized ? WindowState.Maximized : WindowState.Normal), "initial");

                var viewer = window.ViewModel.Viewer;
                Assert.True(viewer.IsFit);

                probe.Steps.Clear();
                probe.Mark("BEFORE-ENTER");
                window.ViewModel.ToggleFullscreen();
                probe.Mark("AFTER-TOGGLE-SYNC");
                await SettleAsync(probe, window, () => Converged(window, true, WindowState.Maximized), "enter");
                probe.Mark("SETTLED-ENTER");
                enter = [.. probe.Steps];

                probe.Steps.Clear();
                probe.Mark("BEFORE-EXIT");
                window.ViewModel.ToggleFullscreen();
                probe.Mark("AFTER-TOGGLE-SYNC");
                await SettleAsync(probe, window, () => Converged(window, false, start == Start.Maximized ? WindowState.Maximized : WindowState.Normal), "exit");
                probe.Mark("SETTLED-EXIT");
                exit = [.. probe.Steps];
            }, TimeSpan.FromSeconds(120));
        }
        finally
        {
            var w = window;
            if (w is not null)
                await StaTestHost.RunAsync(() => { try { w.Close(); } catch (InvalidOperationException) { } return Task.CompletedTask; });
        }
        return (enter, exit);
    }

    private static string Format(string title, List<Step> steps)
    {
        var sb = new StringBuilder().AppendLine("== " + title);
        foreach (var s in steps)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{s.T,8:0.0} {s.Tag,-22} {s.State,-9} win={s.WinW:0}x{s.WinH:0} scroll={s.ScrW:0}x{s.ScrH:0} img={s.ImgW:0.#}x{s.ImgH:0.#} imgMax={s.MaxW:0.#}x{s.MaxH:0.#} vmMax={s.VmMaxW:0.#}x{s.VmMaxH:0.#} bars={s.Bars} opacity={s.Opacity:0.#}"));
        return sb.ToString();
    }

    /// <summary>
    /// Every frame the compositor was given (a RENDER tick with ImageScroll visible) must show the image at exactly the Fit size
    /// of ITS OWN viewport (no scrollbars, nothing larger than the viewport, nothing stale), and never larger than the final
    /// Fit size of the transition.
    /// </summary>
    private static void AssertNoStaleVisibleFrame(string what, List<Step> steps)
    {
        var final = steps[^1];
        var finalW = final.ImgW;
        var finalH = final.ImgH;
        Assert.True(finalW > 1 && finalH > 1, $"{what}: final image has no size");
        var visible = steps.Where(s => s.Tag == "RENDER" && s.Opacity > 0).ToList();
        Assert.NotEmpty(visible);
        foreach (var s in visible)
        {
            var fitW = Math.Min(s.ScrW, s.ScrH * Aspect);
            var fitH = fitW / Aspect;
            Assert.True(s.ImgW <= finalW + FitTolerance && s.ImgH <= finalH + FitTolerance,
                $"{what}: frame at {s.T} ms shows {s.ImgW}x{s.ImgH}, larger than the final Fit {finalW}x{finalH}. {Format(what, steps)}");
            Assert.True(Math.Abs(s.ImgW - fitW) <= FitTolerance && Math.Abs(s.ImgH - fitH) <= FitTolerance,
                $"{what}: frame at {s.T} ms shows {s.ImgW}x{s.ImgH} in a {s.ScrW}x{s.ScrH} viewport (Fit would be {fitW}x{fitH}). {Format(what, steps)}");
        }
        Assert.Equal(1.0, final.Opacity); // never left hidden
    }

    [Theory]
    [InlineData(Start.Maximized)]
    [InlineData(Start.Normal)]
    public async Task FullscreenToggle_NeverRendersAStaleImageFrame(Start start)
    {
        var (enter, exit) = await RunAsync(start);
        var text = Format($"enter from {start}", enter) + Format($"exit to {start}", exit);
        output.WriteLine(text);
        AssertNoStaleVisibleFrame($"enter from {start}", enter);
        AssertNoStaleVisibleFrame($"exit to {start}", exit);
    }
}
