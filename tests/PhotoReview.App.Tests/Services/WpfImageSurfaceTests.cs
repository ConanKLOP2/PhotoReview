using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.App.Tests.Services;

/// <summary>
/// AR13: <see cref="WpfImageSurface"/> forwards each controller call to the real ScrollViewer / Image pair and to the
/// <see cref="ViewerState"/>. Real elements laid out on an STA thread (no window is shown); the mouse and render-tick
/// sources are the only things not exercised for real.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class WpfImageSurfaceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static (ScrollViewer Scroll, Image Image) LaidOut(bool withSource = false)
    {
        var image = new Image { Width = 1000, Height = 800 };
        if (withSource)
        {
            // An Image without a source lays out to nothing whatever its Width says; with pixels it fills 1000 x 800.
            image.Stretch = Stretch.Fill;
            image.Source = BitmapSource.Create(10, 8, 96, 96, PixelFormats.Gray8, null, new byte[80], 10);
        }
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
        return (scroll, image);
    }

    private static WpfImageSurface Surface(ScrollViewer scroll, Image image, ViewerState? viewer = null, Func<bool>? isLoaded = null, Action? updateFitSize = null) =>
        new(scroll, image, viewer ?? new ViewerState(), isLoaded ?? (() => true), updateFitSize ?? (() => { }));

    [Fact]
    public void IsLoaded_ReadsTheWindowsStateEachTime()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();
            var loaded = false;
            var surface = Surface(scroll, image, isLoaded: () => loaded);

            Assert.False(surface.IsLoaded);
            loaded = true;
            Assert.True(surface.IsLoaded);
        });
    }

    [Fact]
    public void UpdateFitSize_RunsTheWindowsUpdateFitSizeOnce()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();
            var calls = 0;
            var surface = Surface(scroll, image, updateFitSize: () => calls++);

            surface.UpdateFitSize();

            Assert.Equal(1, calls);
        });
    }

    [Fact]
    public void Capture_CombinesTheViewerStateWithTheLiveScrollMetrics()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();
            var viewer = new ViewerState { Zoom = 2.5, Stretch = ViewerStretchMode.None, MaxImageWidth = 640, MaxImageHeight = 480 };
            var surface = Surface(scroll, image, viewer);
            surface.ScrollTo(30, 20);
            surface.UpdateLayout();

            var snapshot = surface.Capture();

            Assert.Equal(2.5, snapshot.Zoom);
            Assert.Equal(ViewerStretchMode.None, snapshot.Stretch);
            Assert.Equal(640, snapshot.MaxImageWidth);
            Assert.Equal(480, snapshot.MaxImageHeight);
            Assert.Equal(image.ActualWidth, snapshot.ActualImageWidth);
            Assert.Equal(image.ActualHeight, snapshot.ActualImageHeight);
            Assert.Equal(scroll.ExtentWidth, snapshot.ExtentWidth);
            Assert.Equal(scroll.ExtentHeight, snapshot.ExtentHeight);
            Assert.Equal(scroll.ViewportWidth, snapshot.ViewportWidth);
            Assert.Equal(scroll.ViewportHeight, snapshot.ViewportHeight);
            Assert.Equal(30, snapshot.HorizontalOffset, 3);
            Assert.Equal(20, snapshot.VerticalOffset, 3);
            Assert.Equal(Visibility.Visible, snapshot.HorizontalScrollbarVisibility);
            Assert.Equal(Visibility.Visible, snapshot.VerticalScrollbarVisibility);
        });
    }

    [Fact]
    public void Capture_WhenTheImageFitsTheViewport_ReportsHiddenScrollbars()
    {
        StaUi.Run(() =>
        {
            var image = new Image { Width = 50, Height = 40 };
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

            var snapshot = Surface(scroll, image).Capture();

            Assert.Equal(Visibility.Collapsed, snapshot.HorizontalScrollbarVisibility);
            Assert.Equal(Visibility.Collapsed, snapshot.VerticalScrollbarVisibility);
        });
    }

    [Fact]
    public void Capture_WhenOnlyTheWidthOverflows_ReportsAHorizontalButNoVerticalScrollbar()
    {
        StaUi.Run(() =>
        {
            var image = new Image { Width = 1000, Height = 40 };
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

            var snapshot = Surface(scroll, image).Capture();

            Assert.Equal(Visibility.Visible, snapshot.HorizontalScrollbarVisibility);
            Assert.Equal(Visibility.Collapsed, snapshot.VerticalScrollbarVisibility);
        });
    }

    [Fact]
    public void ScrollMetrics_AreTheScrollViewersOwn()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();
            var surface = Surface(scroll, image);
            surface.ScrollTo(15, 9);
            surface.UpdateLayout();

            Assert.Equal(scroll.HorizontalOffset, surface.HorizontalOffset);
            Assert.Equal(scroll.VerticalOffset, surface.VerticalOffset);
            Assert.Equal(scroll.ViewportWidth, surface.ViewportWidth);
            Assert.Equal(scroll.ViewportHeight, surface.ViewportHeight);
            Assert.Equal(scroll.ExtentWidth, surface.ExtentWidth);
            Assert.Equal(scroll.ExtentHeight, surface.ExtentHeight);
            Assert.Equal(15, surface.HorizontalOffset, 3);
            Assert.Equal(9, surface.VerticalOffset, 3);
            Assert.True(surface.ExtentWidth >= 1000 && surface.ExtentHeight >= 800);
        });
    }

    [Fact]
    public void DragThreshold_IsTheSystemMinimumDragDistance()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();

            Assert.Equal((SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance), Surface(scroll, image).DragThreshold);
        });
    }

    [Fact]
    public void ImageGeometry_ConvertsBetweenTheScrollViewerAndTheImageElement()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut(withSource: true);
            var surface = Surface(scroll, image);
            var unscrolled = surface.ImageOrigin;

            surface.ScrollTo(10, 5);
            surface.UpdateLayout();

            // Scrolling moves the image under the viewport by exactly the offset; ToImageElement is the inverse of that placement.
            var origin = surface.ImageOrigin;
            Assert.Equal(unscrolled.X - 10, origin.X, 3);
            Assert.Equal(unscrolled.Y - 5, origin.Y, 3);
            var local = surface.ToImageElement(new Point(100, 60));
            Assert.Equal(100 - origin.X, local.X, 3);
            Assert.Equal(60 - origin.Y, local.Y, 3);
            Assert.Equal(image.TranslatePoint(new Point(0, 0), scroll), origin);
            Assert.Equal(image.ActualWidth, surface.ImageActualWidth);
            Assert.Equal(image.ActualHeight, surface.ImageActualHeight);
            Assert.Equal(1000, surface.ImageActualWidth);
            Assert.Equal(800, surface.ImageActualHeight);
        });
    }

    [Fact]
    public void SourceSize_IsNullWithoutASource_AndTheSourcesDipSizeWithOne()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();
            var surface = Surface(scroll, image);
            Assert.Null(surface.SourceSize);

            // 64x32 pixels at 192 dpi are 32x16 device-independent units: the surface reports what WPF lays out, not pixels.
            image.Source = BitmapSource.Create(64, 32, 192, 192, PixelFormats.Gray8, null, new byte[64 * 32], 64);

            Assert.Equal((32.0, 16.0), surface.SourceSize);
        });
    }

    [Fact]
    public void SetPanCursor_IsSizeAllWhilePanningAndArrowOtherwise()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();
            var surface = Surface(scroll, image);

            surface.SetPanCursor(true);
            Assert.Same(Cursors.SizeAll, image.Cursor);

            surface.SetPanCursor(false);
            Assert.Same(Cursors.Arrow, image.Cursor);
        });
    }

    [Fact]
    public void RenderingTime_IsNullForAnEventThatIsNotARenderingTick()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();

            Assert.Null(Surface(scroll, image).RenderingTime(EventArgs.Empty));
        });
    }

    [Fact]
    public void Timestamp_IsTheStopwatchClock()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();
            var surface = Surface(scroll, image);

            var before = System.Diagnostics.Stopwatch.GetTimestamp();
            var stamp = surface.Timestamp;
            var after = System.Diagnostics.Stopwatch.GetTimestamp();

            Assert.InRange(stamp, before, after);
        });
    }

    [Fact]
    public async Task YieldToRenderAsync_CompletesAfterNormalWorkAlreadyQueued_AndBeforeBackgroundWork()
    {
        using var ui = new DispatcherThread();
        var order = new List<string>();
        WpfImageSurface? surface = null;
        await ui.Dispatcher.InvokeAsync(() =>
        {
            var (scroll, image) = LaidOut();
            surface = Surface(scroll, image);
        }).Task.WaitAsync(Bound);

        await ui.Dispatcher.InvokeAsync(async () =>
        {
            _ = ui.Dispatcher.InvokeAsync(() => order.Add("background-before"), DispatcherPriority.Background);
            var yielded = surface!.YieldToRenderAsync();
            // Runs on the dispatcher thread the moment the Render-priority operation completes the task.
            var done = yielded.ContinueWith(_ => order.Add("yield-completed"), TaskContinuationOptions.ExecuteSynchronously);
            _ = ui.Dispatcher.InvokeAsync(() => order.Add("normal-after"), DispatcherPriority.Normal);
            await yielded;
            await done;
        }).Task.Unwrap().WaitAsync(Bound);
        // ContextIdle runs after everything of Background priority or higher has drained.
        await ui.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task.WaitAsync(Bound);

        // Normal work beats Render, and Render beats Background even though the background item was queued first.
        Assert.Equal(["normal-after", "yield-completed", "background-before"], order);
    }

    [Fact]
    public void RenderingTime_IsTheRenderingTimeOfARenderTick()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();
            // RenderingEventArgs has no public constructor; WPF raises it itself on a real render tick.
            var tick = (RenderingEventArgs)Activator.CreateInstance(typeof(RenderingEventArgs),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, [TimeSpan.FromMilliseconds(1234)], null)!;

            Assert.Equal(TimeSpan.FromMilliseconds(1234), Surface(scroll, image).RenderingTime(tick));
        });
    }

    private sealed class DispatcherThread : IDisposable
    {
        private readonly Thread _thread;
        private Win32DialogGuard? _guard;

        public DispatcherThread()
        {
            using var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;
            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                _guard = Win32DialogGuard.InstallOnCurrentThread();
                ready.Set();
                Dispatcher.Run();
                _guard.Dispose();
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
            _thread.Join(Bound);
            var exception = _guard?.CreateException();
            if (exception is not null) throw exception;
        }
    }
}
