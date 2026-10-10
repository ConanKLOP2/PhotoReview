using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PhotoReview.App.Composition;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.App.Tests.Services;

/// <summary>Stryker gap pins (round 3) for the small WPF services and formatters. See docs/MUTATION-TESTING.md.</summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class AppRound3GapTests
{
    private sealed class FakeClock(DisplayTiming? timing) : IDisplayClock
    {
        public List<IntPtr> Asked { get; } = [];

        public DisplayTiming? GetTiming(IntPtr window)
        {
            Asked.Add(window);
            return timing;
        }
    }

    private sealed class FakeMouse : IMouseAccess
    {
        public Func<IInputElement, Point> Position { get; set; } = _ => default;
        public IInputElement? Captured { get; set; }
        public int CaptureCalls { get; private set; }

        public Point GetPosition(IInputElement relativeTo) => Position(relativeTo);

        public void Capture(IInputElement? element)
        {
            CaptureCalls++;
            Captured = element;
        }
    }

    private static (ScrollViewer Scroll, Image Image) LaidOut()
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
        return (scroll, image);
    }

    // ---- WpfImageSurface.PointerPosition ----

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(10, 20, true)]
    [InlineData(-1, 20, false)]
    [InlineData(10, -1, false)]
    [InlineData(-1, -1, false)]
    public void PointerPosition_IsOnlyReportedInsideTheViewportsTopLeftEdges(double x, double y, bool inside)
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();
            var surface = new WpfImageSurface(scroll, image, new ViewerState(), () => true, () => { }, mouse: new FakeMouse { Position = _ => new Point(x, y) });

            Assert.Equal(inside ? new PhotoReview.App.Input.PointD(x, y) : null, surface.PointerPosition);
        });
    }

    [Fact]
    public void PointerPosition_IncludesTheViewportsFarEdgesAndExcludesWhatIsBeyond()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();
            Assert.True(scroll.ViewportWidth > 0 && scroll.ViewportHeight > 0);
            Point current = default;
            var surface = new WpfImageSurface(scroll, image, new ViewerState(), () => true, () => { }, mouse: new FakeMouse { Position = _ => current });

            current = new Point(scroll.ViewportWidth, scroll.ViewportHeight);
            Assert.Equal(new PhotoReview.App.Input.PointD(current.X, current.Y), surface.PointerPosition);

            current = new Point(scroll.ViewportWidth + 0.5, 1);
            Assert.Null(surface.PointerPosition);

            current = new Point(1, scroll.ViewportHeight + 0.5);
            Assert.Null(surface.PointerPosition);
        });
    }

    [Fact]
    public void PointerPosition_AsksForThePositionRelativeToTheScrollViewer()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();
            IInputElement? asked = null;
            var surface = new WpfImageSurface(scroll, image, new ViewerState(), () => true, () => { }, mouse: new FakeMouse
            {
                Position = element =>
                {
                    asked = element;
                    return new Point(1, 1);
                },
            });

            _ = surface.PointerPosition;

            Assert.Same(scroll, asked);
        });
    }

    // ---- WpfImageSurface.DisplayTiming ----

    [Fact]
    public void DisplayTiming_WithoutAClock_IsNull()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();
            var window = new Window { Content = scroll };
            new WindowInteropHelper(window).EnsureHandle();

            Assert.Null(new WpfImageSurface(scroll, image, new ViewerState(), () => true, () => { }).DisplayTiming);
        });
    }

    [Fact]
    public void DisplayTiming_ForAnElementOutsideAnyWindow_IsNullWithoutAskingTheClock()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();
            var clock = new FakeClock(new DisplayTiming(1, 2));

            var timing = new WpfImageSurface(scroll, image, new ViewerState(), () => true, () => { }, clock).DisplayTiming;

            Assert.Null(timing);
            Assert.Empty(clock.Asked);
        });
    }

    [Fact]
    public void DisplayTiming_ForAnElementInAWindow_AsksTheClockForThatWindowsHandle()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();
            var window = new Window { Content = scroll, Width = 300, Height = 300, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.Show();
            var handle = new WindowInteropHelper(window).Handle;
            var clock = new FakeClock(new DisplayTiming(11, 22));

            var timing = new WpfImageSurface(scroll, image, new ViewerState(), () => true, () => { }, clock).DisplayTiming;

            Assert.Equal(new DisplayTiming(11, 22), timing);
            Assert.Equal([handle], clock.Asked);
        });
    }

    // ---- WpfImageSurface.ReleaseMouseCapture ----

    [Fact]
    public void ReleaseMouseCapture_ReleasesTheImagesCapture_ButNeverAnotherElements()
    {
        StaUi.Run(() =>
        {
            var (scroll, image) = LaidOut();
            var other = new Border();
            var mouse = new FakeMouse { Captured = image };
            var surface = new WpfImageSurface(scroll, image, new ViewerState(), () => true, () => { }, mouse: mouse);

            surface.ReleaseMouseCapture();
            Assert.Null(mouse.Captured);
            Assert.Equal(1, mouse.CaptureCalls);

            mouse.Captured = other;
            surface.ReleaseMouseCapture();
            Assert.Same(other, mouse.Captured);
            Assert.Equal(1, mouse.CaptureCalls);
        });
    }

    // ---- ambient dispatcher seams ----

    [Fact]
    public void AmbientDispatcher_IsTheApplicationsDispatcherWhenThereIsOne()
    {
        Assert.Equal(Application.Current?.Dispatcher, AmbientDispatcher.Application());
    }

    [Fact]
    public async Task DispatcherUiScheduler_WithoutADispatcher_UsesTheApplicationDispatcherBeforeTheThreadsOwn()
    {
        var done = new ManualResetEventSlim();
        Dispatcher? appDispatcher = null;
        var thread = new Thread(() =>
        {
            appDispatcher = Dispatcher.CurrentDispatcher;
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        SpinWait.SpinUntil(() => appDispatcher is not null, TimeSpan.FromSeconds(10));
        try
        {
            var scheduler = new DispatcherUiScheduler(applicationDispatcher: () => appDispatcher);
            var ranOn = -1;

            await scheduler.InvokeAsync(() => { ranOn = Environment.CurrentManagedThreadId; done.Set(); }).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(done.IsSet);
            Assert.Equal(thread.ManagedThreadId, ranOn);
        }
        finally
        {
            appDispatcher!.InvokeShutdown();
        }
    }

    [Fact]
    public void DispatcherUiScheduler_WithoutAnyApplication_UsesTheCreatingThreadsDispatcher()
    {
        StaUi.Run(() =>
        {
            var scheduler = new DispatcherUiScheduler(applicationDispatcher: () => null);
            var ran = false;

            var task = scheduler.InvokeAsync(() => ran = true);
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);

            Assert.True(ran);
        });
    }

    [Fact]
    public void WpfPresentationSink_WithoutADispatcher_MarshalsThroughTheApplicationDispatcher()
    {
        Dispatcher? appDispatcher = null;
        var thread = new Thread(() =>
        {
            appDispatcher = Dispatcher.CurrentDispatcher;
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        SpinWait.SpinUntil(() => appDispatcher is not null, TimeSpan.FromSeconds(10));
        try
        {
            var ranOn = -1;
            var metrics = new ReviewMetrics();
            var sink = new WpfPresentationSink(onSetStatusText: _ => ranOn = Environment.CurrentManagedThreadId,
                metrics: metrics, applicationDispatcher: () => appDispatcher);

            sink.SetStatusText("x"); // called from this (non-UI) thread

            Assert.Equal(thread.ManagedThreadId, ranOn);
            Assert.Equal(1, metrics.Snapshot().CrossThreadPresentCount);
        }
        finally
        {
            appDispatcher!.InvokeShutdown();
        }
    }

    [Fact]
    public void WpfPresentationSink_WithoutAnyDispatcher_RunsTheUpdateInline()
    {
        var ranOn = -1;
        var sink = new WpfPresentationSink(onSetStatusText: _ => ranOn = Environment.CurrentManagedThreadId, applicationDispatcher: () => null);

        sink.SetStatusText("x");

        Assert.Equal(Environment.CurrentManagedThreadId, ranOn);
    }

    // ---- StartupWarmup ----

    [Fact]
    public void ConnectRenderThread_CreatesTheMediaContextWithoutThrowing()
    {
        StaUi.Run(() => Assert.Null(Record.Exception(StartupWarmup.ConnectRenderThread)));
    }
}

/// <summary>Stryker gap pins (round 3) that need no WPF.</summary>
[Trait("Category", "HotPath")]
public sealed class AppRound3FormatterGapTests
{
    [Fact]
    public void SiblingFolderBoundary_ZeroIsTheFirstFolderMessage()
    {
        Assert.Equal(PhotoReview.Core.Localization.Tr.StatusSiblingFolderFirst, StatusFormatter.SiblingFolderBoundary(0));
        Assert.Equal(PhotoReview.Core.Localization.Tr.StatusSiblingFolderFirst, StatusFormatter.SiblingFolderBoundary(-1));
        Assert.Equal(PhotoReview.Core.Localization.Tr.StatusSiblingFolderLast, StatusFormatter.SiblingFolderBoundary(1));
    }

    [Fact]
    public void FormatDateTime_WhenTheProvidersCalendarCannotRepresentTheDate_FallsBackToInvariant()
    {
        var japanese = new CultureInfo("ja-JP");
        japanese.DateTimeFormat.Calendar = new JapaneseCalendar(); // starts in 1868: an earlier date cannot be formatted
        var date = new DateTime(1800, 1, 1, 12, 30, 0);

        var text = ExifFormatter.FormatDateTime(date, japanese);

        Assert.Equal(date.ToString("g", CultureInfo.InvariantCulture), text);
    }

    private static bool NoLibRaw(out string? reason)
    {
        reason = "libraw.dll not found";
        return false;
    }

    private static bool TurboJpegPresent(out string? reason)
    {
        reason = null;
        return true;
    }

    [Fact]
    public void DecoderProviders_RegistersTurboJpegWhenItsProbeSucceeds()
    {
        var providers = DecoderProviders.Create(PhysicalSourceReader.Instance, () => false, NoLibRaw, TurboJpegPresent, (_, _) => { }, _ => { });

        Assert.Contains(providers, p => p.Item1 == DecoderBackend.TurboJpeg);
    }

    // ---- compare: hash decisions ----

    private static readonly (string Left, string Right) RawAndJpeg = (@"C:\p\a.jpg", @"C:\p\a.cr2");

    [Fact]
    public async Task Compare_RawAgainstJpeg_WithHashingOff_NeverClaimsTheyDiffer()
    {
        var vm = new CompareViewModel();

        await vm.LoadAsync(RawAndJpeg, token: 1, isTokenCurrent: _ => true,
            loadImageAsync: _ => Task.FromResult<object?>(new object()),
            getHashAsync: _ => Task.FromResult("h"), compareHashEnabled: false);

        Assert.Equal(StatusFormatter.CompareHashText(null), vm.HashText);
    }

    [Fact]
    public async Task Compare_RawAgainstJpeg_WithoutAHashFunction_NeverClaimsTheyDiffer()
    {
        var vm = new CompareViewModel();

        await vm.LoadAsync(RawAndJpeg, token: 1, isTokenCurrent: _ => true,
            loadImageAsync: _ => Task.FromResult<object?>(new object()),
            getHashAsync: null, compareHashEnabled: true);

        Assert.Equal(StatusFormatter.CompareHashText(null), vm.HashText);
    }

    [Fact]
    public async Task Compare_RawAgainstJpeg_WithHashingOn_SaysTheyDifferWithoutHashing()
    {
        var vm = new CompareViewModel();
        var hashed = 0;

        await vm.LoadAsync(RawAndJpeg, token: 1, isTokenCurrent: _ => true,
            loadImageAsync: _ => Task.FromResult<object?>(new object()),
            getHashAsync: _ => { hashed++; return Task.FromResult("h"); }, compareHashEnabled: true);

        Assert.Equal(StatusFormatter.CompareHashText(false), vm.HashText);
        Assert.Equal(0, hashed);
    }

    [Fact]
    public async Task Compare_WhenOneHashThrows_ShowsTheHashAsUnknownAndStillLoads()
    {
        var vm = new CompareViewModel();

        var ok = await vm.LoadAsync((@"C:\p\a.jpg", @"C:\p\b.jpg"), token: 1, isTokenCurrent: _ => true,
            loadImageAsync: _ => Task.FromResult<object?>(new object()),
            getHashAsync: path => path.EndsWith("b.jpg", StringComparison.Ordinal)
                ? throw new System.IO.IOException("locked")
                : Task.FromResult("h"),
            compareHashEnabled: true);

        Assert.True(ok);
        Assert.Equal(StatusFormatter.CompareHashUnknown(), vm.HashText);
    }

    [Theory]
    [InlineData(typeof(System.IO.IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(ArgumentException))]
    public void Compare_TryGetFileSize_SwallowsTheExpectedStatFailures(Type exceptionType)
    {
        Assert.Null(CompareViewModel.TryGetFileSize(@"C:\p\a.jpg", _ => throw (Exception)Activator.CreateInstance(exceptionType, "nope")!));
    }

    [Fact]
    public void Compare_TryGetFileSize_DoesNotHideABug_AndReturnsTheLength()
    {
        Assert.Throws<InvalidOperationException>(() => CompareViewModel.TryGetFileSize(@"C:\p\a.jpg", _ => throw new InvalidOperationException("bug")));
        Assert.Equal(42, CompareViewModel.TryGetFileSize(@"C:\p\a.jpg", _ => 42L));
        Assert.Null(CompareViewModel.TryGetFileSize(Path.Combine(Path.GetTempPath(), "PhotoReview_missing_" + Guid.NewGuid().ToString("N"))));
    }
}
