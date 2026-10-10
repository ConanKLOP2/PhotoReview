using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoReview.App;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Input;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests.Golden;

/// <summary>
/// WP-10: the real <see cref="MainWindow"/> (never shown: its content tree is laid out at an explicit client size, as
/// <c>MainWindowZoomDetailTests</c> does) plus the REAL <see cref="PointerInputController"/>/<see cref="FitViewController"/> classes
/// wired exactly as <c>MainWindow</c>'s constructor wires them (MainWindow.xaml.cs, lines "_surface = new WpfImageSurface" ..
/// "_fit = new FitViewController"), except that the surface is a <see cref="ScriptedSurface"/> (real ScrollViewer/Image geometry,
/// scripted time and mouse) so kinetic glides replay deterministically. Layout, ViewerState, zoom/pan/Fit maths are all the WPF
/// production code; only the clock and the mouse device are scripted.
/// </summary>
internal sealed class GoldenWpfView
{
    // MainWindow caches the monitor DPI in this field (read by UpdateTargetDecodeBox -> ViewerState.DpiScale). A per-monitor DPI
    // change handler (MainWindow_DpiChanged) assigns it; the recorder does the same to emulate a window on a 125/150/200 % monitor.
    private static readonly FieldInfo CachedDpiField =
        typeof(MainWindow).GetField("_cachedDpiScale", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("MainWindow._cachedDpiScale not found: the golden recorder must follow the DPI cache rename.");

    private static readonly MethodInfo UpdateFitSizeMethod =
        typeof(MainWindow).GetMethod("UpdateFitSize", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("MainWindow.UpdateFitSize not found: the golden recorder must follow the rename.");

    private static readonly JsonSerializerOptions SettingsOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly ViewportOperationVersion _version = new();
    private int _navigationCount;
    private int _imageIndex;

    public GoldenWpfView(MainWindow window)
    {
        Window = window;
        var updateFitSize = (Action)Delegate.CreateDelegate(typeof(Action), window, UpdateFitSizeMethod);
        var inner = new WpfImageSurface(Scroll, Image, Viewer, () => true, updateFitSize, displayClock: null, mouse: Mouse);
        Surface = new ScriptedSurface(inner, Relayout, Mouse);
        Pointer = new PointerInputController(Surface, Viewer, () => Window.Settings, _version,
            new PointerCommands(() => true, NextAsync: CountNext, PreviousAsync: CountPrevious, ZoomActualSize: Viewer.ZoomToActualSize,
                ApplyFitAsync: () => Fit!.ApplyFitAsync()));
        Fit = new FitViewController(Surface, Viewer, _version, Pointer.CancelPan);
        Viewer.ZoomModeChanged += (_, _) => Pointer.StopKinetic(); // MainWindow.WireViewModelEvents
    }

    public MainWindow Window { get; }
    public ScriptedMouse Mouse { get; } = new();
    public ScriptedSurface Surface { get; }
    public PointerInputController Pointer { get; }
    public FitViewController Fit { get; }
    public ViewerState Viewer => Window.ViewModel.Viewer;
    public ScrollViewer Scroll => Window.ImageScroll;
    public Image Image => Window.MainImage;
    public Size Client { get; private set; }
    public double DpiScale { get; private set; } = 1.0;
    public int NavigationCount => _navigationCount;

    /// <summary>The primary button state a move event reports (WPF: MouseEventArgs.LeftButton).</summary>
    public bool LeftButtonDown { get; set; }

    /// <summary>MainWindow's ShortcutRouter over the live settings (rebuilt by <see cref="ApplySettings"/>, as on a settings change).</summary>
    public ShortcutRouter Router { get; private set; } = new();

    public Task NextImageAsync() => CountNext();

    public Task PreviousImageAsync() => CountPrevious();

    private FrameworkElement Root => (FrameworkElement)Window.Content;

    private Task CountNext() { _navigationCount++; return Task.CompletedTask; }

    private Task CountPrevious() { _navigationCount--; return Task.CompletedTask; }

    /// <summary>Measure/Arrange the window content at <see cref="Client"/> (a never-shown window has no layout pass of its own).</summary>
    public void Relayout()
    {
        Root.Measure(Client);
        Root.Arrange(new Rect(Client));
        Root.UpdateLayout();
    }

    /// <summary>Content area (DIP) of the window: what ImageScroll fills.</summary>
    public void SetClient(double width, double height)
    {
        Client = new Size(width, height);
        Relayout();
    }

    /// <summary>
    /// The monitor DPI the window is on: root DPI of the visual tree (text/layout rounding) and MainWindow's DPI cache, then the same
    /// UpdateTargetDecodeBox call MainWindow_DpiChanged makes, so ViewerState.DpiScale follows exactly as on a real DPI change.
    /// </summary>
    public void SetDpi(double dpiScale)
    {
        DpiScale = dpiScale;
        VisualTreeHelper.SetRootDpi(Root, new DpiScale(dpiScale, dpiScale));
        CachedDpiField.SetValue(Window, dpiScale);
        UpdateFitSizeMethod.Invoke(Window, null); // UpdateFitSize -> UpdateViewport + UpdateTargetDecodeBox (writes Viewer.DpiScale)
        Relayout();
    }

    /// <summary>Applies the keys of <paramref name="settingsOverridesJson"/> to the window's live <see cref="AppSettings"/>.</summary>
    public void ApplySettings(string settingsOverridesJson)
    {
        var settings = Window.Settings;
        // Same defaults for every script: a previous script's overrides must not leak into the next one.
        var defaults = new AppSettings();
        foreach (var property in typeof(AppSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0
                && (property.PropertyType.IsValueType || property.PropertyType == typeof(string) || property.PropertyType == typeof(List<string>)))
                property.SetValue(settings, property.GetValue(defaults));
        }

        if (!string.IsNullOrWhiteSpace(settingsOverridesJson))
        {
            using var document = JsonDocument.Parse(settingsOverridesJson);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                var property = typeof(AppSettings).GetProperty(entry.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
                    ?? throw new InvalidOperationException($"Unknown AppSettings property in golden setup: {entry.Name}");
                var value = JsonSerializer.Deserialize(entry.Value.GetRawText(), property.PropertyType, SettingsOptions);
                property.SetValue(settings, value);
            }
        }

        // MainViewModel.Settings setter (Q-R41): the keyboard zoom step is pushed to the viewer when settings change.
        Viewer.ZoomStep = settings.KeyboardZoomStepPercent / 100.0;
        Viewer.ScalingQuality = settings.ScalingQuality;
        Router = new ShortcutRouter(settings);
    }

    /// <summary>Source bitmap of the displayed image: <paramref name="naturalWidth"/> x <paramref name="naturalHeight"/> DIP (96-dpi equivalent).</summary>
    public static BitmapSource CreateBitmap(double naturalWidth, double naturalHeight)
    {
        // A tiny gray bitmap whose DPI is chosen so that BitmapSource.Width/Height (pixels x 96 / dpi) are the natural DIP size:
        // layout only reads Width/Height, so a 12000 x 1000 "bitmap" needs no 48 MB of pixels.
        const int Divisor = 20;
        var pixelWidth = Math.Max(1, (int)Math.Round(naturalWidth / Divisor));
        var pixelHeight = Math.Max(1, (int)Math.Round(naturalHeight / Divisor));
        var dpiX = 96.0 * pixelWidth / naturalWidth;
        var dpiY = 96.0 * pixelHeight / naturalHeight;
        var bitmap = BitmapSource.Create(pixelWidth, pixelHeight, dpiX, dpiY, PixelFormats.Gray8, null, new byte[pixelWidth * pixelHeight], pixelWidth);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// A new image appears (ImagePresenter -> MainViewModel.NotifyCurrentImageChanged): bitmap + original size to the viewer, Fit size
    /// pushed when in Fit, then the configured initial view (<see cref="PointerInputController.ApplyInitialViewAsync"/>).
    /// </summary>
    public async Task ShowImageAsync(int sourceWidth, int sourceHeight, double? bitmapWidth = null, double? bitmapHeight = null)
    {
        _imageIndex++;
        Pointer.OnCurrentIndexChanged(_imageIndex);
        Image.Source = CreateBitmap(bitmapWidth ?? sourceWidth, bitmapHeight ?? sourceHeight);
        Viewer.SetSourceSize(sourceWidth, sourceHeight, newImage: true);
        Relayout();
        if (Viewer.IsFit) UpdateFitSizeMethod.Invoke(Window, null);
        Relayout();
        await Pointer.ApplyInitialViewAsync(Window.Settings.InitialViewMode, Window.Settings.ClickZoomPercent);
        await SettleAsync();
    }

    /// <summary>The same Fit the toolbar button runs (T89 convergence), then ends at Fit with offsets 0.</summary>
    public async Task ApplyFitAsync()
    {
        await Fit.ApplyFitAsync();
        await SettleAsync();
    }

    /// <summary>
    /// Barrier: completes after every Render-priority continuation the zoom/Fit passes chained has run (an ApplicationIdle operation
    /// only runs once the dispatcher queue is drained down to idle) -- a signal, not a delay.
    /// </summary>
    public async Task SettleAsync()
    {
        await Window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).Task;
        Relayout();
    }
}

/// <summary>Scripted pointer device for <see cref="WpfImageSurface"/>: position in ImageScroll coordinates, capture is tracked, never real.</summary>
internal sealed class ScriptedMouse : IMouseAccess
{
    public Point Position { get; set; } = new(-1, -1);

    public IInputElement? Captured { get; private set; }

    public Point GetPosition(IInputElement relativeTo) => Position;

    public void Capture(IInputElement? element) => Captured = element;
}

/// <summary>Frame arguments carrying the scripted <c>RenderingTime</c>.</summary>
internal sealed class ScriptedFrameArgs(TimeSpan renderingTime) : EventArgs
{
    public TimeSpan RenderingTime { get; } = renderingTime;
}

/// <summary>
/// <see cref="IImageSurface"/>/<see cref="IFitSurface"/> over the real <see cref="WpfImageSurface"/> (geometry, scroll, anchoring all
/// real WPF) with only these scripted: window loaded, drag threshold (SM_CXDRAG = 4 DIP at 96 dpi), mouse capture (no input desktop),
/// the per-frame callback (<see cref="Frame"/> replays <c>CompositionTarget.Rendering</c> with a fixed RenderingTime) and the clock.
/// </summary>
internal sealed class ScriptedSurface(WpfImageSurface inner, Action relayout, ScriptedMouse mouse) : IImageSurface, IFitSurface
{
    private EventHandler? _frameHandler;

    public long Ticks { get; set; }

    public bool IsLoaded => true;
    public double HorizontalOffset => inner.HorizontalOffset;
    public double VerticalOffset => inner.VerticalOffset;
    public double ViewportWidth => inner.ViewportWidth;
    public double ViewportHeight => inner.ViewportHeight;
    public double ExtentWidth => inner.ExtentWidth;
    public double ExtentHeight => inner.ExtentHeight;
    public (double Horizontal, double Vertical) DragThreshold => (4, 4);
    public void ScrollTo(double horizontal, double vertical) => inner.ScrollTo(horizontal, vertical);

    public void UpdateLayout() => relayout();

    public Task YieldToRenderAsync() => inner.YieldToRenderAsync();
    public Point ImageOrigin => inner.ImageOrigin;
    public Point ToImageElement(Point surfacePoint) => inner.ToImageElement(surfacePoint);
    public double ImageActualWidth => inner.ImageActualWidth;
    public double ImageActualHeight => inner.ImageActualHeight;
    public (double Width, double Height)? SourceSize => inner.SourceSize;
    public void CaptureMouse() { }
    public void ReleaseMouseCapture() { }
    public void SetPanCursor(bool panning) { }

    public void HookRenderFrame(EventHandler handler) => _frameHandler = (EventHandler?)Delegate.Combine(_frameHandler, handler);

    public void UnhookRenderFrame(EventHandler handler) => _frameHandler = (EventHandler?)Delegate.Remove(_frameHandler, handler);

    public TimeSpan? RenderingTime(EventArgs e) => e is ScriptedFrameArgs frame ? frame.RenderingTime : null;
    public long Timestamp => Ticks;
    public DisplayTiming? DisplayTiming => null;
    public Point? PointerPosition
    {
        get
        {
            var position = mouse.Position;
            return position.X >= 0 && position.Y >= 0 && position.X <= ViewportWidth && position.Y <= ViewportHeight ? position : null;
        }
    }

    // IFitSurface
    public (double Width, double Height) ViewportSize => inner.ViewportSize;
    public void UpdateFitSize() => inner.UpdateFitSize();
    public ViewportSnapshot Capture() => inner.Capture();
    public void ScrollHome() => inner.ScrollHome();

    /// <summary>One <c>CompositionTarget.Rendering</c> callback at <paramref name="renderingTimeMs"/> (no-op when no glide hooked the frame).</summary>
    public void Frame(double renderingTimeMs) =>
        _frameHandler?.Invoke(this, new ScriptedFrameArgs(TimeSpan.FromMilliseconds(renderingTimeMs)));
}

/// <summary>Runs a body against a fresh real <see cref="MainWindow"/> on the STA host, with an isolated data root, closing it after.</summary>
// DataRootFixture sets PHOTOREVIEW_DATA_ROOT: every user of this host is a [Collection("GlobalState")] class (TEST-09 checks the host too).
[Collection("GlobalState")]
internal static class GoldenWpfHost
{
    /// <summary>The whole grid (>= 1000 layouts and ~100 scripts) is one STA body; the host default of 30 s is for a single test.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>One fresh window per item inside a single STA body (a controller keeps state and subscribes to its viewer for life).</summary>
    public static async Task RunEachAsync<T>(IReadOnlyList<T> items, Func<GoldenWpfView, T, Task> body)
    {
        using var dataRoot = new DataRootFixture();
        var windows = new List<MainWindow>();
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                foreach (var item in items)
                {
                    var window = TestAppHost.CreateMainWindow(null, new TestHostHooks { DisablePreload = true });
                    windows.Add(window);
                    await body(new GoldenWpfView(window), item);
                    try { window.Close(); } catch (InvalidOperationException) { }
                }
            }, Timeout);
        }
        finally
        {
            var open = windows.ToList();
            if (open.Count > 0)
                await StaTestHost.RunAsync(() =>
                {
                    foreach (var window in open)
                    {
                        try { window.Close(); } catch (InvalidOperationException) { }
                    }
                    return Task.CompletedTask;
                });
        }
    }

    /// <param name="body">Runs on the STA thread against the window.</param>
    /// <param name="initialFolder">Optional folder with images: the window opens it and the body starts after the first image is presented.</param>
    public static async Task RunAsync(Func<GoldenWpfView, Task> body, string? initialFolder = null)
    {
        using var dataRoot = new DataRootFixture();
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                var presented = new List<string>();
                window = TestAppHost.CreateMainWindow(initialFolder, new TestHostHooks { DisablePreload = true, OnPresented = presented.Add });
                if (initialFolder is not null)
                    Assert.True(await StaTestHost.WaitForAsync(() => presented.Count > 0, TimeSpan.FromSeconds(20)), "The first image was never presented.");
                await body(new GoldenWpfView(window));
            }, Timeout);
        }
        finally
        {
            var opened = window;
            if (opened is not null)
                await StaTestHost.RunAsync(() =>
                {
                    try { opened.Close(); } catch (InvalidOperationException) { }
                    return Task.CompletedTask;
                });
        }
    }
}
