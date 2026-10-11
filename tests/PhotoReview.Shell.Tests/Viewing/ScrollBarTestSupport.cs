using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Input;
using PhotoReview.Shell.Win32.Input;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>Ghi lại lệnh tô của thanh cuộn (hộp + màu theo thứ tự) và chồng clip.</summary>
internal sealed class FillRecorder : IDrawContext
{
    public List<(RectD Rect, ColorF Color)> Rects { get; } = [];

    public List<(RectD Rect, double Radius, ColorF Color)> Rounded { get; } = [];

    public List<RectD> Clips { get; } = [];

    public int ClipDepth { get; private set; }

    public void Clear(ColorF color)
    {
    }

    public void DrawImage(IGpuImage image, RectD destination, RectD? sourcePixels, ImageInterpolation interpolation, float opacity = 1f)
    {
    }

    public void FillRectangle(RectD rect, ColorF color) => Rects.Add((rect, color));

    public void FillRoundedRectangle(RectD rect, double radius, ColorF color) => Rounded.Add((rect, radius, color));

    public void DrawRectangle(RectD rect, ColorF color, double strokeWidth)
    {
    }

    public void DrawText(ITextLayout layout, PointD origin, ColorF color)
    {
    }

    public void PushClip(RectD rect)
    {
        Clips.Add(rect);
        ClipDepth++;
    }

    public void PopClip() => ClipDepth--;

    public void PushOpacity(float opacity)
    {
    }

    public void PopOpacity()
    {
    }

    public bool TryReadPixels(out PixelBuffer pixels)
    {
        pixels = null!;
        return false;
    }
}

/// <summary>
/// Cửa sổ giả + viewport thật + <see cref="ShellScrollBars"/> thật + <see cref="ShellInput"/> thật (đăng ký SAU thanh cuộn, đúng
/// thứ tự của <c>ShellMainWindow.RegisterFeatures</c>). Ảnh mặc định 3000x2000 ở 100 % trong client 1000x600: cả hai thanh hiện,
/// viewport 990x590, thanh dọc x 990..1000, thanh ngang y 590..600. Mọi thời gian do <see cref="Now"/> (tick 1 ms) điều khiển.
/// </summary>
internal sealed class ScrollBarRig : IDisposable
{
    private readonly (double Width, double Height) _bitmap;

    public ScrollBarRig(double clientWidth = 1000, double clientHeight = 600, int imageWidth = 3000, int imageHeight = 2000,
        double dpiScale = 1.0, bool actualSize = true, bool withScrollBars = true, (int DelayMs, int IntervalMs)? repeat = null)
    {
        Settings = new AppSettings { KineticPanEnabled = false };
        Window = new FakeShellWindow { ClientSizeDip = new SizeD(clientWidth, clientHeight), DpiScale = dpiScale };
        Dispatcher = new QueueDispatcher();
        Clock = new ManualFrameClock();
        Host = new RecordingInputHost();
        Viewer = new ViewerState { DpiScale = dpiScale };
        Viewer.SetSourceSize(imageWidth, imageHeight, newImage: true);
        _bitmap = (imageWidth, imageHeight);
        Viewport = new ViewportController(Window, Dispatcher, Clock, Content, UpdateFitSize, new ViewportControllerOptions
        {
            DragMetricsPixels = () => (4, 4),
            Timestamp = () => Now,
        });
        Viewer.PropertyChanged += (_, _) => Viewport.InvalidateLayout();
        var timing = repeat ?? (500, 50);
        if (withScrollBars)
        {
            Bars = new ShellScrollBars(Window, Viewport, () => CompareVisible, () => Presses++, new ScrollBarOptions
            {
                RepeatTiming = () => timing,
                TrackMouseLeave = hwnd => TrackedLeave.Add(hwnd),
                TicksPerSecond = 1000,
            });
        }

        Input = new ShellInput(Window, Viewport, Viewer, () => Settings, Host, new ShellInputEnvironment
        {
            Modifiers = () => default,
            MessageTime = () => 1000,
            TickCount = () => 1000,
            DoubleClickTimeMs = () => 500,
            DoubleClickSizePixels = _ => (4, 4),
            WheelDevice = () => default,
        });
        Drain();
        if (actualSize)
        {
            Viewer.ZoomToActualSize();
        }
        else
        {
            Viewer.ResetFit(clientWidth, clientHeight);
        }

        Drain();
    }

    public AppSettings Settings { get; }

    public FakeShellWindow Window { get; }

    public QueueDispatcher Dispatcher { get; }

    public ManualFrameClock Clock { get; }

    public RecordingInputHost Host { get; }

    public ViewerState Viewer { get; }

    public ViewportController Viewport { get; }

    public ShellScrollBars Bars { get; } = null!;

    public ShellInput Input { get; }

    public long Now { get; set; }

    public bool CompareVisible { get; set; }

    public int Presses { get; private set; }

    public List<nint> TrackedLeave { get; } = [];

    public ScrollBarLayout Layout => Bars.CurrentLayout;

    private ViewportContent Content() => new(Viewer.Stretch, Viewer.ImageWidth, Viewer.ImageHeight, Viewer.MaxImageWidth,
        Viewer.MaxImageHeight, _bitmap.Width, _bitmap.Height, Viewer.IsFit);

    private void UpdateFitSize()
    {
        var (width, height) = Viewport.ViewportSize;
        Viewer.UpdateViewport(width, height);
    }

    public void Drain() => Dispatcher.Drain();

    public static nint Pack(int x, int y) => (nint)(((uint)(ushort)y << 16) | (ushort)x);

    /// <summary>Gửi thông điệp chuột (toạ độ PIXEL client) qua chuỗi handler đã đăng ký, rồi chạy hàng đợi.</summary>
    public (bool Handled, nint Result) Mouse(uint message, double x, double y, int keyFlags = 0)
    {
        var outcome = Window.Dispatch(message, keyFlags, Pack((int)Math.Round(x), (int)Math.Round(y)));
        Drain();
        return outcome;
    }

    public (bool Handled, nint Result) Down(double x, double y) => Mouse(WindowMessages.WmLButtonDown, x, y, 0x0001);

    public (bool Handled, nint Result) Move(double x, double y, bool leftDown = true) =>
        Mouse(WindowMessages.WmMouseMove, x, y, leftDown ? 0x0001 : 0);

    public (bool Handled, nint Result) Up(double x, double y) => Mouse(WindowMessages.WmLButtonUp, x, y);

    /// <summary>Một nhịp khung ở thời điểm <paramref name="ms"/> (tính từ 0 của <see cref="Now"/>).</summary>
    public void TickAt(long ms)
    {
        Now = ms;
        Clock.Tick(16);
        Drain();
    }

    /// <summary>Vẽ vào bản ghi và trả các lệnh.</summary>
    public FillRecorder Draw()
    {
        var recorder = new FillRecorder();
        Bars.Draw(recorder);
        return recorder;
    }

    public void Dispose()
    {
        Bars?.Dispose();
        Input.Dispose();
        Viewport.Dispose();
    }
}
