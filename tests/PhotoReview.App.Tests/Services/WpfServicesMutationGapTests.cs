using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Diagnostics;
using Xunit;

namespace PhotoReview.App.Tests.Services;

/// <summary>
/// Stryker gap pins for the thin WPF services (presentation sink, image surface, dark title bar, window placement).
/// Real WPF objects on an STA thread; windows are only given a native handle (EnsureHandle), never shown, except where
/// SetWindowPlacement itself shows one (run these on the hidden desktop like every UI test).
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class WpfServicesMutationGapTests
{
    // ---- WpfPresentationSink ----

    [Fact]
    public void ApplyInitialViewMode_WithAnOverride_RunsOnlyTheOverride()
    {
        StaUi.Run(() =>
        {
            var constructorCalls = 0;
            var overrideCalls = 0;
            var sink = new WpfPresentationSink(onApplyInitialViewMode: () => constructorCalls++, dispatcher: Dispatcher.CurrentDispatcher)
            {
                ApplyInitialViewModeOverride = () => overrideCalls++,
            };

            sink.ApplyInitialViewMode();

            Assert.Equal(1, overrideCalls);
            Assert.Equal(0, constructorCalls);
        });
    }

    [Fact]
    public void ApplyInitialViewMode_WithoutAnOverride_RunsTheConstructorCallback()
    {
        StaUi.Run(() =>
        {
            var constructorCalls = 0;
            var sink = new WpfPresentationSink(onApplyInitialViewMode: () => constructorCalls++, dispatcher: Dispatcher.CurrentDispatcher);

            sink.ApplyInitialViewMode();

            Assert.Equal(1, constructorCalls);
        });
    }

    [Fact]
    public void ApplyInitialViewMode_WithNeitherCallbackNorOverride_IsANoOp()
    {
        StaUi.Run(() => Assert.Null(Record.Exception(() => new WpfPresentationSink(dispatcher: Dispatcher.CurrentDispatcher).ApplyInitialViewMode())));
    }

    [Fact]
    public void TracePresented_WithATraceCallback_ForwardsToItInsteadOfTheRenderTrace()
    {
        StaUi.Run(() =>
        {
            (long Token, string Kind, long Assigned)? seen = null;
            var sink = new WpfPresentationSink(onTracePresented: (token, kind, assigned) => seen = (token, kind, assigned),
                dispatcher: Dispatcher.CurrentDispatcher);

            sink.TracePresented(7, "Preview", 123);

            Assert.Equal((7L, "Preview", 123L), seen);
        });
    }

    [Fact]
    public void OffThreadUpdate_IsLoggedOnlyOnce()
    {
        using var capture = new CapturedAppLog();
        using var ui = new DispatcherThread();
        var sink = new WpfPresentationSink(onSetStatusText: _ => { }, dispatcher: ui.Dispatcher, metrics: new ReviewMetrics());

        sink.SetStatusText("a");
        Assert.Contains("update arrived off the UI thread", capture.Text(), StringComparison.Ordinal); // the very first one is reported
        sink.SetStatusText("b");

        var text = capture.Text();
        var first = text.IndexOf("update arrived off the UI thread", StringComparison.Ordinal);
        Assert.True(first >= 0, "The first off-thread update must be logged.");
        Assert.True(text.IndexOf("update arrived off the UI thread", first + 1, StringComparison.Ordinal) < 0, "Later off-thread updates must not be logged again.");
    }

    // ---- WpfImageSurface ----

    [Fact]
    public void ViewportSize_SubtractsTheScrollViewersBorderOnEachSide()
    {
        StaUi.Run(() =>
        {
            var scroll = new ScrollViewer { Width = 400, Height = 300, BorderThickness = new Thickness(3, 5, 7, 11) };
            scroll.Measure(new Size(400, 300));
            scroll.Arrange(new Rect(0, 0, 400, 300));
            var surface = new WpfImageSurface(scroll, new Image(), new ViewerState(), () => true, () => { });

            Assert.Equal((400.0 - 3 - 7, 300.0 - 5 - 11), surface.ViewportSize);
        });
    }

    [Fact]
    public void ViewportSize_NeverGoesNegative()
    {
        StaUi.Run(() =>
        {
            var scroll = new ScrollViewer { Width = 10, Height = 10, BorderThickness = new Thickness(20) };
            scroll.Measure(new Size(10, 10));
            scroll.Arrange(new Rect(0, 0, 10, 10));
            var surface = new WpfImageSurface(scroll, new Image(), new ViewerState(), () => true, () => { });

            Assert.Equal((0.0, 0.0), surface.ViewportSize);
        });
    }

    [Fact]
    public void ScrollTo_MovesTheViewportAndScrollHome_ReturnsToTheOrigin()
    {
        StaUi.Run(() =>
        {
            var image = new Image { Width = 1000, Height = 800 };
            var scroll = new ScrollViewer
            {
                Width = 200,
                Height = 100,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = image,
            };
            scroll.Measure(new Size(200, 100));
            scroll.Arrange(new Rect(0, 0, 200, 100));
            scroll.UpdateLayout();
            var surface = new WpfImageSurface(scroll, image, new ViewerState(), () => true, () => { });

            surface.ScrollTo(120, 80);
            surface.UpdateLayout();
            Assert.Equal(120, surface.HorizontalOffset, 3);
            Assert.Equal(80, surface.VerticalOffset, 3);

            surface.ScrollHome();
            surface.UpdateLayout();
            Assert.Equal(0, surface.HorizontalOffset, 3);
            Assert.Equal(0, surface.VerticalOffset, 3);
        });
    }

    // ---- DarkTitleBarChrome ----

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    private const int DwmUseImmersiveDarkMode = 20;

    [Fact]
    public void Apply_WhenTheWindowGetsItsHandle_TurnsOnTheDarkTitleBar()
    {
        StaUi.Run(() =>
        {
            var window = new Window();
            DarkTitleBarChrome.Apply(window);

            var handle = new WindowInteropHelper(window).EnsureHandle();

            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.Equal(0, DwmGetWindowAttribute(handle, DwmUseImmersiveDarkMode, out var dark, sizeof(int)));
            Assert.Equal(1, dark);
            window.Close();
        });
    }

    // ---- WindowPlacementService ----

    private static readonly JsonSerializerOptions PlacementJson = new() { IncludeFields = true };

    private static System.Drawing.Rectangle FirstWorkArea()
    {
        var areas = (List<System.Drawing.Rectangle>)typeof(WindowPlacementService)
            .GetMethod("GetMonitorWorkAreas", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, null)!;
        Assert.NotEmpty(areas);
        return areas[0];
    }

    private static WindowPlacementService.WindowPlacement ReadPlacement(IntPtr handle)
    {
        var placement = new WindowPlacementService.WindowPlacement { Length = Marshal.SizeOf<WindowPlacementService.WindowPlacement>() };
        var ok = (bool)typeof(WindowPlacementService)
            .GetMethod("GetWindowPlacement", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [handle, placement])!;
        Assert.True(ok);
        return placement;
    }

    private static string WritePlacementFile(string directory, int left, int top, int right, int bottom)
    {
        var path = Path.Combine(directory, "placement.json");
        var placement = new WindowPlacementService.WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacementService.WindowPlacement>(),
            ShowCommand = 1,
            NormalPosition = new WindowPlacementService.Rectangle { Left = left, Top = top, Right = right, Bottom = bottom },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(placement, PlacementJson));
        return path;
    }

    private static void WithTempDirectory(Action<string> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PhotoReview_Placement_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { body(directory); }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Save_ForAWindowWithAHandle_WritesItsPlacement()
    {
        WithTempDirectory(directory => StaUi.Run(() =>
        {
            var window = new Window();
            new WindowInteropHelper(window).EnsureHandle();
            var path = Path.Combine(directory, "sub", "placement.json");

            WindowPlacementService.Save(window, path);

            Assert.True(File.Exists(path));
            var saved = JsonSerializer.Deserialize<WindowPlacementService.WindowPlacement>(File.ReadAllText(path), PlacementJson)!;
            Assert.True(saved.NormalPosition.Right > saved.NormalPosition.Left);
            Assert.True(saved.NormalPosition.Bottom > saved.NormalPosition.Top);
            Assert.Equal(1, saved.ShowCommand); // a window that was never shown reopens normally
            window.Close();
        }));
    }

    [Fact]
    public void Save_ForAWindowWithoutAHandle_WritesNothing()
    {
        WithTempDirectory(directory => StaUi.Run(() =>
        {
            var path = Path.Combine(directory, "placement.json");

            WindowPlacementService.Save(new Window(), path);

            Assert.False(File.Exists(path));
        }));
    }

    [Fact]
    public void Restore_VisiblePlacement_IsAppliedToTheWindow()
    {
        WithTempDirectory(directory => StaUi.Run(() =>
        {
            var work = FirstWorkArea();
            var path = WritePlacementFile(directory, work.Left + 100, work.Top + 100, work.Left + 600, work.Top + 450);
            var window = new Window();
            var handle = new WindowInteropHelper(window).EnsureHandle();

            WindowPlacementService.Restore(window, path);

            var applied = ReadPlacement(handle).NormalPosition;
            Assert.InRange(applied.Left, work.Left + 98, work.Left + 102);
            Assert.InRange(applied.Top, work.Top + 98, work.Top + 102);
            Assert.InRange(applied.Right - applied.Left, 498, 502);
            Assert.InRange(applied.Bottom - applied.Top, 348, 352);
            window.Close();
        }));
    }

    [Fact]
    public void Restore_PlacementOutsideEveryMonitor_IsIgnored()
    {
        WithTempDirectory(directory => StaUi.Run(() =>
        {
            var path = WritePlacementFile(directory, -30000, -30000, -29500, -29600);
            var window = new Window();
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var before = ReadPlacement(handle).NormalPosition;

            WindowPlacementService.Restore(window, path);

            var after = ReadPlacement(handle).NormalPosition;
            Assert.Equal((before.Left, before.Top, before.Right, before.Bottom), (after.Left, after.Top, after.Right, after.Bottom));
            window.Close();
        }));
    }

    [Fact]
    public void Restore_ForAWindowWithoutAHandle_DoesNotThrow()
    {
        WithTempDirectory(directory => StaUi.Run(() =>
        {
            var work = FirstWorkArea();
            var path = WritePlacementFile(directory, work.Left + 100, work.Top + 100, work.Left + 600, work.Top + 450);

            var window = new Window();

            WindowPlacementService.Restore(window, path);

            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle); // nothing to apply it to, and no handle forced into existence
        }));
    }

    /// <summary>A dedicated STA thread running a Dispatcher loop, shut down on dispose.</summary>
    private sealed class DispatcherThread : IDisposable
    {
        private readonly Thread _thread;

        public DispatcherThread()
        {
            using var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;
            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
            Dispatcher = dispatcher!;
        }

        public Dispatcher Dispatcher { get; }

        public void Dispose()
        {
            Dispatcher.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
